using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Kibo.Web.Features.Routers;

public record RouterConnection(string Host, int Port, bool UseTls, bool AllowSelfSigned, string Username, string Password);

public record RouterResource(string Version, string BoardName, string Architecture, int CpuLoadPercent,
    long FreeMemoryBytes, long TotalMemoryBytes, long UptimeSeconds);

public record RouterInterface(string Id, string Name, string Type, bool Running, bool Disabled, long RxBytes, long TxBytes);

public record HotspotActiveSession(string Id, string User, string Address, string MacAddress, long UptimeSeconds,
    long BytesIn, long BytesOut, string Server);

public record HotspotServer(string Name, string Interface, string Profile, bool Disabled);

public record RadiusClientEntry(string Address, string Service, bool Disabled);

public enum RouterFailureKind
{
    Unreachable,
    Timeout,
    AuthenticationFailed,
    TlsFailure,
    ApiError
}

public class RouterOsException(RouterFailureKind kind, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public RouterFailureKind Kind { get; } = kind;
}

public partial class RouterOsClient(IHttpClientFactory httpClientFactory)
{
    public const string TrustingClientName = "routeros-self-signed";
    public const string StrictClientName = "routeros";

    public async Task<RouterResource> GetResourceAsync(RouterConnection connection, CancellationToken ct = default)
    {
        var json = await GetObjectAsync(connection, "system/resource", ct);
        return new RouterResource(
            Version: Text(json, "version"),
            BoardName: Text(json, "board-name"),
            Architecture: Text(json, "architecture-name"),
            CpuLoadPercent: (int)Number(json, "cpu-load"),
            FreeMemoryBytes: Number(json, "free-memory"),
            TotalMemoryBytes: Number(json, "total-memory"),
            UptimeSeconds: ParseDuration(Text(json, "uptime")));
    }

    public async Task<string> GetIdentityAsync(RouterConnection connection, CancellationToken ct = default)
    {
        var json = await GetObjectAsync(connection, "system/identity", ct);
        return Text(json, "name");
    }

    public async Task<string?> GetSerialNumberAsync(RouterConnection connection, CancellationToken ct = default)
    {
        try
        {
            var json = await GetObjectAsync(connection, "system/routerboard", ct);
            var serial = Text(json, "serial-number");
            return string.IsNullOrEmpty(serial) ? null : serial;
        }
        catch (RouterOsException ex) when (ex.Kind == RouterFailureKind.ApiError)
        {
            return null;
        }
    }

    public async Task<List<HotspotServer>> GetHotspotServersAsync(RouterConnection connection, CancellationToken ct = default)
    {
        var items = await GetArrayAsync(connection, "ip/hotspot", ct);
        return items.Select(i => new HotspotServer(Text(i, "name"), Text(i, "interface"), Text(i, "profile"),
            Bool(i, "disabled"))).ToList();
    }

    public async Task<List<RadiusClientEntry>> GetRadiusEntriesAsync(RouterConnection connection, CancellationToken ct = default)
    {
        var items = await GetArrayAsync(connection, "radius", ct);
        return items.Select(i => new RadiusClientEntry(Text(i, "address"), Text(i, "service"), Bool(i, "disabled"))).ToList();
    }

    public async Task<List<RouterInterface>> GetInterfacesAsync(RouterConnection connection, CancellationToken ct = default)
    {
        var items = await GetArrayAsync(connection, "interface", ct);
        return items.Select(i => new RouterInterface(Text(i, ".id"), Text(i, "name"), Text(i, "type"),
            Bool(i, "running"), Bool(i, "disabled"), Number(i, "rx-byte"), Number(i, "tx-byte"))).ToList();
    }

    public async Task<List<HotspotActiveSession>> GetHotspotActiveAsync(RouterConnection connection, CancellationToken ct = default)
    {
        var items = await GetArrayAsync(connection, "ip/hotspot/active", ct);
        return items.Select(i => new HotspotActiveSession(Text(i, ".id"), Text(i, "user"), Text(i, "address"),
            Text(i, "mac-address"), ParseDuration(Text(i, "uptime")), Number(i, "bytes-in"), Number(i, "bytes-out"),
            Text(i, "server"))).ToList();
    }

    public async Task RemoveHotspotActiveAsync(RouterConnection connection, string sessionId, CancellationToken ct = default)
    {
        await PostAsync(connection, "ip/hotspot/active/remove", new JsonObject { [".id"] = sessionId }, ct);
    }

    public async Task RemovePppActiveAsync(RouterConnection connection, string sessionId, CancellationToken ct = default)
    {
        await PostAsync(connection, "ppp/active/remove", new JsonObject { [".id"] = sessionId }, ct);
    }

    public async Task<(long RxBitsPerSecond, long TxBitsPerSecond)> MonitorTrafficAsync(RouterConnection connection,
        string interfaceName, CancellationToken ct = default)
    {
        var body = new JsonObject { ["interface"] = interfaceName, ["once"] = "" };
        var result = await PostAsync(connection, "interface/monitor-traffic", body, ct);
        var first = result is JsonArray array && array.Count > 0 ? array[0] as JsonObject : result as JsonObject;
        if (first == null)
        {
            return (0, 0);
        }

        return (Number(first, "rx-bits-per-second"), Number(first, "tx-bits-per-second"));
    }

    public async Task<string> ExportConfigurationAsync(RouterConnection connection, CancellationToken ct = default)
    {
        var body = new JsonObject { ["script"] = "/export terse", ["as-string"] = "" };
        var result = await PostAsync(connection, "execute", body, ct);
        return result is JsonObject obj ? Text(obj, "ret") : result?.ToString() ?? "";
    }

    private async Task<JsonObject> GetObjectAsync(RouterConnection connection, string path, CancellationToken ct)
    {
        var node = await SendAsync(connection, HttpMethod.Get, path, null, ct);
        return node as JsonObject ?? throw new RouterOsException(RouterFailureKind.ApiError, $"Unexpected response for /{path}.");
    }

    private async Task<List<JsonObject>> GetArrayAsync(RouterConnection connection, string path, CancellationToken ct)
    {
        var node = await SendAsync(connection, HttpMethod.Get, path, null, ct);
        return node is JsonArray array ? array.OfType<JsonObject>().ToList() : [];
    }

    private Task<JsonNode?> PostAsync(RouterConnection connection, string path, JsonObject body, CancellationToken ct) =>
        SendAsync(connection, HttpMethod.Post, path, body, ct);

    private async Task<JsonNode?> SendAsync(RouterConnection connection, HttpMethod method, string path, JsonObject? body,
        CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(connection.AllowSelfSigned ? TrustingClientName : StrictClientName);
        var scheme = connection.UseTls ? "https" : "http";
        using var request = new HttpRequestMessage(method, $"{scheme}://{connection.Host}:{connection.Port}/rest/{path}");
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{connection.Username}:{connection.Password}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
        if (body != null)
        {
            request.Content = JsonContent.Create(body);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new RouterOsException(RouterFailureKind.Timeout, "The router did not respond within 10 seconds.");
        }
        catch (HttpRequestException ex) when (ex.InnerException is System.Security.Authentication.AuthenticationException)
        {
            throw new RouterOsException(RouterFailureKind.TlsFailure,
                "TLS handshake failed. Check the router certificate or allow its self-signed certificate.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new RouterOsException(RouterFailureKind.Unreachable, $"Could not reach the router: {ex.Message}", ex);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new RouterOsException(RouterFailureKind.AuthenticationFailed,
                    "The router rejected the API username or password.");
            }

            var content = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new RouterOsException(RouterFailureKind.ApiError,
                    $"Router API returned {(int)response.StatusCode} for /{path}: {Truncate(content, 200)}");
            }

            if (string.IsNullOrWhiteSpace(content))
            {
                return null;
            }

            try
            {
                return JsonNode.Parse(content);
            }
            catch (JsonException ex)
            {
                throw new RouterOsException(RouterFailureKind.ApiError, $"Router returned invalid JSON for /{path}.", ex);
            }
        }
    }

    private static string Text(JsonObject json, string key) => json[key]?.ToString() ?? "";

    private static long Number(JsonObject json, string key) =>
        long.TryParse(json[key]?.ToString(), out var value) ? value : 0;

    private static bool Bool(JsonObject json, string key) =>
        string.Equals(json[key]?.ToString(), "true", StringComparison.OrdinalIgnoreCase);

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length];

    public static long ParseDuration(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        long total = 0;
        foreach (Match match in DurationPart().Matches(value))
        {
            var amount = long.Parse(match.Groups[1].Value);
            total += match.Groups[2].Value switch
            {
                "w" => amount * 604800,
                "d" => amount * 86400,
                "h" => amount * 3600,
                "m" => amount * 60,
                "s" => amount,
                _ => 0
            };
        }

        var clock = ClockPart().Match(value);
        if (clock.Success)
        {
            total += long.Parse(clock.Groups[1].Value) * 3600 + long.Parse(clock.Groups[2].Value) * 60 +
                     long.Parse(clock.Groups[3].Value);
        }

        return total;
    }

    [GeneratedRegex(@"(\d+)(w|d|h|m|s)(?!s)")]
    private static partial Regex DurationPart();

    [GeneratedRegex(@"(\d{1,2}):(\d{2}):(\d{2})$")]
    private static partial Regex ClockPart();
}
