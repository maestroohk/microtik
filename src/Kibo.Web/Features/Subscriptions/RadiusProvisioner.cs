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
        DateTime countingSince, CancellationToken ct = default)
    {
        var nasAddresses = db.RadiusNas.IgnoreQueryFilters().Where(n => n.TenantId == tenantId).Select(n => n.NasName);
        var sessions = db.RadiusAccounting
            .Where(a => a.UserName == username && a.AcctStartTime >= countingSince && nasAddresses.Contains(a.NasIpAddress!));

        var totals = await sessions
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Bytes = g.Sum(a => (a.AcctInputOctets ?? 0) + (a.AcctOutputOctets ?? 0)),
                Seconds = g.Sum(a => a.AcctSessionTime ?? 0),
                First = g.Min(a => a.AcctStartTime)
            })
            .FirstOrDefaultAsync(ct);

        return totals == null ? new UsageTotals(0, 0, null) : new UsageTotals(totals.Bytes, totals.Seconds, totals.First);
    }
}
