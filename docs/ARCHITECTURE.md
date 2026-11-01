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
* Accounting rows are written by FreeRADIUS directly (works when the app is down). Start/interim/stop are
  upserts on `acctuniqueid` with `GREATEST(...)` so duplicate or out-of-order packets cannot reduce counters.
* Octets are stored as 64-bit: `gigawords << 32 + octets`.
* `Accounting-On/Off` closes all open sessions of that NAS (router reboot). Sessions without an update for
  5 minutes are closed as `Stale-Session` and stop counting toward `Simultaneous-Use`.
* Validity is wall-clock (`ExpiresAt`), so router reboots never reset it. Re-login receives the *remaining* time
  and bytes, so reconnecting cannot bypass a cap. Overshoot is bounded by one interim interval per device; the
  lifecycle worker disconnects all sessions once the quota is used.

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
* Kibo **never pushes configuration on its own**. It generates a reviewed onboarding script and `login.html`.
  Automated provisioning will be opt-in per router (`ProvisioningEnabled`) and tested against CHR first.
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

## Configuration
Non-secret settings: `appsettings*.json`. Secrets: `dotnet user-secrets` (development) or environment variables.
Lab container secrets: `deploy/.env` (git-ignored; template in `deploy/.env.example`).

## Known gaps (tracked in PROGRESS.md)
Real mobile money adapters, RADIUS CoA for in-session speed changes, automated provisioning, CHR verification,
PPPoE simulator coverage.
