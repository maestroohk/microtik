# Kibo progress

_Last updated: 2026-11-01_

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

## Completed and tested

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
| Security headers/CSP, secure cookies, rate limits, health check, forwarded headers | Done | Build + browser |
| Production compose (Caddy TLS, WireGuard hub, web image) | Written | **Not yet built or run** |

Automated tests: **49 passing** (`dotnet test`): business rules, phone/duration parsing, CHAP hashing, login-URL
allow-list, tenant isolation, RADIUS SQL decisions (tenant scoping, remaining bytes/time, FUP, expiry,
suspension, fair-use without byte cap), payment idempotency under concurrency, amount mismatch, append-only ledger,
RADIUS codec (CoA vendor attributes, authenticators, PAP encryption).

## Known limitations

* **No real RouterOS verified yet.** All router behaviour above is from the Kibo simulator. Next step: run
  CHR in Hyper-V/VirtualBox (free licence, 1 Mbps) and repeat the lab script.
* Real MTN MoMo / Airtel Money adapters are not written (only `sim-momo`). Refunds are recorded, not sent to a
  provider.
* Fair-use is a soft threshold: detection waits for the next interim update (60 s) plus the 15 s worker, so a
  fast customer can exceed the threshold before being slowed. Hard caps (no fair-use speed) are enforced by the
  router itself via `Mikrotik-Total-Limit`.
* The production compose file and web Dockerfile are untested.
* `login.html` passes the CHAP challenge in the query string; confirm against real RouterOS escaping.
* Per-subscription usage queries run every 15 s; for thousands of concurrent subscribers this should become a
  set-based query plus an index on `(username, acctstarttime)`.
* The simulated mobile money provider keeps transactions in memory; restarting it makes pending payments fail
  safely.
* Platform admin tenant switching lasts for the browser tab's session only.

## Next steps

1. CHR lab on Hyper-V with an isolated internal switch; verify hotspot redirect, CHAP login, limits, CoA port.
2. MTN MoMo Collections and Airtel Money adapters behind `IPaymentProvider` (sandbox only).
3. Build and smoke-test the production compose; add backups and metrics.
4. Set-based usage aggregation for large subscriber counts.
5. Opt-in automated provisioning per router, tested against CHR first.
