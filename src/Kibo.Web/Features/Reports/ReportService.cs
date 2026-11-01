using System.Globalization;
using System.Text;
using Kibo.Web.Data;
using Kibo.Web.Data.Entities;
using Kibo.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Kibo.Web.Features.Reports;

public record DayRow(DateOnly Day, decimal Revenue, int Sales, int NewCustomers, long DataBytes, int FailedLogins);

public record BreakdownRow(string Label, decimal Revenue, int Sales);

public record ReportData(
    DateOnly From,
    DateOnly To,
    string Currency,
    decimal GrossSales,
    decimal Refunds,
    decimal Commission,
    int SalesCount,
    int NewCustomers,
    long DataBytes,
    List<DayRow> Days,
    List<BreakdownRow> BySite,
    List<BreakdownRow> ByPackage,
    List<BreakdownRow> ByChannel,
    List<BreakdownRow> ByAgent);

public class ReportService(KiboDb kiboDb, CurrentUser currentUser)
{
    public async Task<ReportData> LoadAsync(DateOnly from, DateOnly to)
    {
        if (!currentUser.Can(Permissions.ViewFinance))
        {
            throw new UnauthorizedAccessException("Finance access is required for reports.");
        }

        await using var db = kiboDb.ForCurrentUser();
        var tenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == db.TenantScope);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(tenant.TimeZone);
        var fromUtc = TimeZoneInfo.ConvertTimeToUtc(from.ToDateTime(TimeOnly.MinValue), zone);
        var toUtc = TimeZoneInfo.ConvertTimeToUtc(to.AddDays(1).ToDateTime(TimeOnly.MinValue), zone);

        DateOnly LocalDay(DateTime utc) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone));

        var ledger = await db.LedgerEntries.AsNoTracking().Where(l => l.CreatedAt >= fromUtc && l.CreatedAt < toUtc).ToListAsync();
        var customers = await db.Customers.AsNoTracking().Where(c => c.CreatedAt >= fromUtc && c.CreatedAt < toUtc)
            .Select(c => c.CreatedAt).ToListAsync();
        var nasNames = db.RadiusNas.Select(n => n.NasName);
        var usage = await db.RadiusAccounting.AsNoTracking()
            .Where(a => a.AcctStartTime >= fromUtc && a.AcctStartTime < toUtc && nasNames.Contains(a.NasIpAddress!))
            .Select(a => new { Start = a.AcctStartTime!.Value, Bytes = (a.AcctInputOctets ?? 0) + (a.AcctOutputOctets ?? 0) })
            .ToListAsync();
        var rejects = await db.RadiusPostAuth.AsNoTracking()
            .Where(p => p.AuthDate >= fromUtc && p.AuthDate < toUtc && p.Reply == "Access-Reject" && nasNames.Contains(p.NasIpAddress!))
            .Select(p => p.AuthDate).ToListAsync();

        var sales = ledger.Where(l => l.Kind == LedgerEntryKind.Sale).ToList();
        var refunds = ledger.Where(l => l.Kind == LedgerEntryKind.Refund).ToList();
        var subscriptionIds = sales.Where(s => s.SubscriptionId != null).Select(s => s.SubscriptionId!.Value).ToList();
        var packageBySubscription = await db.Subscriptions.AsNoTracking().Where(s => subscriptionIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.PackageName);
        var sites = await db.Sites.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Name);
        var agents = await db.Agents.AsNoTracking().ToDictionaryAsync(a => a.Id, a => a.Name);

        var days = new List<DayRow>();
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            var current = day;
            days.Add(new DayRow(
                day,
                sales.Where(s => LocalDay(s.CreatedAt) == current).Sum(s => s.Amount) -
                refunds.Where(r => LocalDay(r.CreatedAt) == current).Sum(r => r.Amount),
                sales.Count(s => LocalDay(s.CreatedAt) == current),
                customers.Count(c => LocalDay(c) == current),
                usage.Where(u => LocalDay(u.Start) == current).Sum(u => u.Bytes),
                rejects.Count(r => LocalDay(r) == current)));
        }

        static List<BreakdownRow> Breakdown(IEnumerable<LedgerEntry> rows, Func<LedgerEntry, string> label) => rows
            .GroupBy(label)
            .Select(g => new BreakdownRow(g.Key, g.Sum(x => x.Amount), g.Count()))
            .OrderByDescending(b => b.Revenue)
            .ToList();

        return new ReportData(
            from, to, tenant.Currency,
            sales.Sum(s => s.Amount),
            refunds.Sum(r => r.Amount),
            ledger.Where(l => l.Kind == LedgerEntryKind.AgentCommission).Sum(l => l.Amount),
            sales.Count,
            customers.Count,
            usage.Sum(u => u.Bytes),
            days,
            Breakdown(sales, s => s.SiteId != null && sites.TryGetValue(s.SiteId.Value, out var name) ? name : "Unassigned"),
            Breakdown(sales, s => s.SubscriptionId != null && packageBySubscription.TryGetValue(s.SubscriptionId.Value, out var name) ? name : "Other"),
            Breakdown(sales, s => s.PaymentId != null ? "Mobile money" : s.VoucherId != null ? "Vouchers" : "Cash at counter"),
            Breakdown(sales.Where(s => s.AgentId != null), s => agents.GetValueOrDefault(s.AgentId!.Value, "Unknown agent")));
    }

    public static string ToCsv(ReportData data)
    {
        var csv = new StringBuilder();
        csv.AppendLine("date,revenue,sales,new_customers,data_mb,failed_logins");
        foreach (var day in data.Days)
        {
            csv.AppendLine(string.Join(',',
                day.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                day.Revenue.ToString(CultureInfo.InvariantCulture),
                day.Sales,
                day.NewCustomers,
                (day.DataBytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture),
                day.FailedLogins));
        }

        return csv.ToString();
    }
}
