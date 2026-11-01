using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kibo.Web.Data;
using Kibo.Web.Data.Entities;
using Kibo.Web.Features.Customers;
using Kibo.Web.Features.Packages;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Kibo.Web.Features.Portal;

public record RouterContext(string? Mac, string? Ip, string? LoginUrl, string? Destination, string? ChapId,
    string? ChapChallenge, string? Error);

public record PortalSite(Site Site, Tenant Tenant, List<Package> Packages);

public record CustomerStatusView(string Username, string? PackageName, SubscriptionStatus? Status, DateTime? ExpiresAt,
    long? DataQuotaBytes, long UsedBytes, long? TimeQuotaSeconds, long UsedSeconds, int ScheduledCount);

public class PortalService(KiboDb kiboDb, IDataProtectionProvider protection, IWebHostEnvironment environment,
    TimeProvider clock)
{
    public const string ContextCookie = "kibo.portal";
    private readonly IDataProtector protector = protection.CreateProtector("Kibo.Portal.RouterContext.v1");

    public async Task<PortalSite?> LoadSiteAsync(string slug)
    {
        await using var db = kiboDb.ForSystem();
        var site = await db.Sites.AsNoTracking().FirstOrDefaultAsync(s => s.Slug == slug);
        if (site == null)
        {
            return null;
        }

        var tenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == site.TenantId);
        var now = clock.GetUtcNow().UtcDateTime;
        var packages = await db.Packages.AsNoTracking()
            .Where(p => p.TenantId == site.TenantId && p.IsActive && p.ShowOnPortal &&
                        p.ServiceType == ServiceType.Hotspot && (p.SiteId == null || p.SiteId == site.Id))
            .OrderBy(p => p.SortOrder).ThenBy(p => p.Price)
            .ToListAsync();
        return new PortalSite(site, tenant, packages.Where(p => PackageCatalog.IsAvailable(p, now)).ToList());
    }

    public RouterContext ReadContext(HttpContext http)
    {
        var query = http.Request.Query;
        if (query.ContainsKey("login") || query.ContainsKey("mac"))
        {
            var fromRouter = new RouterContext(
                Clean(query["mac"]), Clean(query["ip"]), Clean(query["login"]), Clean(query["dst"]),
                Clean(query["chapid"]), Clean(query["chapch"]), Clean(query["error"]));
            http.Response.Cookies.Append(ContextCookie, protector.Protect(JsonSerializer.Serialize(fromRouter)),
                new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Lax, MaxAge = TimeSpan.FromHours(2) });
            return fromRouter;
        }

        if (http.Request.Cookies.TryGetValue(ContextCookie, out var cookie))
        {
            try
            {
                return JsonSerializer.Deserialize<RouterContext>(protector.Unprotect(cookie))!;
            }
            catch (CryptographicException)
            {
            }
        }

        return new RouterContext(null, null, null, null, null, null, null);
    }

    public string? BuildRouterLoginUrl(RouterContext context, string username, string password)
    {
        if (!IsAllowedLoginUrl(context.LoginUrl))
        {
            return null;
        }

        var secret = password;
        if (!string.IsNullOrEmpty(context.ChapId) && !string.IsNullOrEmpty(context.ChapChallenge))
        {
            var input = new List<byte>();
            input.AddRange(DecodeRouterOsString(context.ChapId));
            input.AddRange(Encoding.UTF8.GetBytes(password));
            input.AddRange(DecodeRouterOsString(context.ChapChallenge));
            secret = Convert.ToHexStringLower(MD5.HashData(input.ToArray()));
        }

        var url = new StringBuilder(context.LoginUrl);
        url.Append(context.LoginUrl!.Contains('?') ? '&' : '?');
        url.Append("username=").Append(Uri.EscapeDataString(username));
        url.Append("&password=").Append(Uri.EscapeDataString(secret));
        if (!string.IsNullOrEmpty(context.Destination))
        {
            url.Append("&dst=").Append(Uri.EscapeDataString(context.Destination));
        }

        return url.ToString();
    }

    public bool IsAllowedLoginUrl(string? loginUrl)
    {
        if (!Uri.TryCreate(loginUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return false;
        }

        if (environment.IsDevelopment() && uri.IsLoopback)
        {
            return true;
        }

        if (!IPAddress.TryParse(uri.Host, out var address))
        {
            return uri.Host.EndsWith(".hotspot", StringComparison.OrdinalIgnoreCase) ||
                   uri.Host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase);
        }

        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && (bytes[0] == 10 ||
                                     (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                                     (bytes[0] == 192 && bytes[1] == 168) ||
                                     (bytes[0] == 100 && bytes[1] is >= 64 and <= 127));
    }

    public static byte[] DecodeRouterOsString(string value)
    {
        var bytes = new List<byte>();
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && IsOctal(value, i + 1))
            {
                bytes.Add(Convert.ToByte(value.Substring(i + 1, 3), 8));
                i += 3;
            }
            else
            {
                bytes.Add((byte)value[i]);
            }
        }

        return bytes.ToArray();
    }

    private static bool IsOctal(string value, int start) =>
        start + 3 <= value.Length && value.Substring(start, 3).All(c => c is >= '0' and <= '7');

    public async Task<OperationResult<AccessAccount>> FindAccountForLoginAsync(Guid tenantId, string phone, string code)
    {
        var normalized = PhoneNumbers.NormalizeUganda(phone);
        if (normalized == null)
        {
            return OperationResult<AccessAccount>.Fail("Enter the phone number you paid with.");
        }

        await using var db = kiboDb.ForTenant(tenantId);
        var account = await db.AccessAccounts.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Username == normalized && a.Kind == AccessAccountKind.Hotspot);
        if (account == null || !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(account.Password), Encoding.UTF8.GetBytes(code.Trim())))
        {
            return OperationResult<AccessAccount>.Fail("That phone number and access code do not match.");
        }

        return OperationResult<AccessAccount>.Ok(account);
    }

    public async Task<CustomerStatusView?> GetStatusAsync(Guid tenantId, Guid accessAccountId)
    {
        await using var db = kiboDb.ForTenant(tenantId);
        var account = await db.AccessAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == accessAccountId);
        if (account == null)
        {
            return null;
        }

        var subscriptions = await db.Subscriptions.AsNoTracking()
            .Where(s => s.AccessAccountId == accessAccountId &&
                        (s.Status == SubscriptionStatus.Active || s.Status == SubscriptionStatus.Unused ||
                         s.Status == SubscriptionStatus.Scheduled))
            .ToListAsync();
        var current = subscriptions.Where(s => s.Status != SubscriptionStatus.Scheduled)
            .OrderByDescending(s => s.Status == SubscriptionStatus.Active).FirstOrDefault();
        var latestEnded = current == null
            ? await db.Subscriptions.AsNoTracking().Where(s => s.AccessAccountId == accessAccountId)
                .OrderByDescending(s => s.PurchasedAt).FirstOrDefaultAsync()
            : null;
        var shown = current ?? latestEnded;

        return new CustomerStatusView(account.Username, shown?.PackageName, shown?.Status, shown?.ExpiresAt,
            shown?.DataQuotaBytes, shown?.UsedBytes ?? 0, shown?.TimeQuotaSeconds, shown?.UsedSeconds ?? 0,
            subscriptions.Count(s => s.Status == SubscriptionStatus.Scheduled));
    }

    public async Task<AccessAccount?> GetAccountAsync(Guid tenantId, Guid accessAccountId)
    {
        await using var db = kiboDb.ForTenant(tenantId);
        return await db.AccessAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == accessAccountId);
    }

    public string? NormalizeVoucherCode(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var code = new string(input.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
        return code.Length is >= 6 and <= 16 ? code : null;
    }

    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length > 512 ? trimmed[..512] : trimmed;
    }
}
