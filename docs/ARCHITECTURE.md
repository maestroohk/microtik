# Kibo architecture

Kibo is a multi-tenant hotspot and ISP management platform for MikroTik networks. This document records the
decisions a new developer needs; the code itself carries no explanatory comments by design.

## Shape of the system

```
 phone ──WiFi──► MikroTik hotspot ──redirect──► Kibo captive portal (/p/{site})
                     │   ▲                               │
          RADIUS auth│   │Access-Accept (limits)         │ payment request / status
          + accounting   │                               ▼
                     ▼   │                     Kibo web app (ASP.NET Core 10)
                 FreeRADIUS 3.2 ──SQL──► PostgreSQL ◄──EF Core── services + workers
                                              ▲                    │
                                              └── RouterOS REST ◄──┘ (monitoring, disconnect)
```

* **One deployable web app** (`src/Kibo.Web`): Blazor Server admin UI, static server-rendered captive portal,
  minimal API endpoints (payment callbacks, RFC 8908 captive API), and background workers. Folders under
  `Features/` are the module boundaries (Routers, Customers, Packages, Subscriptions, Payments, Vouchers, Portal,
  Sessions, Platform, Reports). No microservices.
* **FreeRADIUS** speaks RADIUS. Kibo never implements the RADIUS server.
* **PostgreSQL** is the single source of truth. Chosen over MySQL for partial unique indexes (payment
  idempotency), row locks with `SKIP LOCKED` (job queue), `xmin` optimistic concurrency, PL/pgSQL for the RADIUS
  decision function, and mature `rlm_sql_postgresql` support.
* **Kibo.Lab** (`src/Kibo.Lab`) is a development-only simulator of a RouterOS hotspot (REST API subset, hotspot
  login, real RADIUS client with PAP/CHAP, accounting, Disconnect-Request listener) and of a mobile money
  provider. It never claims to verify real RouterOS behaviour.

## Key decisions

### Who decides whether a customer may connect
Business lifecycle (which subscription is current, activation, expiry, refunds) lives in C#
(`Features/Subscriptions`). The C# side writes one **projection row per login** into `radius.access`
(`RadiusProvisioner.SyncAccountAsync`). FreeRADIUS calls `radius.check_items` / `radius.reply_items`
(`Data/Sql/radius_functions.sql`), which compute at login time:

* the tenant, from the **packet source address** (`radius.nas`), so a username only exists inside the router
  owner's tenant;
* remaining validity → `Session-Timeout`; remaining bytes → `Mikrotik-Total-Limit` (+ `-Gigawords`);
  speed → `Mikrotik-Rate-Limit` (`upload/download`); devices → `Simultaneous-Use`; night-window end;
* fair-use: once the data allowance is used, the throttled rate is returned instead of a reject.

Consequence: **logins and expiry keep working while the web app is down** (FreeRADIUS + PostgreSQL only).
Purchases need the app. If PostgreSQL is down FreeRADIUS rejects ("Service temporarily unavailable"); routers keep
existing sessions and their own timers.

### Time and quota semantics (never interchangeable)
| Concept | Field | Meaning |
|---|---|---|
| Validity | `ValidityAmount` + `ValidityUnit` | Calendar time. Keeps running while offline. Months use calendar months. |
| Validity start | `ValidityStart` | `OnPurchase`, or `OnFirstLogin` = first accounting start after the subscription became current. |
| Activation deadline | `ActivateBy` | An unused first-login purchase or voucher expires after this. |
| Online time | `TimeQuotaSeconds` | Sum of session time only. |
| Single session cap | `MaxSessionSeconds` | Longest continuous session; customer re-authenticates. |
| Data | `DataQuotaBytes` | Upload + download bytes from accounting (gigawords included). |
| Grace | `GraceSeconds` | Access continues after expiry (PPPoE renewals). |
| Access window | `AccessWindowStart/End` | Local time of day (tenant time zone); can cross midnight. |

Package terms are **copied onto the subscription at purchase**; editing a package never changes what was sold.
A purchase while another package is current is **queued** (`Scheduled`) and becomes current when the first one ends.

### Usage and accounting correctness
Every accounting packet goes through one PL/pgSQL function, `radius.account(...)`
(`Data/Sql/radius_accounting.sql`, `SECURITY DEFINER`; the FreeRADIUS role only has EXECUTE). Rules:

* **Identity is server-side.** Tenant and router come from the packet's source address (`radius.nas`). The
  subscription comes from `Class` only if that subscription belongs to the same tenant and username; otherwise
  from the account's current projection row. Usage is attributed by `subscription_id`, never by MAC, IP or time window.
* **The router clock is never trusted.** Start, update and stop times are the server's receive time; the router's
  `Event-Timestamp` is kept only as evidence (`nas_event_time`). A router with a wrong clock cannot hide usage.
* **Counters are cumulative per session and only move up.** Each session row stores the highest
  input/output totals seen (`gigawords << 32 + octets`); subscription usage is the sum of session maxima, recomputed
  (never incremented), so duplicates, retransmissions and reordered packets cannot double-count or reduce usage.
* **32-bit wrap without gigawords** is detected (drop of more than 2 GiB with session time still rising).
* **Session-ID reuse** (router restarted without Accounting-On and reused an ID) is detected by session time going
  backwards or a start for an old open session: the old row is closed `Session-Id-Reused`, a new row starts.
* **Missing start**: the first interim/stop creates the session. **Missing stop**: no update for 5 minutes closes it
  `Stale-Session`; if updates resume it is reopened. **Accounting-On/Off** closes all of that router's sessions.
* Packets are serialized per (router, session, user) with an advisory lock, so parallel retransmissions are safe.
* Every packet is stored in `radius.acct_events` with the decision taken (`outcome`), kept 90 days, for
  reconciliation and dispute evidence.

**Upload/download direction.** For hotspot and PPP, RouterOS reports `Acct-Input-*` as bytes received from the
customer (upload) and `Acct-Output-*` as bytes sent to them (download). Both are stored; quotas use the sum.
_Code-reviewed against RFC 2866; must be confirmed on CHR._

### Quota enforcement guarantees
* **Single session, hard cap:** exact up to RouterOS's own enforcement, because the router receives
  `Mikrotik-Total-Limit(-Gigawords)` = remaining bytes and cuts the session itself.
* **Re-login:** remaining = quota − used − *in-flight reserve*, where the reserve is
  (seconds since each open session's last update) × (that session's speed limit). Delayed accounting therefore
  cannot hand out the same bytes twice. If the reserve exhausts the allowance the login is refused (fail-closed).
* **Concurrent sessions on one subscription (several devices or routers):** RouterOS cannot enforce one byte limit
  across sessions or routers. Each new session gets `remaining / (open sessions + 1)`. Worst-case router-enforced
  total is below `used + remaining × (1 + 1/2 + … )`; the lifecycle worker then disconnects every session once the
  sum reaches the quota, within one accounting interval plus 15 s. Exact aggregate enforcement is **not** claimed.
* **Login races:** recent `Access-Accept`s that have no accounting start yet count toward `Simultaneous-Use`
  (`radius.simultaneous_sessions`), closing the window between authorization and the first accounting packet.
  A sub-second race between two FreeRADIUS threads remains possible.
* **Fair use** is a separate policy from hard caps, with two modes per package:
  * `RateChange` (default): no byte limit is sent; when accounting shows the threshold crossed, Kibo sends CoA with
    the reduced `Mikrotik-Rate-Limit`. Overshoot ≈ (accounting interval + up to 15 s) × speed. It is recorded per
    subscription (`ThrottleOvershootBytes`) and shown on the dashboard. The interval is configurable per package
    (15–90 s); shorter means more RADIUS/database load.
  * `RouterCutoff`: the remaining bytes to the threshold are sent as `Mikrotik-Total-Limit`, so the router stops the
    session exactly at the threshold; the next login gets the reduced speed. Exact, but the customer reconnects once.
* **Brute force:** 10 rejects from one device (Calling-Station-Id) at one router within 10 minutes lock that device out
  for the rest of the window, whatever username it tries.

### Payments
* Provider-independent `IPaymentProvider`. Only the simulated provider exists today.
* A callback is only a **hint**: it must carry a valid HMAC signature, and even then Kibo **re-queries the
  provider** and checks amount and currency before settling. Pending/failed/mismatched payments never grant access.
* Settlement runs under `SELECT … FOR UPDATE` on the payment row, a unique index on
  `(tenant, source, source_reference)` for subscriptions and on `(payment_id, kind)` for ledger entries.
  Duplicate callbacks, retries and concurrent workers produce exactly one subscription and one ledger entry
  (covered by `DatabaseTests`).
* `ledger_entries` is append-only (database trigger). Corrections are new entries (refunds, adjustments).
* Provider secrets come from configuration (user-secrets in development, environment variables in production).

### Router integration
* Monitoring and session control use the **RouterOS v7 REST API** (`/rest`, HTTPS). Credentials are encrypted
  with ASP.NET Data Protection.
* **A router is not trusted because someone knows its address and password.** Registration issues a one-time
  claim token; the onboarding script writes it into `/system note`. "Verify ownership" reads the note and the
  RouterOS software ID over the API. Only then is the `radius.nas` row created (until then FreeRADIUS ignores the
  router), and a software ID can belong to only one router across all tenants.
* **Provisioning is plan → approve → backup → apply → verify → roll back.** Kibo only adds items commented
  `kibo-managed` or sets the specific properties shown in the plan (RADIUS client, `radius incoming`, hotspot
  profile RADIUS/accounting/login methods, portal walled-garden host, PPP AAA, the claim note). Firewall, NAT,
  DHCP, routing, VLANs and users are never touched. A plan older than 15 minutes must be recreated. An `/export`
  backup is stored encrypted before applying. After applying, the plan is recomputed; any remaining difference
  rolls back every step in reverse. RouterOS has no transactions, so a rollback can itself fail; that state is
  reported with the backup to restore from.
* **Security audit (read-only)** flags: RADIUS not used, MAC or trial login, local hotspot users or PPP secrets
  (bypass RADIUS), `bypassed` IP bindings, open walled gardens, extra RADIUS servers, CoA disabled, FastTrack,
  global IPv6 on the hotspot interface (the hotspot only controls IPv4), clear-text or unrestricted management
  services, and RouterOS older than 7.10.
* Production routers connect over a per-router WireGuard tunnel; RADIUS, CoA and REST all use tunnel addresses.
  RouterOS listens for CoA on UDP 1700 unless configured; the port is stored per router.
* Health checks back off exponentially (30 s → 15 min) so dead routers cost little. Router-down vs platform-down
  is visible from connection events and `LastError`.

### Captive portal
* Static server-rendered pages (no Blazor circuit, no WebSocket, minimal JS) because iOS/Android captive network
  assistants have short-lived cookies and weak JavaScript. Self-hosted fonts (Google Fonts would be blocked by the
  walled garden).
* Router context (MAC, login URL, CHAP challenge) arrives in the query string from `login.html` and is kept in a
  data-protected cookie. The final login uses a **CHAP hash**, never the plain code, and the login URL must be a
  private/hotspot address (prevents a crafted link from sending credentials elsewhere).
* MAC addresses are recorded for support only; identity is phone + access code, voucher code, or payment token.
* Portal POSTs and staff sign-in are rate-limited per client IP; staff accounts lock after 5 failures.

### Tenancy and permissions
* Every tenant-owned table has `TenantId`; `AppDbContext` applies global query filters and refuses to save rows
  for another tenant (`GuardTenantOwnership`). `KiboDb.ForCurrentUser()` / `ForTenant()` / `ForSystem()` make the
  scope explicit. Workers use `ForSystem()` deliberately.
* Roles map to permission strings in `Security/Permissions.cs`. Services check permissions themselves, so UI
  mistakes cannot bypass them.

### Background work
* `RouterMonitorWorker` (10 s tick, per-router schedule), `SubscriptionLifecycleWorker` (15 s: activation, usage,
  expiry, voucher redemption, stale sessions), `JobWorker` (durable `background_jobs` table, `SKIP LOCKED`,
  bounded retries with backoff, deduplication key). All survive restarts because state is in the database.

### Mid-session changes
`DynamicAuthorizationClient` sends RFC 5176 CoA and Disconnect requests to the router's tunnel address and stored
CoA port, signed with that router's RADIUS secret. Fair-use throttling uses CoA (`Mikrotik-Rate-Limit`); if the
router does not acknowledge, Kibo disconnects so the next login gets the throttled rate. Disconnects use the
RouterOS API first and fall back to RADIUS Disconnect-Request. Packages with a fair-use speed are not sent a
`Mikrotik-Total-Limit`, otherwise the router would cut the customer off instead of slowing them down.

## Deployment
`deploy/docker-compose.prod.yml`: PostgreSQL, FreeRADIUS (sharing the WireGuard container's network namespace, so
RADIUS is only reachable through router tunnels), the web app image (`src/Kibo.Web/Dockerfile`, Tailwind built
in a Node stage), and Caddy for automatic TLS. Data-protection keys live in a volume; losing them makes stored
router API passwords unreadable (re-enter them). Back up PostgreSQL daily (`pg_dump`); it holds everything.
Set `KIBO_TUNNEL_SUBNET` so FreeRADIUS only accepts dynamic clients from tunnel addresses.

## Configuration
Non-secret settings: `appsettings*.json`. Secrets: `dotnet user-secrets` (development) or environment variables.
Lab container secrets: `deploy/.env` (git-ignored; template in `deploy/.env.example`).

## Known gaps (tracked in PROGRESS.md)
Real mobile money adapters, automated provisioning, CHR verification, an untested production compose.
