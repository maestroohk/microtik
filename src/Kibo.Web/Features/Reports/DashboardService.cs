using Kibo.Web.Data;
using Kibo.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Kibo.Web.Features.Reports;

public record DailyRevenue(DateOnly Day, decimal Amount, int Sales);

public record SiteRevenue(string Site, decimal Amount, int Sales);

public record RecentPayment(string Reference, string Phone, string PackageName, decimal Amount, string Currency,
    PaymentStatus Status, DateTime CreatedAt);

public record DashboardData(
    int RoutersOnline,
    int RoutersTotal,
    int ActiveSessions,
    int ActiveSubscriptions,
    int ExpiringToday,
    decimal RevenueToday,
    decimal RevenueYesterday,
    decimal Revenue30Days,
    string Currency,
    int NewCustomers7Days,
    int FailedLogins24h,
    int PaymentFailures24h,
    bool HasSyntheticData,
    List<Router> Routers,
    List<DailyRevenue> Revenue14Days,
    List<SiteRevenue> RevenueBySite,
    List<RecentPayment> RecentPayments);

public class DashboardService(KiboDb kiboDb, TimeProvider clock)
{
    public async Task<DashboardData> LoadAsync()
    {
        await using var db = kiboDb.ForCurrentUser();
        var tenantId = db.TenantScope ?? throw new UnauthorizedAccessException("Select an operator first.");
        var tenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == tenantId);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(tenant.TimeZone);
        var now = clock.GetUtcNow().UtcDateTime;
        var localToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(now, zone));
        var todayStartUtc = TimeZoneInfo.ConvertTimeToUtc(localToday.ToDateTime(TimeOnly.MinValue), zone);
        var since14 = todayStartUtc.AddDays(-13);

        var routers = await db.Routers.AsNoTracking().Include(r => r.Site).OrderBy(r => r.Name).ToListAsync();
        var nasNames = db.RadiusNas.Select(n => n.NasName);

        var activeSessions = await db.RadiusAccounting.CountAsync(a => a.AcctStopTime == null && nasNames.Contains(a.NasIpAddress!));
        var activeSubscriptions = await db.Subscriptions.CountAsync(s => s.Status == SubscriptionStatus.Active);
        var expiringToday = await db.Subscriptions.CountAsync(s => s.Status == SubscriptionStatus.Active &&
                                                                   s.ExpiresAt >= now && s.ExpiresAt < todayStartUtc.AddDays(1));

        var sales = await db.LedgerEntries.AsNoTracking()
            .Where(l => l.CreatedAt >= todayStartUtc.AddDays(-29) && (l.Kind == LedgerEntryKind.Sale || l.Kind == LedgerEntryKind.Refund))
            .Select(l => new { l.CreatedAt, l.Amount, l.Kind, l.SiteId })
            .ToListAsync();

        decimal Signed(LedgerEntryKind kind, decimal amount) => kind == LedgerEntryKind.Refund ? -amount : amount;

        var revenueByDay = Enumerable.Range(0, 14).Select(i =>
        {
            var dayStart = since14.AddDays(i);
            var daySales = sales.Where(s => s.CreatedAt >= dayStart && s.CreatedAt < dayStart.AddDays(1)).ToList();
            return new DailyRevenue(localToday.AddDays(i - 13), daySales.Sum(s => Signed(s.Kind, s.Amount)),
                daySales.Count(s => s.Kind == LedgerEntryKind.Sale));
        }).ToList();

        var sites = await db.Sites.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Name);
        var bySite = sales.Where(s => s.CreatedAt >= todayStartUtc.AddDays(-6))
            .GroupBy(s => s.SiteId)
            .Select(g => new SiteRevenue(g.Key != null && sites.TryGetValue(g.Key.Value, out var name) ? name : "Unassigned",
                g.Sum(s => Signed(s.Kind, s.Amount)), g.Count(s => s.Kind == LedgerEntryKind.Sale)))
            .OrderByDescending(s => s.Amount)
            .ToList();

        var since24h = now.AddHours(-24);
        var failedLogins = await db.RadiusPostAuth.CountAsync(p => p.AuthDate >= since24h && p.Reply == "Access-Reject" &&
                                                                   nasNames.Contains(p.NasIpAddress!));
        var paymentFailures = await db.Payments.CountAsync(p => p.CreatedAt >= since24h &&
                                                                (p.Status == PaymentStatus.Failed || p.Status == PaymentStatus.Expired));

        var packages = await db.Packages.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name);
        var recent = (await db.Payments.AsNoTracking().OrderByDescending(p => p.CreatedAt).Take(8).ToListAsync())
            .Select(p => new RecentPayment(p.Reference, p.PhoneNumber, packages.GetValueOrDefault(p.PackageId, "—"),
                p.Amount, p.Currency, p.Status, p.CreatedAt))
            .ToList();

        return new DashboardData(
            routers.Count(r => r.Status == RouterStatus.Online),
            routers.Count,
            activeSessions,
            activeSubscriptions,
            expiringToday,
            revenueByDay[^1].Amount,
            revenueByDay[^2].Amount,
            sales.Sum(s => Signed(s.Kind, s.Amount)),
            tenant.Currency,
            await db.Customers.CountAsync(c => c.CreatedAt >= now.AddDays(-7)),
            failedLogins,
            paymentFailures,
            routers.Any(r => r.ApiKind == RouterApiKind.Simulator),
            routers,
            revenueByDay,
            bySite,
            recent);
    }
}
