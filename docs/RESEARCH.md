# Kibo: Competitive & Technical Research

Kibo is a MikroTik hotspot and ISP billing SaaS. This document covers competitors and the technical background for building it.

- **Date:** 2026-11-01
- **Scope:** Public pages only. No logins and no proprietary code.
- **Stack:** ASP.NET Core / .NET 10, Blazor, PostgreSQL, FreeRADIUS.
- **First market:** Uganda (MTN MoMo, Airtel Money), then East Africa.

**Evidence labels**
- **[Verified-public-page]:** stated on a public page fetched on 2026-11-01.
- **[Vendor claim]:** marketing text we have not verified.
- **[Inferred]:** our deduction from indirect evidence.
- **[Spec]:** taken from vendor documentation or an RFC.

---

## 1. Sources consulted (2026-11-01)

### XenFi
| URL | Result |
|---|---|
| https://xenfi.net/ | Fetched |
| https://xenfi.net/mikrotik-hotspot-billing | Fetched |
| https://xenfi.net/radius-billing-system | Fetched |
| https://xenfi.net/uganda | Fetched |
| https://xenfi.net/fees | Fetched |
| https://xenfi.net/support | Fetched; no technical content, points to the blog |
| https://xenfi.net/partner | Fetched |
| https://xenfi.net/mobile-money-hotspot-billing-uganda | Fetched |
| https://xenfi.net/wifi-voucher-system | Fetched |
| https://blog.xenfi.net (index, page 1 of 5) | Fetched |
| blog: `/blog/smarter-router-setup-starts-here-automating-mikrotik-configuration-with-xenfi` | Fetched |
| blog: `/blog/skip-the-wait-self-onboard-your-router-and-go-live-with-xenfi` | Fetched |
| blog: `/blog/before-your-isp-caps-your-hotspot-configure-these-3-xenfi-features` | Fetched |
| blog: `/blog/before-you-generate-your-next-300-vouchers-on-xenfi-do-this-first` | Fetched |
| blog: `/blog/how-to-connect-a-payment-gateway-and-start-receiving-payments-on-xenfi` | Fetched |
| blog: `/blog/critical-mikrotik-routeros-and-hotspot-security-updates-available-on-xenfi-secure-your-hotspot-now` | Fetched |
| blog: `/blog/xenfi-hotspot-transfers-moving-a-hotspot-to-another-organisation` | Fetched |
| Dashboard (`web.zenfii.net`), router scripts, RADIUS host/secret | Not accessed; behind login. The sign-up domain is spelled `zenfii.net` while the brand is `xenfi.net`. |
| Blog pages 2 to 5 | Not fetched |

### MikroTik

MikroTik is moving its docs from help.mikrotik.com to manual.mikrotik.com. The help.mikrotik.com pages are marked "frozen".

| URL | Result |
|---|---|
| https://help.mikrotik.com/docs/spaces/ROS/pages/87162881/Hotspot+customisation | Fetched |
| https://help.mikrotik.com/docs/spaces/ROS/pages/56459266/HotSpot+-+Captive+portal | Fetched |
| https://help.mikrotik.com/docs/spaces/ROS/pages/1409149/RADIUS | Returned unrelated CAPsMAN content |
| http://manual.mikrotik.com/docs/authentication-authorization-accounting/radius/ | Fetched (succeeded on the second attempt) |
| http://manual.mikrotik.com/docs/authentication-authorization-accounting/user-manager/ | Fetched |
| https://help.mikrotik.com/docs/spaces/ROS/pages/47579162/REST+API | Fetched |
| https://manual.mikrotik.com/docs/getting-started/routeros-licensing/chr/chr-licensing/ | Fetched |
| https://forum.mikrotik.com/t/chr-free-10mbps-instead-of-1mbps/269950 | Fetched |
| http://manual.mikrotik.com/docs/ (index) | HTTP 403 |

### Benchmarks
| URL | Result |
|---|---|
| https://github.com/laksa19/mikhmonv3 (Mikhmon) | Fetched |
| https://splynx.com/ and https://splynx.com/pricing/ | Fetched |
| https://powerlynx.app/ (Splynx's sister hotspot product) | Fetched |
| docs.splynx.com: `mikrotik_dhcp_radius`, `troubleshooting_radius` | Search snippets only |
| https://www.radiusdesk.com/ | Fetched |
| https://radiusdesk.com/wiki24/technical/mikrotik-hotspot-advanced | Fetched |
| https://github.com/lirantal/daloradius | Fetched |
| Omada (TP-Link) docs 12978 and 13025 on support.omadanetworks.com | Search snippets only |

### Standards and lab
| URL | Result |
|---|---|
| https://www.rfc-editor.org/rfc/rfc8910.html | Fetched |
| RFC 2865, 2866, 2869, 5176, 8908 | Not fetched; attribute numbers below are from the well-known RFC text and should be checked against rfc-editor.org |
| Captive-portal probes on Android and iOS | Secondary sources only (purple.ai guides); verify against Apple/Google developer docs |
| evilfreelancer/docker-routeros | Search results and an index page (awesome.ecosyste.ms, MikroTik forum) |

---

## 2. XenFi findings

### 2.1 Positioning and coverage
- XenFi is a cloud hotspot and ISP billing platform with a hosted RADIUS core. It combines billing, provisioning, router monitoring, payment reconciliation and reports. [Verified-public-page]
- It claims to support many router brands [Vendor claim]:
  - MikroTik, through the RouterOS API and cloud RADIUS, "down to queue and profile level"
  - Ruijie Reyee, TP-Link Omada, Ubiquiti UniFi
  - Cisco, Huawei, D-Link and Cambium over standard RADIUS
- Its site lists about 24 country pages, heavily African: Uganda, Kenya, Tanzania, Rwanda, the DRC, Nigeria, Ghana and others. [Verified-public-page]
- The company appears to be based in Uganda: the support phone number is +256 and the payout markets are Uganda and Kenya. [Inferred]

### 2.2 MikroTik integration model (most important)

**Three layers.** Combining the blog posts, XenFi's integration has three layers:
1. **Remote access VPN.** Per router, XenFi generates "a remote access VPN configuration and XenFi installation and bootstrap scripts". The operator pastes the remote-access script into the WinBox terminal. The router then "establish[es] a secure connection with the XenFi platform" and shows up in the dashboard. [Verified-public-page: blog]
   - The VPN protocol is not named anywhere public.
   - Our guess is WireGuard on RouterOS v7, or OpenVPN/SSTP/L2TP for v6 compatibility. [Inferred]
2. **Configuration over the tunnel.**
   - **Automatic mode:** XenFi detects the router's topology, uptime and RouterOS version. The operator builds a bridge in a web UI by dragging in ports such as ether2 and ether3, then sets the gateway IP, subnet size and service type (Hotspot Server or PPPoE Server). XenFi generates a script; the operator pastes it into WinBox and sees "Deployment Successful". [Verified-public-page: blog]
   - **Manual mode:** the operator creates the bridge, IP, DHCP pool and DHCP server, and the hotspot (wizard). They then "use the XenFi profile if one exists or point to the XenFi RADIUS address", and finally install the "XenFi services script". [Verified-public-page: blog]
   - The setup guide assumes a factory reset with "No Default Configuration", WAN on ether1 via DHCP client, and LAN on ether2. [Verified-public-page: blog]
   - The marketing pages say "Existing configs survive". [Vendor claim]
3. **Cloud RADIUS for AAA.** "The router points at XenFi's cloud RADIUS". Authentication and accounting run in the cloud, and plans and expiries live in the cloud rather than in router memory, so a reboot neither grants free time nor loses paid time. [Verified-public-page]
   - The RADIUS page says "no agent". [Vendor claim] For MikroTik this is contradicted in practice: the router still gets a VPN plus scripts.

**What the layers suggest about the protocols.** [Inferred]
- The phrase "down to queue and profile level", plus remote RouterOS upgrades from the dashboard [Verified-public-page: blog], means XenFi uses the RouterOS API (classic or REST) over the VPN. RADIUS alone cannot trigger upgrades.
- Remote access to "a router's management interface from the dashboard" [Vendor claim] is most likely a proxied WinBox or WebFig session over the tunnel.
- "Router heartbeats" and alerts when a router goes silent [Vendor claim] suggest polling over the VPN or API, a script on the router (`/tool fetch` on a scheduler), or both. The "services script" is probably that heartbeat or keepalive scheduler.

**Not documented publicly**
- The captive portal hosting model. Pages mention only "a branded portal" placed "in front of the hotspot" and "customizable landing pages". [Verified-public-page]
- Walled garden, HTTPS/certificates, CoA versus API kick, Acct-Interim-Interval, and the RADIUS host and secret.
- Our best guess: a custom `login.html` uploaded by the bootstrap script redirects to a XenFi-hosted portal, with the walled garden set to the portal and payment hosts. This is the standard pattern, the same one RADIUSdesk documents. [Inferred]

**Expiry and disconnect**
- An expired plan "disconnects on its own". [Vendor claim]
- The mechanism (Session-Timeout, CoA Disconnect, or API `/ip hotspot active remove`) is not stated. Given the VPN, either a DM over the tunnel or the API is plausible. [Inferred]

### 2.3 Packages and limits
- Hotspot bundles can be hourly, daily, weekly or monthly, priced in UGX, with speed, data cap and expiry set per plan. [Verified-public-page]
- PPPoE plans have speeds, caps and expiry, and are provisioned automatically. [Vendor claim]
- Speeds are set per package in Mbps, for example 2 up and 4 down. [Verified-public-page: blog]
- Data quota is per voucher, set in MB or GB, and does not reset. When it runs out the voucher shows "Data Limit Quota Exceeded" and is dead even if its validity remains. [Verified-public-page: blog]
- Speeds step down automatically when a cap is hit, and clients can top up. [Vendor claim] This is a fair-usage-policy throttle, likely done with CoA Rate-Limit or a profile change. [Inferred]
- No night or happy-hour packages, time-of-day rules, or shared-user/device limits are documented.

### 2.4 Payments
- **Uganda and Kenya:** mobile money is built in, so the operator does not need a gateway, an API key or a partnership. XenFi collects the money. [Verified-public-page]
- **Other countries:** the operator connects their own gateway under Settings → Payment Gateways. Providers are not named. [Verified-public-page: blog]
- **Methods:** M-PESA, MTN MoMo, Airtel Money, cards and bank transfer. [Vendor claim]
- **Session activation:** "instant session activation on confirmation" happens directly from the portal. [Vendor claim] The user experience is not documented (phone-number entry, USSD push, auto-login, failure and retry handling).
- **Wallet and ledger:** there is a wallet, ledger and "agent float", with automatic reconciliation. Settlement and payout timing to operators are not documented. [Verified-public-page]

### 2.5 Vouchers and agents
- **Batches:** generated with a numeric or alphanumeric format, a chosen length, an optional username prefix or postfix (for agent attribution), and a batch note. They print as QR or classic PDF layouts. [Verified-public-page: blog]
- **Codes:** the blog's sample codes are short (5 to 6 characters). XenFi itself recommends 8 or more characters to resist brute force. [Verified-public-page: blog]
- **State in the cloud:** "used is used, expired is expired, rebooted routers change nothing". [Verified-public-page]
- **Agent sales:** agents (POS or ground teams) sell for cash, and the code reaches the buyer by SMS. Sales are attributed per agent, and agents get limited admin rights through an "Agent App" and an "Agents gateway". [Verified-public-page]
- **Not documented:** MAC binding, expiry starting at first use, and device-sharing limits.

### 2.6 Tenancy
- Tenancy runs Organisation → Hotspots → Routers.
- Hotspots can be transferred between organisations through a reviewed request. The source organisation must keep at least one hotspot. [Verified-public-page: blog]
- All of an account's routers share vouchers, which allows roaming across a site on the same SSID. [Verified-public-page: blog]
- The model is multi-tenant SaaS: XenFi is the platform and operators are its tenants. [Inferred]

### 2.7 Pricing and partners
- **Revenue share only:** no monthly fee, setup fee, contract or per-hotspot charge. [Verified-public-page]
- **Fees:**
  - The general fee page says 3% of each sale.
  - In Uganda, mobile money is 10%, described as including the telco cost, while printed vouchers and agent sales are 3%. [Verified-public-page]
  - Uganda page example: a UGX 1,000 daily bundle costs the operator UGX 100 (10% on mobile money).
- **Plan tiers:** testimonials and the FAQ mention a free plan and a "Pro" plan, but the fees page lists no tiers. This is inconsistent. [Verified-public-page]
- **Partner programme:** a referral scheme, not white-label. [Verified-public-page]
  - Partners earn 10% of XenFi's revenue on wallet transactions and 20% on invoiced ISP payments.
  - Commission recurs and is paid to a partner wallet with withdrawal to mobile money.
  - There are five tiers, Bronze to Diamond.
  - Payouts currently cover Uganda and Kenya only.
- No white-label offering is mentioned anywhere. [Verified-public-page: absence]

---

## 3. Benchmark summaries

**Mikhmon v3** (laksa19, GPL-2.0, PHP)
- Router-local hotspot manager that talks to one or more MikroTiks through the RouterOS API (classic API).
- Does not use RADIUS. Users live in `/ip hotspot user`, expiry is enforced by on-login profile scripts plus a scheduler, and vouchers print from editable templates with a price and QR code.
- Reports come from router script logs.
- The last changelog entry is v3.20 (2021) and no RouterOS v7 support is noted.
- Its weaknesses are exactly what XenFi markets against: state lives on the router, reboots and resets cause problems, and there are no payments.

**Splynx** (commercial ISP BSS/OSS)
- Built-in RADIUS plus MikroTik API integration (queues and address lists).
- Sends CoA and Disconnect with radclient to port 3799, logged in `coa.log`.
- Its docs say CoA/PoD is not supported for MikroTik DHCP-RADIUS and recommend API address-list blocking instead. This is a useful caveat for DHCP-based services.
- Price: $0.55 per active subscriber per month, falling to $0.165 above 10,000, plus a maintenance fee. The minimum is $255/month.
- Hotspot is a separate product, **Powerlynx**:
  - Cloud RADIUS plus a hosted no-code captive portal.
  - Requires a VPN (WireGuard, OpenVPN or IPsec) or a public IP, a RADIUS client, and external portal redirection. This matches our proposed model.
  - Supports MTN MoMo, M-PESA, Selcom, DPO and Paystack, among others.
  - Priced at $100 per 400 online customers.

**MikroTik User Manager v7** (built into RouterOS)
- A RADIUS server on the router. Supports PAP, CHAP, MS-CHAPv2, EAP and RadSec.
- **Data model:** users, groups, profiles (validity, price, activation start) and limitations (bytes up, down or total, uptime, rate), restricted by time of day or weekday. That covers night packages natively.
- **Extras:** batch users and vouchers, a customer portal at `/um/` with PayPal only, and a per-router CoA port (default 3799).
- **Limits:** active sessions are capped by license level. It is single-tenant with no mobile money.

**RADIUSdesk** (open source: FreeRADIUS plus a PHP/CakePHP web app)
- Covers PPPoE, hotspot, PPSK and MAC authentication, with "multiple clouds, each with multiple admins" (a hierarchy of access providers).
- Includes an OpenWrt mesh/AP controller and an API.
- MikroTik integration hosts the login pages centrally. You replace `login.html`, `alogin.html`, `status.html`, `logout.html` and `fstatus.html` with redirect versions that POST `nasid=$(identity)`, `mac` and `link_login_only` to the RD server.
- Router setup: enable HTTP-PAP, add the RD server to the walled-garden IP list, and optionally set up an HTTPS cert whose DNS name matches the hotspot `dns-name`.
- This is the closest public reference design for our portal flow.

**daloRADIUS** (GPL-2.0, PHP, MariaDB, FreeRADIUS SQL schema)
- A web UI over the FreeRADIUS `rad*` tables.
- Features: NAS, user and group management, hotspot listing, billing (POS, plans, PayPal, invoices), accounting reports, and a PoD/CoA disconnect tool.
- Operators get ACLs and TOTP MFA.
- Single-tenant, with no mobile money. The schema is a good reference for FreeRADIUS `rlm_sql` integration.

**TP-Link Omada** (controller portal)
- Authentication types include voucher, SMS, local user, external RADIUS and external portal server.
- An external web portal is only available with External RADIUS. It submits to the controller at `/portal/radius/browserauth`. From controller 5.3.1 this can be an HTML form POST, added to avoid CORS problems between HTTPS portals and HTTP controllers.
- This is relevant if Kibo later supports Omada: it has the same mixed-content problem as MikroTik.

---

## 4. Feature comparison matrix

Key: ✔ = documented, ✖ = absent, ? = unknown, claim = marketing only.

| Feature | XenFi | Mikhmon | Splynx (+Powerlynx) | User Manager v7 | RadiusDesk | daloRADIUS | Kibo (planned) |
|---|---|---|---|---|---|---|---|
| Hotspot billing | ✔ | ✔ (router-local) | ✔ (Powerlynx) | ✔ | ✔ | ✔ | ✔ |
| Hosted captive portal | claim | ✖ (router templates) | ✔ | ✔ (`/um/` self-care) | ✔ | ✖ | ✔ |
| Mobile money (MTN/Airtel) | ✔ (UG/KE built-in) | ✖ | ✔ (Powerlynx) | ✖ (PayPal) | ✖ | ✖ (PayPal) | ✔ |
| Vouchers | ✔ | ✔ | ✔ | ✔ | ✔ | ? (batch via POS) | ✔ |
| Agents / resellers | ✔ agents; referral partners | ✖ | ✔ (partners) | ✖ | ✔ (hierarchy) | ✖ (operators) | ✔ |
| PPPoE | claim | ✖ | ✔ | ✔ | ✔ | ✔ | ✔ |
| RADIUS | ✔ (cloud) | ✖ | ✔ | ✔ | ✔ | ✔ | ✔ (FreeRADIUS) |
| CoA / disconnect | ? ("disconnects on its own") | ✖ (API kick) | ✔ | ✔ (CoA port) | ? | ✔ | ✔ |
| Multi-tenant | ✔ (orgs) | ✖ | ✖ / partners | ✖ | ✔ | ✖ | ✔ |
| Multi-router | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ |
| Data quotas | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ |
| Scheduled / night packages | ? | ✖ | ? | ✔ (time-of-day limits) | ? | ? | ✔ |
| Reports | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ |
| Monitoring | claim (heartbeat, CPU, bandwidth) | ✔ (traffic) | ✔ | ✖ | ✔ (mesh) | ✖ | ✔ |
| Router auto-provisioning | ✔ (VPN + scripts) | ✖ | ? | n/a | ✖ (manual files) | ✖ | ✔ |
| White-label portal | ✖ (branded portal only) | ✖ | ✔ (branded) | partial (CSS/HTML) | ✔ | ✖ | ✔ |
| Public API | ✖ (none documented) | ✖ | ✔ | ✔ (RouterOS API) | ✔ | ✖ | ✔ |

---

## 5. Technical notes for implementation

### 5.1 Recommended router onboarding (RouterOS v7)

1. **Tunnel.** Create a WireGuard tunnel to the Kibo concentrator. Each router gets a fixed tunnel IP from 10.x/16 (RouterOS v7 has native WireGuard).
   - All RADIUS, CoA and API traffic runs over the tunnel. This solves CGNAT (common on Ugandan LTE and fibre uplinks), dynamic WAN IPs, and the need to expose ports 8728 and 3799 to the internet. [Inferred: same reason XenFi and Powerlynx require a VPN]
2. **Management user.** Create a dedicated API user restricted to the tunnel subnet.
3. **Base config.** Push hotspot, RADIUS and walled-garden config through the REST API, or as a generated `.rsc` script the operator pastes, which is XenFi's UX.
4. **Login pages.** Upload the redirect login pages with `/tool fetch` from the Kibo CDN.

```routeros
# --- RADIUS client (over tunnel; 10.200.0.1 = Kibo FreeRADIUS on tunnel) ---
/radius add service=hotspot,ppp address=10.200.0.1 secret="<per-router-secret>" \
    src-address=10.200.12.34 timeout=3s authentication-port=1812 accounting-port=1813 \
    require-message-auth=yes-for-request-resp comment="kibo"
# Accept CoA/Disconnect. NOTE: RouterOS default port is 1700, NOT 3799 [Spec: manual.mikrotik.com RADIUS]
/radius incoming set accept=yes port=3799

# --- Hotspot server profile ---
/ip hotspot profile set [find name=hsprof1] use-radius=yes radius-accounting=yes \
    radius-interim-update=received login-by=http-chap,http-pap,cookie \
    dns-name=hs.<router-slug>.kibo.example html-directory=kibo nas-port-type=wireless-802.11
# radius-interim-update=received -> honour Acct-Interim-Interval from Access-Accept (HotSpot-only rule)
/ip hotspot user profile set [find default=yes] shared-users=1 add-mac-cookie=yes

# --- PPPoE ---
/ppp aaa set use-radius=yes accounting=yes interim-update=5m

# --- Walled garden for externally hosted portal + payment pages ---
/ip hotspot walled-garden add dst-host=portal.kibo.example action=allow comment=kibo
/ip hotspot walled-garden add dst-host=*.kibo-cdn.example action=allow comment=kibo
/ip hotspot walled-garden ip add dst-address=<portal-ip> protocol=tcp dst-port=443 action=accept comment=kibo
# (walled-garden = HTTP host match via hotspot proxy; walled-garden ip = L3 bypass, required for HTTPS/SNI)

# --- Identity: used as NAS-Identifier / nasid ---
/system identity set name=kibo-<tenant>-<router-id>
```

Implementation notes:
- **`/radius incoming` port.** The default is **1700** [Spec]. Either set `port=3799` explicitly or have the CoA sender target 1700. Treat it as a per-router value stored in the database.
- **Walled-garden IP actions.** The docs disagree on `action=accept` versus `allow` for walled-garden IP entries. Verify in the lab.
- **Shared secrets.** Generate a unique secret per router.
  - Identify the NAS by tunnel source IP in FreeRADIUS `clients`. Loading clients dynamically from SQL with `read_clients = yes` needs a restart; prefer a dynamic-clients virtual server.
  - Cross-check `NAS-Identifier` against the system identity.
- **RadSec.** RouterOS v7 supports RadSec (TCP 2083, `protocol=radsec`) as an alternative where a VPN is not wanted. CoA would still need inbound reachability. [Spec]
- **Classic API versus REST.**
  - **Classic API:** port 8728 plain, 8729 TLS, persistent socket. It supports `listen` and streaming for `/interface monitor-traffic`, logs and active-session changes.
  - **REST:** RouterOS ≥ 7.1 over the `www-ssl` service at `https://<ip>/rest`, or plain `www` from 7.9.
    - Uses Basic auth. GET maps to print, PUT to add, PATCH to set, DELETE to remove, and POST to any command.
    - Supports `.proplist` and `.query`.
    - Has a 60-second timeout and no streaming; use `monitor once`. [Spec]
  - **Recommendation:** use REST over the tunnel for configuration and CRUD (simple and stateless from .NET `HttpClient`), and the classic API (TLS, 8729) only for live bandwidth streaming if needed. Supporting RouterOS v6 would require the classic API.

### 5.2 Hotspot login flow with an externally hosted portal

This follows the model MikroTik documents in "Hotspot customisation → external authentication", and the one RADIUSdesk uses.

1. **Unauthenticated HTTP request.** The client's first plain-HTTP request is intercepted, and the router serves `redirect.html` and then `login.html` from `html-directory=kibo`.
2. **Hand-off to the portal.** Our `login.html` is a tiny auto-submitting form (or a JS redirect) to `https://portal.kibo.example/h/<routerId>`. It carries:
   - `mac=$(mac)`, `ip=$(ip)`
   - `link-login-only=$(link-login-only)`, `link-orig=$(link-orig)`
   - `chap-id=$(chap-id)`, `chap-challenge=$(chap-challenge)`
   - `nasid=$(identity)`, `error=$(error)`

   CHAP variables only exist on the login page. [Spec]
3. **The portal (Blazor or a static page) shows packages.** The user either enters a voucher or pays with MoMo or Airtel through a USSD push. The portal polls the payment status.
4. **Credentials.** On success, the backend creates a RADIUS credential. Use the voucher code itself, or a one-time username and password bound to the MAC for paid sessions.
5. **Post back to the router.** The portal sends the browser back to `link-login-only` with:
   - `username`
   - `password`: either the plain password (PAP), or for CHAP the hex MD5 of `chap-id + password + chap-challenge` (bytes in that order; the octal-escaped chap-id/challenge must be decoded first) [Spec]
   - `dst=<link-orig or success URL>`
   - `popup=false`
6. **RADIUS round trip.** The router sends an Access-Request to FreeRADIUS. On Access-Accept the router shows `alogin.html` (redirect to `dst`). On reject it shows `flogin.html`/`login.html` with `$(error)`, which we bounce back to the portal with the error text. [Spec]
7. **Optional MAC re-login.** `login-by=mac` or `mac-cookie` with RADIUS (`radius-mac-format`) lets returning devices re-authenticate silently while a plan is still valid. Use `mac-auth-password` or the MAC as password.

**Mixed content is the key gotcha.** [Inferred, verify in lab]
- `link-login-only` is `http://<dns-name or IP>/login`. A form POST from an HTTPS portal to an HTTP router URL makes Chrome and Android show an "insecure form" warning interstitial.
- **Mitigation A:** have the portal do a top-level GET navigation to `link-login-only?username=..&password=<CHAP-hash>&dst=..`. With CHAP, the hash is single-use and useless after the challenge rotates. Top-level navigations are not blocked as mixed content.
- **Mitigation B:** give each router a real cert on a per-router DNS name (`hs.<slug>.kibo.example`, Let's Encrypt, uploaded through the API), enable `login-by=https`, and set `ssl-certificate`. This is operationally heavy: certs renew every 90 days, and the DNS name must resolve to the hotspot IP from inside the LAN (the hotspot `dns-name` does this).
- **Default:** use A (CHAP plus GET) by default and offer B as an option.
- **Never use PAP over HTTP outside the LAN.** The LAN leg is local, but avoid PAP anyway.

### 5.3 RADIUS attributes Kibo must return

| Attribute | Type | Use |
|---|---|---|
| Session-Timeout (27) | RFC 2865 | Remaining validity in seconds, for time packages and absolute expiry (`min(plan_left, expiry - now)`) |
| Idle-Timeout (28) | RFC 2865 | Idle kick, e.g. 300–900 s |
| Acct-Interim-Interval (85) | RFC 2869 | 60–300 s. HotSpot honours it only with `radius-interim-update=received` |
| Class (25) | RFC 2865 | Opaque session/plan id echoed in accounting, for reliable session-to-purchase linking |
| Mikrotik-Rate-Limit (14988:8) | VSA string | e.g. `2M/4M` (rx/tx from the router's view: rx = client upload). Bursts: `2M/4M 4M/8M 1500k/3M 10/10` |
| Mikrotik-Recv-Limit (14988:1), Mikrotik-Recv-Limit-Gigawords (14988:14) | VSA int | Upload byte cap |
| Mikrotik-Xmit-Limit (14988:2), Mikrotik-Xmit-Limit-Gigawords (14988:15) | VSA int | Download byte cap |
| Mikrotik-Total-Limit (14988:17), Mikrotik-Total-Limit-Gigawords (14988:18) | VSA int | Combined cap. Listed in the vendor table; verify that HotSpot honours it, otherwise enforce via Recv/Xmit plus accounting |
| Mikrotik-Address-List (14988:19) | VSA string | Tag sessions for firewall/QoS rules (e.g. `kibo-throttled`) |
| Mikrotik-Group (14988:3) | VSA string | Map to a local hotspot user profile or PPP profile |
| Framed-IP-Address / Framed-Pool | RFC 2865 | PPPoE static IPs and pools |
| Port-Limit (62) | RFC 2865 | Simultaneous sessions (HotSpot shared-users equivalent) |

**Remaining-quota rule.** Return the *remaining* quota, `plan_bytes - used_bytes`, on every Access-Accept. The router enforces it locally within a session, and the backend enforces it across sessions.

### 5.4 Accounting and gigawords math (RFC 2866 / 2869)

**Packet types**
- `Acct-Status-Type`: Start = 1, Stop = 2, Interim-Update = 3, Accounting-On = 7, Accounting-Off = 8.
- On Accounting-On/Off (router reboot), close all of that NAS's open sessions.

**Byte counts**
- `bytes_in = Acct-Input-Gigawords(52) * 2^32 + Acct-Input-Octets(42)`, and the same for output (53 and 43).
- Store as `bigint`. Octets alone wrap at 4 GiB.
- Input is from the NAS's perspective, meaning client upload.

**Counters are cumulative per `Acct-Session-Id`(44).** Store the last snapshot and compute deltas: `delta = new_total - last_total`. If the delta is negative, treat it as a counter reset or a new session.

**Session time**
- `Acct-Session-Time`(46) is cumulative seconds.
- `Acct-Terminate-Cause`(49) values: User-Request = 1, Lost-Carrier = 2, Idle-Timeout = 4, Session-Timeout = 5, Admin-Reset = 6, NAS-Reboot = 11.

**Splitting outbound limits.** For a value V over 4 GiB, send `Limit = V mod 2^32` and `Limit-Gigawords = V div 2^32`. [Spec]

**Stale sessions.** Close sessions with no interim update for 2.5–3 × the interim interval, and mark them `stale`.

**Throughput.** FreeRADIUS `rlm_sql` to PostgreSQL works. At scale, write accounting to a queue table and aggregate asynchronously; don't run billing logic inside FreeRADIUS. Use `rlm_rest` to call the .NET API for authorize/post-auth if we want the business logic in C#.

### 5.5 CoA / Disconnect (RFC 5176)

**Packet codes**
- Disconnect-Request = 40, with ACK 41 and NAK 42.
- CoA-Request = 43, with ACK 44 and NAK 45.
- The IANA port is UDP 3799, but RouterOS defaults to 1700.

**RouterOS behaviour** [Spec]
- Supports DM and CoA. "POD is not supported" in MikroTik's terminology, though DM is effectively RFC 5176 Disconnect.
- CoA can change Rate-Limit, Recv/Xmit-Limit, Group, Session-Timeout, Idle-Timeout, Address/Advertise, Filter-Id and Port-Limit.
- IP, pool and route changes need a disconnect.

**Identifying the session.** Send `User-Name` plus `Acct-Session-Id`, optionally `Framed-IP-Address` and `Calling-Station-Id`, and sign with the per-router secret including Message-Authenticator.

**By service**
- **HotSpot:** Disconnect works, and also removes the MAC cookie. CoA rate change works for FUP throttling.
- **PPPoE:** Disconnect works. CoA rate change works for the dynamic simple queue.
- **DHCP-RADIUS:** Splynx reports no CoA/PoD support, so use API address-list blocking.

**Fallback.** If CoA gets a NAK or times out, call REST `POST /rest/ip/hotspot/active/remove` or `/rest/ppp/active/remove` over the tunnel.

**Expiry-at-time.** Rely primarily on Session-Timeout so the router kicks the user even if the backend is offline. Use CoA only for early termination: refunds, quota exhausted across sessions, or a plan change.

### 5.6 Captive portal / CNA gotchas

**Probe URLs** [secondary sources]
- iOS/macOS: `http://captive.apple.com/hotspot-detect.html`, expects a 200 with a "Success" body.
- Android: `http://connectivitycheck.gstatic.com/generate_204`, expects a 204.
- Windows: `msftconnecttest.com/connecttest.txt`.

**Walled garden must not include probe hosts.**
- Never walled-garden these probe hosts, or the device thinks it is online and never opens the portal.
- After login the router must let probes through, so the CNA closes or shows "Done".

**CNA limitations (Apple's mini-browser)**
- Ephemeral cookies and storage.
- Poor support for redirects into HTTPS with untrusted certs.
- Cannot hand off to other apps. USSD push works because it is network-initiated, but deep links to MoMo apps may fail.
- Heavy JS and social logins break.

**Portal design rules**
- Keep the portal light and server-rendered: plain HTML, or a Blazor static SSR page with progressive enhancement rather than Blazor Server or WASM.
- Put the payment status poll in a few lines of plain JS.
- Show the voucher or receipt code on screen and by SMS, so a user whose CNA closes can log in again from a full browser.

**HTTPS interception.** RouterOS hotspot cannot transparently redirect HTTPS (cert errors are inevitable). We rely on OS probes being HTTP.

**RFC 8910 / 8908**
- DHCPv4 option **114**, DHCPv6 option 103, RA option 37 carry a URI to an RFC 8908 JSON API. Should be HTTPS and must not be an IP literal. [Spec]
- iOS 14+ and Android 11+ use it for portal detection [secondary source; verify].
- **Plan:** set it from Kibo with `/ip dhcp-server option add code=114 name=capport value="'https://portal.kibo.example/capport/<routerId>'"`.
- Serve a per-client JSON response: `{"captive":true,"user-portal-url":...,"seconds-remaining":...,"bytes-remaining":...}`.
- **Open question:** the API is queried from the client's IP. Behind hotspot NAT, the backend must map the request to a session using the router's src-NAT IP plus MAC or IP. Needs design.

### 5.7 Local lab

- **CHR licensing** [Spec]
  - Free tier: runs indefinitely but caps **upload at 1 Mbps per interface**, with all features otherwise available. Still 1 Mbps as of 2026; a forum thread asks for 10 Mbps.
  - Paid tiers: P1 is 1 Gbps for $45, P10 is 10 Gbps for $95, P-Unlimited is $250.
  - A 60-day trial is available via `/system license renew` with a mikrotik.com account.
  - **Fine for functional RADIUS, CoA, API and hotspot testing.**
- **`evilfreelancer/docker-routeros`**
  - Community project (MIT): runs CHR inside QEMU in a Docker container. Needs `/dev/net/tun`, `/dev/kvm` and NET_ADMIN.
  - Last push February 2025. RouterOS v7 tags should be verified before relying on it.
  - It needs KVM, so on Windows run it under WSL2 with nested virtualisation, or use Hyper-V or VirtualBox with the official CHR `.vhdx`/`.vdi` image. Using the official image directly is simpler on this dev machine.
  - RouterOS cannot run inside its own `/container` feature.
- **Hotspot clients in the lab:** put a Linux VM or container on the CHR LAN side, then curl the login flow and run `radclient` for CoA tests.

---

## 6. Architectural assumptions

1. **RADIUS is the source of truth for access; the router holds no billing state.** Hotspot users are never written to `/ip hotspot user`. This is the core lesson from Mikhmon's weaknesses and XenFi's "rebooted routers change nothing".
2. **Every router connects over a Kibo-managed WireGuard tunnel.** RADIUS, CoA and the management API all go over it. The architecture assumes routers sit behind NAT or CGNAT and have no public IP. RouterOS v7 is the baseline; v6 is unsupported or best-effort (it has no WireGuard and no REST).
3. **FreeRADIUS sits at the edge; business logic lives in .NET.**
   - FreeRADIUS uses `rlm_rest` or `rlm_sql` against PostgreSQL and the Kibo API for authorize, post-auth and accounting.
   - Authorization decisions (remaining time and bytes, rate) are computed in C# and cached.
   - Kibo needs its own CoA sender: a .NET UDP client or radclient behind a queue.
4. **Multi-tenancy is row-level in PostgreSQL**, with `tenant_id` on every table and RLS or EF global query filters. NAS records map tunnel IP to router to site to tenant.
5. **Mobile money is collected through aggregators or direct APIs.** Options are the MTN MoMo Open API Collections and Airtel Money Open API in Uganda, or aggregators such as Flutterwave, Pesapal, Yo! Payments or DPO.
   - Whether Kibo or the operator is merchant of record is a business decision. XenFi holds funds itself, which implies a wallet, ledger, payouts and probably a regulatory licence such as a Bank of Uganda payment-system licence. [Inferred, needs legal check]
   - The architecture must support both a platform wallet and operator-owned keys (bring your own gateway).
6. **The captive portal is hosted centrally** at one per-tenant white-label domain. Routers carry only a tiny redirect `login.html` plus status and logout pages.
7. **Session expiry is enforced by router-side timeouts** (Session-Timeout and byte limits). CoA and the API are only for early changes, so the system degrades safely if the cloud is unreachable.
   - The router cannot authenticate new users without RADIUS. A cloud outage means no new logins.
   - Consider an optional RADIUS backup: a secondary server, or User Manager on the router as a fallback for vouchers.

## 7. Proposed enhancements over benchmarks

1. **Transparent router onboarding.**
   - A one-line bootstrap script that shows exactly what it changes, with an idempotent, versioned config "desired state" applied over REST and drift detection.
   - XenFi requires a factory reset in its guide; Kibo should merge into existing configs and roll back on failure.
2. **Cheaper mobile-money fees and an honest fee breakdown** (XenFi charges 10% on mobile money in Uganda).
   - Offer bring-your-own MoMo/Airtel credentials at a flat SaaS fee, alongside an optional managed wallet.
   - Show telco fee versus platform fee per transaction.
3. **Real white-label.** Custom domain, theme and SMS sender ID per tenant, plus a reseller (sub-tenant) hierarchy with margins. XenFi only has referral partners.
4. **Public REST API and webhooks** (payment.succeeded, session.started, voucher.redeemed, router.offline). XenFi documents none.
5. **Smarter packages:**
   - Night and happy-hour packages and time-of-day rate profiles (User Manager has these; XenFi does not document them).
   - Expiry starting at first use versus at purchase.
   - Device or MAC binding, shared-user count, and data rollover.
   - Fair-usage throttling via CoA (`Mikrotik-Rate-Limit`) instead of a hard cut.
6. **Resilient payments UX:**
   - USSD push with clear pending, failed and retry states, with idempotency keys on each callback.
   - Automatic login when the CNA closes (MAC auto-login after payment).
   - SMS receipts with a reconnect code.
   - Reconciliation jobs against provider statements.
7. **Voucher security:** 8–10 characters from an unambiguous alphabet, rate-limited redemption per MAC and per router, and batch revoke.
8. **RFC 8910/8908 support** (DHCP option 114) for better captive-portal UX on modern iOS and Android. No competitor documents this.
9. **Observability:**
   - Per-router heartbeat over the tunnel, plus CPU, uptime and live clients by polling over REST.
   - "Router silent / site stopped selling" alerts.
   - Accounting-gap detection, plus a reboot audit via Accounting-On.
10. **Agent app with float management and offline voucher sales.** Agents pre-buy float, and every sale is attributed (which XenFi does). Add commission rules and daily settlement reports.
11. **Lab-first engineering:** a CHR-based integration test suite in CI (QEMU or KVM runner) covering hotspot login, CHAP, CoA, quotas and gigawords wrap.

### Open items to verify in the lab
- Whether HotSpot honours `Mikrotik-Total-Limit`.
- `walled-garden ip` `action=accept` versus `allow`.
- CoA rate change on an active hotspot session on v7.
- Chrome's behaviour with an HTTPS→HTTP GET hand-off to `link-login-only`.
- The DHCP option 114 value format on RouterOS.
- Interim behaviour with `radius-interim-update=received` versus a fixed time.
