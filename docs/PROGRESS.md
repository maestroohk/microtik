# Kibo progress

_Last updated: 2026-11-02 (production smoke test)_

## How to run it

```bash
cd deploy && cp .env.example .env   # fill every value with `openssl rand -hex 16`
docker compose up -d                  # Postgres, FreeRADIUS, two RouterOS simulators (lab network 10.247.0.0/24)
cd ../src/Kibo.Web && npm install && dotnet run --launch-profile http   # http://localhost:5290
```

Development secrets live in `dotnet user-secrets` (connection string, seed passwords, lab secrets, payment
simulator keys) — see `docs/ARCHITECTURE.md`. The simulator must be published once before `docker compose up`:
`dotnet publish src/Kibo.Lab -c Release -o lab/sim-publish`.

| Address | What |
|---|---|
| http://localhost:5290 | Operator dashboard (seeded owner `owner@kampala-connect.test`, admin `admin@kibo.test`; passwords in `deploy/.env`) |
| http://localhost:5290/p/kampala-connect-ntinda | Captive portal for the Ntinda site |
| http://localhost:58721 | Lab simulator 1 (phones, hotspot login, PPPoE dial, reboot, mobile money approval) |
| 127.0.0.1:55432 | PostgreSQL · 127.0.0.1:51812-51813/udp FreeRADIUS |

All ports bind to 127.0.0.1. The lab uses its own Docker network and does not touch host networking or routers.

## Hardening round (2026-11-02): status by evidence

Categories: **RouterOS** = verified on RouterOS CHR 7.25 in the lab · **Simulator** = verified with Kibo.Lab ·
**Integration** = automated test against PostgreSQL / the RADIUS SQL · **Unit** = automated test without a
database · **Reviewed** = code-reviewed only · **Not tested**.

### Accounting and quota
| Item | Evidence | Notes |
|---|---|---|
| `radius.account`: duplicates, reorder, gigawords, 32-bit wrap, missing start, router clock skew, session-ID reuse, delayed interim, stale reopen, Accounting-On/Off, cross-tenant Class, unknown NAS, parallel retransmissions | Integration | `AccountingTests` |
| 10,000 interim updates from 4 writers | Integration | About 1,200 updates/s on this laptop; totals exact |
| Concurrent-session grant split, in-flight reserve, pending-login device count | Integration | |
| Upload/download direction (Acct-Input = upload), `Class` echoed back, 64-bit totals | RouterOS | |
| 20 MB hard cap | RouterOS | Router cut the session itself; overshoot under 2 KB; cause `NAS-Request`; re-login rejected with Kibo's message |
| Graceful reboot: Stop (`Admin-Reboot`) then Accounting-On | RouterOS | Handled idempotently |
| Power cut: orphaned session closed by Accounting-On (`NAS-Reboot`) | RouterOS | |
| Simultaneous use: second device refused | RouterOS | |
| RADIUS outage: new logins fail clearly, live sessions continue, recovery | RouterOS | |
| Usage confidence (Final / Live / Delayed / Router unreachable) on the customer page and accounting health page | Integration (classification), browser (badge) | Missing usage is never shown as a settled zero |
| Device lockout after 10 rejects in 10 minutes | Reviewed | |
| Fair use `RateChange`: dynamic queue holds the package rate; CoA by Framed-IP changes it to the fair-use rate | RouterOS | Sent with `radclient` from the configured RADIUS address |
| Fair use: Kibo's own CoA packet reaching the router | **Not tested** | The lab forwarder changes the source address and RouterOS drops it; Kibo fell back to an API disconnect (RouterOS) and recorded 149 KB overshoot at a 30 s interval |
| Fair use `RouterCutoff` | Integration | SQL sends the threshold as `Mikrotik-Total-Limit` |

### Bypass resistance
| Item | Evidence | Notes |
|---|---|---|
| Printed access code typed into the router's own login page is refused (hidden router secret) | RouterOS | Portal login with the same code works |
| Local hotspot user bypasses RADIUS (`radius=false`) and the audit flags it | RouterOS | |
| Audit: MAC/trial login, bypassed bindings, open walled garden, extra RADIUS servers, CoA off, FastTrack, IPv6, telnet/ftp, unrestricted services, PPP secrets, old RouterOS | RouterOS (run on a real config, false positives fixed) | FastTrack and IPv6 findings come from configuration reads, not traffic tests |
| IPv6 traffic bypass, DNS tunnelling through the walled garden, FastTrack counter loss | **Not tested** | |
| Router login URL bound to the site's registered hotspot hosts | Unit | Userinfo tricks rejected |
| Portal rate limits behind hotspot NAT (per browser + per IP) | Simulator, browser | |

### Router trust and provisioning
| Item | Evidence | Notes |
|---|---|---|
| Ownership proof (claim note + software/system ID); RADIUS client created only afterwards | RouterOS | Found and fixed: RADIUS client rows were created without a tenant (now refused by `GuardTenantOwnership`; Integration test) |
| Plan, backup, apply, verify; `login.html` upload over REST | RouterOS | |
| Rollback, including a partially failed rollback reported as such | RouterOS | |
| REST quirks: Content-Length bodies, unescaped item IDs, `system-id`, `available-from` | RouterOS | All handled in `RouterOsClient` |
| `kibo-api` group can write files | RouterOS | RouterOS least-privilege limit, documented |
| PPPoE on CHR | **Not tested** | Simulator only (first round) |
| RouterOS 7.24.6 (long-term) | **Not tested** | Boots; the serial bootstrap loses characters under emulation |

### Application and deployment security
| Item | Evidence | Notes |
|---|---|---|
| RADIUS codec fuzzing (fixed an out-of-bounds read) | Unit | 20,000 packets |
| TOTP (RFC 6238 vectors, replay refusal) | Unit | |
| Platform admin needs two-step sign-in; without it, operator pages show a set-up prompt | Production container + curl | Found and fixed: a new admin got HTTP 500 on every page |
| First-admin bootstrap (`--create-platform-admin`) | Integration + production container | Weak password and a second admin are refused |
| Production image build, migrations on an empty database, `/healthz`, Caddy TLS (internal CA on localhost), HTTP to HTTPS redirect, CSP/frame headers, secure cookies | Production compose (localhost only) | Fixed: `Kibo.Radius` missing from the image, no `.dockerignore`, health check relied on `wget` |
| Session survives a web restart and an image rebuild (persistent data-protection keys) | Production compose | |
| Graceful shutdown | Production compose | Exit code 0 |
| `pg_dump` / `pg_restore` round trip with RADIUS grants intact | Production compose | |
| FreeRADIUS starts inside the WireGuard namespace and connects to PostgreSQL; the WireGuard hub generates peer configs | Production compose | No router has connected through a tunnel yet |
| Renewal requests under duplicate workers (`FOR UPDATE SKIP LOCKED`) | Reviewed | |
| Worker crash, payment provider outage, replay through the UI | **Not tested** | |
| Dependencies | NuGet: no known vulnerabilities; npm audit: 0 | |

Automated tests: **83 passing** (`dotnet test`, needs the lab PostgreSQL).

Lab limitations: no KVM, so CHR runs under QEMU emulation (one core per router); Docker Desktop does not pass the
VM's ARP broadcasts, so `lab/chr/lab-arp.sh` pins entries after each start; Kibo's CoA goes through a forwarder
that RouterOS does not accept as its RADIUS server. Host: i7-13650HX, 39.6 GB RAM, WSL2; no host networking was
changed.

## Completed and tested (first round)

| Area | Status | How it was tested |
|---|---|---|
| Research + feature matrix | Done | `docs/RESEARCH.md` |
| App shell, design system (light/dark), auth, lockout, RBAC | Done | Browser |
| Multi-tenancy (query filters + write guard), platform operators, plans, suspension | Done | DB tests, browser |
| Router registration, encrypted API credentials, onboarding script + `login.html` | Done | Browser |
| Router monitoring (REST), capability detection, reboot/offline detection, backoff | Done | Simulator: reboot and stop/start |
| FreeRADIUS integration (SQL decision function, dynamic clients per NAS, accounting upserts, gigawords) | Done | Live RADIUS via simulator, DB tests |
| Captive portal (packages, mobile money, status, voucher, sign-in, RFC 8908 API) | Done | Browser at phone size |
| Simulated mobile money: signed callbacks, provider re-verification, idempotent settlement, ledger | Done | Forged + 3 duplicate callbacks; 6-way concurrency test |
| Subscription lifecycle: first-login activation, data/time caps, expiry, FUP, queueing, stale sessions | Done | Data cap cut-off and reconnect rejection in lab; unit tests |
| Simultaneous-use limits | Done | Second device rejected in lab |
| Router reboot safety (validity kept, Accounting-On closes sessions) | Done | Lab reboot: 244 s left after re-login, expiry unchanged |
| RADIUS outage behaviour | Done | FreeRADIUS stopped: new logins refused clearly, live sessions kept, recovery works |
| Customers, devices, support notes, cash sales, suspend/reactivate, access-code reset | Done | Browser |
| Packages and package builder (validity, start rule, data, time, session cap, speed, FUP, night window, promo) | Done | Browser |
| Vouchers: batches, QR print sheet, sale recording, redemption ledger, void, expiry | Done | Generated, printed, redeemed in lab |
| Agents: commission, credit limit, cash reconciliation | Done (UI) | Service rules exercised manually |
| Payments list, refunds (ledger + access cancellation) | Done | Browser |
| PPPoE: accounts, static IP, cash sale, service-type separation, renewal requests | Done | PPPoE dial in simulator; voucher refused on PPPoE |
| Reports (channel/package/site/agent, daily table, CSV), dashboard, audit logs, staff management | Done | Browser |
| RADIUS CoA (fair-use speed change without disconnect) and Disconnect-Request fallback | Done | Live session dropped from 1024k/2048k to 128k/256k by CoA |
| Security headers/CSP, secure cookies, rate limits, health check, forwarded headers | Done | curl + browser (no CSP violations) |
| Role-aware navigation and dashboard; RBAC enforced in services | Done | Network engineer blocked from payments, reports, staff |
| Tenant isolation in the UI and operator suspension | Done | Second operator sees nothing of the first; suspended owner refused |
| Customer device history (MAC, for support only) | Done | Recorded from live accounting |
| Production compose (Caddy TLS, WireGuard hub, web image) | Done | Smoke-tested on localhost (see above) |

First-round automated tests: **49 passing** (`dotnet test`): business rules, phone/duration parsing, CHAP hashing, login-URL
allow-list, tenant isolation, RADIUS SQL decisions (tenant scoping, remaining bytes/time, FUP, expiry,
suspension, fair-use without byte cap), payment idempotency under concurrency, amount mismatch, append-only ledger,
RADIUS codec (CoA vendor attributes, authenticators, PAP encryption).

## Known limitations

* Real MTN MoMo / Airtel Money adapters are not written (only `sim-momo`). Refunds are recorded, not sent to a
  provider.
* Fair-use `RateChange` overshoot is (accounting interval + up to 15 s) x speed; use `RouterCutoff` when an exact
  threshold matters.
* Several sessions on one subscription share the remaining bytes by splitting; exact aggregate enforcement across
  sessions or routers is not claimed.
* PPPoE, IPv6 bypass and FastTrack effects have not been exercised on RouterOS; 7.24.6 is not bootstrapped.
* Kibo's own CoA has not reached a real router (lab limitation); `radclient` CoA from the RADIUS address works.
* The simulated mobile money provider keeps transactions in memory; restarting it makes pending payments fail
  safely.
* Platform admin tenant switching lasts for the browser tab's session only.

## Next steps

1. Send Kibo's CoA from the RADIUS address in the lab (or over a WireGuard peer) and verify end-to-end throttling.
2. PPPoE and IPv6 tests on CHR; FastTrack accounting comparison.
3. Connect a CHR to the production WireGuard hub.
4. MTN MoMo Collections and Airtel Money adapters behind `IPaymentProvider` (sandbox only).
5. Worker-crash and provider-outage drills.
