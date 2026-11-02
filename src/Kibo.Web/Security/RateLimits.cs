using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Kibo.Web.Features.Portal;

namespace Kibo.Web.Security;

public static class RateLimits
{
    public const int PerBrowserPerMinute = 10;
    public const int PerAddressPerMinute = 300;

    public static PartitionedRateLimiter<HttpContext> Create() => PartitionedRateLimiter.CreateChained(
        PartitionedRateLimiter.Create<HttpContext, string>(http => Partition(http, perBrowser: true)),
        PartitionedRateLimiter.Create<HttpContext, string>(http => Partition(http, perBrowser: false)));

    private static RateLimitPartition<string> Partition(HttpContext http, bool perBrowser)
    {
        var path = http.Request.Path.Value ?? "";
        var sensitive = HttpMethods.IsPost(http.Request.Method) && (path.StartsWith("/p/") || path.StartsWith("/account/login"));
        if (!sensitive)
        {
            return RateLimitPartition.GetNoLimiter("unlimited");
        }

        var address = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (!perBrowser)
        {
            return RateLimitPartition.GetFixedWindowLimiter($"address:{address}", _ => Window(PerAddressPerMinute));
        }

        var browser = http.Request.Cookies.TryGetValue(PortalService.ContextCookie, out var cookie) && cookie.Length > 0
            ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cookie)))[..16]
            : "no-cookie";
        var action = path.Split('/').ElementAtOrDefault(3) ?? path;
        return RateLimitPartition.GetFixedWindowLimiter($"browser:{address}:{browser}:{action}", _ => Window(PerBrowserPerMinute));
    }

    private static FixedWindowRateLimiterOptions Window(int permits) =>
        new() { PermitLimit = permits, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 };
}
