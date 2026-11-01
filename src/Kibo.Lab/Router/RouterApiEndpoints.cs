using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace Kibo.Lab.Router;

public static class RouterApiEndpoints
{
    public static void MapRouterApi(this WebApplication app)
    {
        var rest = app.MapGroup("/rest").AddEndpointFilter(async (context, next) =>
        {
            var options = context.HttpContext.RequestServices.GetRequiredService<IOptions<LabOptions>>().Value;
            return IsAuthorized(context.HttpContext.Request, options)
                ? await next(context)
                : Results.Json(new { error = 401, message = "Unauthorized" }, statusCode: 401);
        });

        rest.MapGet("/system/resource", (SimulatedRouter router) => new JsonObject
        {
            ["uptime"] = FormatUptime(DateTime.UtcNow - router.BootedAt),
            ["version"] = "7.16.2 (simulator)",
            ["cpu-load"] = Math.Min(95, 4 + router.Sessions.Count * 3 + Random.Shared.Next(0, 6)).ToString(),
            ["free-memory"] = (180_000_000L - router.Sessions.Count * 400_000L).ToString(),
            ["total-memory"] = "268435456",
            ["architecture-name"] = "x86_64",
            ["board-name"] = "Kibo RouterOS simulator",
            ["platform"] = "MikroTik"
        });

        rest.MapGet("/system/identity", (SimulatedRouter router) => new JsonObject { ["name"] = router.Settings.Identity });

        rest.MapGet("/system/routerboard", () => new JsonObject { ["routerboard"] = "false" });

        rest.MapGet("/ip/hotspot", () => new JsonArray
        {
            new JsonObject { [".id"] = "*1", ["name"] = "hotspot1", ["interface"] = "bridge-hotspot", ["profile"] = "hsprof1", ["disabled"] = "false" }
        });

        rest.MapGet("/radius", (SimulatedRouter router) => new JsonArray
        {
            new JsonObject
            {
                [".id"] = "*1", ["address"] = router.Settings.RadiusServer, ["service"] = "ppp,hotspot",
                ["src-address"] = router.Settings.NasIpAddress, ["disabled"] = "false"
            }
        });

        rest.MapGet("/radius/incoming", () => new JsonObject { ["accept"] = "true", ["port"] = "3799" });

        rest.MapGet("/ip/hotspot/active", (SimulatedRouter router) =>
        {
            var now = DateTime.UtcNow;
            var list = new JsonArray();
            foreach (var s in router.Sessions.Where(s => !s.IsPppoe))
            {
                list.Add(new JsonObject
                {
                    [".id"] = s.Id, ["server"] = "hotspot1", ["user"] = s.User, ["address"] = s.Device.Ip,
                    ["mac-address"] = s.Device.Mac, ["uptime"] = FormatUptime(TimeSpan.FromSeconds(s.UptimeSeconds(now))),
                    ["bytes-in"] = s.BytesIn.ToString(), ["bytes-out"] = s.BytesOut.ToString(),
                    ["session-time-left"] = s.SessionTimeoutSeconds == null ? "" :
                        FormatUptime(TimeSpan.FromSeconds(Math.Max(0, s.SessionTimeoutSeconds.Value - s.UptimeSeconds(now)))),
                    ["radius"] = "true"
                });
            }

            return list;
        });

        rest.MapPost("/ip/hotspot/active/remove", async (JsonObject body, SimulatedRouter router, CancellationToken ct) =>
        {
            var id = body[".id"]?.ToString() ?? "";
            return await router.RemoveSessionAsync(id, "Admin-Reset", ct)
                ? Results.Json(new JsonArray())
                : Results.Json(new { error = 400, message = "no such item" }, statusCode: 400);
        });

        rest.MapGet("/ppp/active", (SimulatedRouter router) =>
        {
            var now = DateTime.UtcNow;
            var list = new JsonArray();
            foreach (var s in router.Sessions.Where(s => s.IsPppoe))
            {
                list.Add(new JsonObject
                {
                    [".id"] = s.Id, ["name"] = s.User, ["service"] = "pppoe", ["caller-id"] = s.Device.Mac,
                    ["address"] = s.FramedIp ?? s.Device.Ip, ["uptime"] = FormatUptime(TimeSpan.FromSeconds(s.UptimeSeconds(now))),
                    ["radius"] = "true"
                });
            }

            return list;
        });

        rest.MapPost("/ppp/active/remove", async (JsonObject body, SimulatedRouter router, CancellationToken ct) =>
        {
            var id = body[".id"]?.ToString() ?? "";
            return await router.RemoveSessionAsync(id, "Admin-Reset", ct)
                ? Results.Json(new JsonArray())
                : Results.Json(new { error = 400, message = "no such item" }, statusCode: 400);
        });

        rest.MapGet("/interface", (SimulatedRouter router) => new JsonArray
        {
            Interface("*1", "ether1", "ether", router.UplinkRxBytes, router.UplinkTxBytes),
            Interface("*2", "bridge-hotspot", "bridge", router.UplinkTxBytes, router.UplinkRxBytes)
        });

        rest.MapPost("/interface/monitor-traffic", (JsonObject body, SimulatedRouter router) => new JsonArray
        {
            new JsonObject
            {
                ["name"] = body["interface"]?.ToString() ?? "ether1",
                ["rx-bits-per-second"] = (router.LastTxBitsPerSecond + 16_000).ToString(),
                ["tx-bits-per-second"] = (router.LastRxBitsPerSecond + 8_000).ToString()
            }
        });

        rest.MapPost("/execute", (SimulatedRouter router) => new JsonObject
        {
            ["ret"] = $"""
                # {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} by Kibo RouterOS simulator
                # This is synthetic output and not a real RouterOS export.
                /system identity set name={router.Settings.Identity}
                /ip hotspot add interface=bridge-hotspot name=hotspot1 profile=hsprof1
                /ip hotspot profile set hsprof1 use-radius=yes radius-interim-update=1m login-by=http-chap,http-pap
                /radius add address={router.Settings.RadiusServer} service=ppp,hotspot src-address={router.Settings.NasIpAddress}
                /radius incoming set accept=yes port=3799
                """
        });
    }

    private static JsonObject Interface(string id, string name, string type, long rx, long tx) => new()
    {
        [".id"] = id, ["name"] = name, ["type"] = type, ["running"] = "true", ["disabled"] = "false",
        ["rx-byte"] = rx.ToString(), ["tx-byte"] = tx.ToString()
    };

    private static bool IsAuthorized(HttpRequest request, LabOptions options)
    {
        var header = request.Headers.Authorization.ToString();
        if (!header.StartsWith("Basic ", StringComparison.Ordinal))
        {
            return false;
        }

        var expected = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.ApiUsername}:{options.ApiPassword}"));
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(header[6..]), Encoding.ASCII.GetBytes(expected));
    }

    public static string FormatUptime(TimeSpan span)
    {
        var builder = new StringBuilder();
        if (span.Days >= 7) builder.Append($"{span.Days / 7}w");
        if (span.Days % 7 > 0) builder.Append($"{span.Days % 7}d");
        if (span.Hours > 0) builder.Append($"{span.Hours}h");
        if (span.Minutes > 0) builder.Append($"{span.Minutes}m");
        builder.Append($"{span.Seconds}s");
        return builder.ToString();
    }
}
