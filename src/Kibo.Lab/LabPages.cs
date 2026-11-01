using System.Net;
using System.Text;
using Kibo.Lab.MobileMoney;
using Kibo.Lab.Router;

namespace Kibo.Lab;

public static class LabPages
{
    private const string DeviceCookie = "sim-device";

    public static void MapLabPages(this WebApplication app)
    {
        app.MapGet("/", (SimulatedRouter router, MobileMoneySimulator momo) => Results.Content(Dashboard(router, momo), "text/html"));

        app.MapPost("/lab/devices", (SimulatedRouter router) =>
        {
            router.AddDevice();
            return Results.Redirect("/");
        });

        app.MapPost("/lab/devices/{mac}/activity/{activity}", (string mac, Activity activity, SimulatedRouter router) =>
        {
            var device = router.FindDevice(mac);
            if (device != null)
            {
                device.Activity = activity;
            }

            return Results.Redirect("/");
        });

        app.MapPost("/lab/reboot", async (SimulatedRouter router, CancellationToken ct) =>
        {
            await router.RebootAsync(ct);
            return Results.Redirect("/");
        });

        app.MapPost("/lab/sessions/{id}/logout", async (string id, SimulatedRouter router, CancellationToken ct) =>
        {
            await router.RemoveSessionAsync("*" + id, "User-Request", ct);
            return Results.Redirect("/");
        });

        app.MapPost("/lab/momo/{id}/{action}", async (string id, string action, MobileMoneySimulator momo, CancellationToken ct) =>
        {
            switch (action)
            {
                case "approve":
                    await momo.CompleteAsync(id, true, 1, ct);
                    break;
                case "approve-duplicate":
                    await momo.CompleteAsync(id, true, 3, ct);
                    break;
                case "decline":
                    await momo.CompleteAsync(id, false, 1, ct);
                    break;
                case "forge":
                    await momo.SendForgedCallbackAsync(id, ct);
                    break;
            }

            return Results.Redirect("/");
        });

        app.MapGet("/hotspot/connect/{mac}", (string mac, SimulatedRouter router, HttpContext http) =>
        {
            var device = router.FindDevice(mac);
            if (device == null)
            {
                return Results.Redirect("/");
            }

            http.Response.Cookies.Append(DeviceCookie, device.Mac, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Lax });
            var existing = router.Sessions.FirstOrDefault(s => s.Device.Mac == device.Mac);
            if (existing != null)
            {
                return Results.Redirect("/hotspot/status");
            }

            router.IssueChallenge(device);
            return Results.Redirect(PortalUrl(router, device, null));
        });

        app.MapGet("/login", async (HttpContext http, SimulatedRouter router, CancellationToken ct) =>
        {
            var device = router.FindDevice(http.Request.Cookies[DeviceCookie]);
            if (device == null)
            {
                return Results.Content(Page("No simulated device", "<p>Open the portal from the lab page first so the router knows which phone you are.</p><p><a href='/'>Lab</a></p>"), "text/html");
            }

            var username = http.Request.Query["username"].ToString();
            var password = http.Request.Query["password"].ToString();
            var destination = http.Request.Query["dst"].ToString();
            var result = await router.LoginAsync(device, username, password, ct);
            device.LastMessage = result.Message;
            if (!result.Success)
            {
                router.IssueChallenge(device);
                return Results.Redirect(PortalUrl(router, device, result.Message));
            }

            return Results.Redirect(string.IsNullOrEmpty(destination) ? "/hotspot/status" : destination);
        });

        app.MapGet("/hotspot/status", (HttpContext http, SimulatedRouter router) =>
        {
            var device = router.FindDevice(http.Request.Cookies[DeviceCookie]);
            var session = router.Sessions.FirstOrDefault(s => s.Device.Mac == device?.Mac);
            if (session == null)
            {
                var note = device?.LastMessage == null ? "" : $"<p class='muted'>Last message: {Encode(device.LastMessage)}</p>";
                return Results.Content(Page("Not connected", $"<p>This simulated phone has no active hotspot session.</p>{note}<p><a href='/'>Back to the lab</a></p>"), "text/html");
            }

            var now = DateTime.UtcNow;
            var left = session.SessionTimeoutSeconds == null ? "no limit" : $"{Math.Max(0, session.SessionTimeoutSeconds.Value - session.UptimeSeconds(now))} s";
            var data = session.TotalLimitBytes == null ? "no limit" : $"{Math.Max(0, session.TotalLimitBytes.Value - session.BytesIn - session.BytesOut) / 1024 / 1024.0:0.0} MB";
            return Results.Content(Page("You are connected", $"""
                <meta http-equiv="refresh" content="5">
                <p>Logged in as <b>{Encode(session.User)}</b> on {Encode(router.Settings.Identity)} (simulator).</p>
                <table>
                <tr><td>Time left (router counter)</td><td>{left}</td></tr>
                <tr><td>Data left (router counter)</td><td>{data}</td></tr>
                <tr><td>Rate limit</td><td>{Encode(session.RateLimit)}</td></tr>
                <tr><td>Used this session</td><td>{(session.BytesIn + session.BytesOut) / 1024 / 1024.0:0.00} MB</td></tr>
                </table>
                <p><a href="/">Back to the lab</a></p>
                """), "text/html");
        });
    }

    private static string PortalUrl(SimulatedRouter router, SimDevice device, string? error)
    {
        var settings = router.Settings;
        var query = new StringBuilder();
        query.Append("mac=").Append(Uri.EscapeDataString(device.Mac));
        query.Append("&ip=").Append(device.Ip);
        query.Append("&login=").Append(Uri.EscapeDataString($"{settings.PublicBaseUrl}/login"));
        query.Append("&dst=").Append(Uri.EscapeDataString($"{settings.PublicBaseUrl}/hotspot/status"));
        query.Append("&chapid=").Append(Uri.EscapeDataString(SimulatedRouter.OctalEscape([device.ChapId])));
        query.Append("&chapch=").Append(Uri.EscapeDataString(SimulatedRouter.OctalEscape(device.ChapChallenge)));
        if (!string.IsNullOrEmpty(error))
        {
            query.Append("&error=").Append(Uri.EscapeDataString(error));
        }

        return $"{settings.PortalBaseUrl.TrimEnd('/')}/p/{settings.PortalSiteSlug}?{query}";
    }

    private static string Dashboard(SimulatedRouter router, MobileMoneySimulator momo)
    {
        var now = DateTime.UtcNow;
        var body = new StringBuilder();
        body.Append($"""
            <meta http-equiv="refresh" content="4">
            <div class="banner">Simulator — not RouterOS. Use it to exercise Kibo's RADIUS, portal and payment flows; it does not prove real router behaviour.</div>
            <section>
              <h2>Router <span class="muted">{Encode(router.Settings.Identity)} · NAS {router.Settings.NasIpAddress}</span></h2>
              <p>Uptime {RouterApiEndpoints.FormatUptime(now - router.BootedAt)} ·
                 RADIUS {(router.RadiusReachable ? "<span class='ok'>reachable</span>" : $"<span class='bad'>{Encode(router.LastRadiusError)}</span>")}</p>
              <form method="post" action="/lab/reboot"><button>Reboot router (drops sessions, sends Accounting-On)</button></form>
            </section>
            <section>
              <h2>Phones</h2>
              <form method="post" action="/lab/devices"><button>Add a phone</button></form>
              <table><tr><th>Phone</th><th>MAC / IP</th><th>Activity</th><th>Last message</th><th></th></tr>
            """);
        foreach (var device in router.Devices)
        {
            body.Append($"""
                <tr><td>{Encode(device.Name)}</td><td class="mono">{device.Mac}<br>{device.Ip}</td>
                <td>{ActivityButtons(device)}</td><td>{Encode(device.LastMessage ?? "")}</td>
                <td><a class="button" href="/hotspot/connect/{Uri.EscapeDataString(device.Mac)}">Open browser on this phone</a></td></tr>
                """);
        }

        body.Append("</table></section><section><h2>Active hotspot sessions</h2><table><tr><th>User</th><th>Phone</th><th>Uptime</th><th>Used</th><th>Limits from RADIUS</th><th></th></tr>");
        foreach (var s in router.Sessions)
        {
            var limit = s.TotalLimitBytes == null ? "no data cap" : $"{s.TotalLimitBytes / 1024.0 / 1024.0:0.0} MB cap";
            var timeout = s.SessionTimeoutSeconds == null ? "no timeout" : $"timeout {s.SessionTimeoutSeconds}s";
            body.Append($"""
                <tr><td class="mono">{Encode(s.User)}</td><td>{Encode(s.Device.Name)}</td><td>{s.UptimeSeconds(now)}s</td>
                <td>{(s.BytesIn + s.BytesOut) / 1024.0 / 1024.0:0.00} MB</td><td>{Encode(s.RateLimit)} · {limit} · {timeout}</td>
                <td><form method="post" action="/lab/sessions/{s.Id.TrimStart('*')}/logout"><button>Log out</button></form></td></tr>
                """);
        }

        body.Append("</table></section><section><h2>Mobile money prompts <span class='muted'>(simulated provider)</span></h2><table><tr><th>Transaction</th><th>Phone</th><th>Amount</th><th>Status</th><th>Actions</th><th>Callbacks</th></tr>");
        foreach (var c in momo.Recent)
        {
            var actions = c.Status == "PENDING"
                ? $"""
                  <form method="post" action="/lab/momo/{c.TransactionId}/approve"><button class="primary">Approve</button></form>
                  <form method="post" action="/lab/momo/{c.TransactionId}/approve-duplicate"><button>Approve + 3 duplicate callbacks</button></form>
                  <form method="post" action="/lab/momo/{c.TransactionId}/decline"><button>Decline</button></form>
                  <form method="post" action="/lab/momo/{c.TransactionId}/forge"><button>Send forged callback</button></form>
                  """
                : "";
            body.Append($"""
                <tr><td class="mono">{c.TransactionId}<br><span class="muted">{Encode(c.Request.ExternalReference)}</span></td>
                <td class="mono">{Encode(c.Request.PhoneNumber)}</td><td>{c.Request.Amount:#,0} {Encode(c.Request.Currency)}</td>
                <td>{c.Status}</td><td class="actions">{actions}</td><td class="muted">{Encode(string.Join(" · ", c.CallbackLog))}</td></tr>
                """);
        }

        body.Append("</table></section>");
        return Page("Kibo lab", body.ToString());
    }

    private static string ActivityButtons(SimDevice device) => string.Concat(Enum.GetValues<Activity>().Select(a =>
        $"<form method='post' action='/lab/devices/{Uri.EscapeDataString(device.Mac)}/activity/{a}'><button class='{(device.Activity == a ? "primary" : "")}'>{a}</button></form>"));

    private static string Encode(string value) => WebUtility.HtmlEncode(value);

    private static string Page(string title, string body) => $$"""
        <!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <title>{{Encode(title)}}</title>
        <style>
        body{font-family:system-ui,sans-serif;margin:0;padding:24px;background:#f4f4f2;color:#1c1c1c;font-size:14px}
        h1{font-size:20px;margin:0 0 16px} h2{font-size:15px;margin:0 0 10px}
        section{background:#fff;border:1px solid #ddd;border-radius:8px;padding:16px;margin-bottom:16px;overflow-x:auto}
        table{border-collapse:collapse;width:100%;margin-top:10px} td,th{border-top:1px solid #eee;padding:8px;text-align:left;vertical-align:top}
        th{font-size:12px;color:#666} .mono{font-family:ui-monospace,monospace;font-size:12px} .muted{color:#777;font-weight:normal}
        button,.button{font:inherit;font-size:12px;padding:5px 9px;border:1px solid #bbb;border-radius:5px;background:#fff;cursor:pointer;text-decoration:none;color:inherit;display:inline-block;margin:2px 0}
        button.primary{background:#1c1c1c;color:#fff;border-color:#1c1c1c} form{display:inline}
        .banner{background:#fff4d6;border:1px solid #e8c66a;padding:10px 14px;border-radius:8px;margin-bottom:16px}
        .ok{color:#137a4b} .bad{color:#b4232c} .actions form{display:block}
        </style></head><body><h1>{{Encode(title)}}</h1>{{body}}</body></html>
        """;
}
