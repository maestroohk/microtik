using Kibo.Web.Data;
using Kibo.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Kibo.Web.Features.Subscriptions;

public static class RadiusProvisioner
{
    public static async Task SyncAccountAsync(AppDbContext db, Guid accessAccountId, DateTime now, CancellationToken ct = default)
    {
        var account = await db.AccessAccounts.IgnoreQueryFilters().Include(a => a.Customer)
            .FirstOrDefaultAsync(a => a.Id == accessAccountId, ct);
        var existing = await db.RadiusAccess.IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.AccessAccountId == accessAccountId, ct);

        if (account == null)
        {
            if (existing != null)
            {
                db.RadiusAccess.Remove(existing);
            }

            return;
        }

        var tenant = await db.Tenants.FirstAsync(t => t.Id == account.TenantId, ct);
        var current = await db.Subscriptions.IgnoreQueryFilters()
            .Where(s => s.AccessAccountId == account.Id &&
                        (s.Status == SubscriptionStatus.Active || s.Status == SubscriptionStatus.Unused))
            .OrderByDescending(s => s.Status == SubscriptionStatus.Active)
            .ThenBy(s => s.PurchasedAt)
            .FirstOrDefaultAsync(ct);

        var desired = Entitlements.BuildRadiusAccess(account, current, tenant, account.Customer, now);

        if (existing != null && existing.Username != desired.Username)
        {
            db.RadiusAccess.Remove(existing);
            existing = null;
        }

        if (existing == null)
        {
            db.RadiusAccess.Add(desired);
            return;
        }

        db.Entry(existing).CurrentValues.SetValues(desired);
    }

    public static async Task<UsageTotals> ReadUsageAsync(AppDbContext db, Guid tenantId, string username,
        Guid subscriptionId, DateTime countingSince, CancellationToken ct = default)
    {
        var sessions = db.RadiusAccounting.Where(a => a.TenantId == tenantId &&
            (a.SubscriptionId == subscriptionId ||
             (a.SubscriptionId == null && a.UserName == username && a.AcctStartTime >= countingSince)));

        var totals = await sessions
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Upload = g.Sum(a => a.AcctInputOctets ?? 0),
                Download = g.Sum(a => a.AcctOutputOctets ?? 0),
                Seconds = g.Sum(a => a.AcctSessionTime ?? 0),
                First = g.Min(a => a.AcctStartTime),
                Observed = g.Max(a => a.AcctUpdateTime ?? a.AcctStartTime),
                Open = g.Count(a => a.AcctStopTime == null)
            })
            .FirstOrDefaultAsync(ct);

        return totals == null
            ? new UsageTotals(0, 0, null)
            : new UsageTotals(totals.Upload + totals.Download, totals.Seconds, totals.First, totals.Observed, totals.Open,
                totals.Upload, totals.Download);
    }
}
