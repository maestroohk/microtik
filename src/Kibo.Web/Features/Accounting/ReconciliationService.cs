using Kibo.Web.Data;
using Kibo.Web.Data.Entities;
using Kibo.Web.Features.Routers;
using Kibo.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Kibo.Web.Features.Accounting;

public enum UsageState
{
    Verified,
    Live,
    Stale,
    Unavailable
}

public enum FindingSeverity
{
    Info,
    Warning,
    Critical
}

public record Finding(FindingSeverity Severity, string Area, string Subject, string Detail, long? RadAcctId = null);

public record RouterAccountingHealth(Guid RouterId, string RouterName, RouterStatus Status, DateTime? LastPacketAt,
    int OpenSessions, int StaleSessions, int? RouterSessions);

public record AccountingHealth(DateTime CheckedAt, DateTime? LastPacketAt, int EventsLastHour, int OpenSessions,
    int StaleSessions, double? CoaSuccessRate, int CoaAttempts, List<RouterAccountingHealth> Routers, List<Finding> Findings);

public record SessionEvidence(RadiusAccounting Session, List<RadiusAccountingEvent> Events);

public class ReconciliationService(KiboDb kiboDb, CurrentUser currentUser, RouterOsClient routerClient,
    RouterHealthChecker healthChecker, TimeProvider clock)
{
    public static readonly TimeSpan LiveWindow = TimeSpan.FromSeconds(150);
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);

    public static UsageState Classify(IEnumerable<(DateTime? LastUpdate, bool Open, RouterStatus RouterStatus)> sessions, DateTime now)
    {
        var open = sessions.Where(s => s.Open).ToList();
        if (open.Count == 0)
        {
            return UsageState.Verified;
        }

        if (open.Any(s => s.RouterStatus is RouterStatus.Offline or RouterStatus.AuthenticationFailed))
        {
            return UsageState.Unavailable;
        }

        return open.All(s => s.LastUpdate != null && now - s.LastUpdate.Value <= LiveWindow) ? UsageState.Live : UsageState.Stale;
    }

    public async Task<UsageState> UsageStateForSubscriptionAsync(Guid subscriptionId)
    {
        await using var db = kiboDb.ForCurrentUser();
        var sessions = await (
                from session in db.RadiusAccounting
                join router in db.Routers on session.RouterId equals router.Id into routers
                from router in routers.DefaultIfEmpty()
                where session.SubscriptionId == subscriptionId && session.AcctStopTime == null
                select new { session.AcctUpdateTime, Status = router == null ? RouterStatus.Offline : router.Status })
            .ToListAsync();
        return Classify(sessions.Select(s => (s.AcctUpdateTime, true, s.Status)), clock.GetUtcNow().UtcDateTime);
    }

    public async Task<AccountingHealth> CheckAsync(bool queryRouters, CancellationToken ct = default)
    {
        if (!currentUser.Can(Permissions.ViewRouters))
        {
            throw new UnauthorizedAccessException("Missing permission to view accounting health.");
        }

        var now = clock.GetUtcNow().UtcDateTime;
        await using var db = kiboDb.ForCurrentUser();
        var tenantId = db.TenantScope ?? throw new UnauthorizedAccessException("Select an operator first.");
        var findings = new List<Finding>();

        var routers = await db.Routers.AsNoTracking().OrderBy(r => r.Name).ToListAsync(ct);
        var lastPacketByRouter = await db.RadiusAccountingEvents.AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.ReceivedAt > now.AddDays(-7))
            .Join(db.RadiusNas, e => e.NasIpAddress, n => n.NasName, (e, n) => new { n.RouterId, e.ReceivedAt })
            .GroupBy(x => x.RouterId)
            .Select(g => new { RouterId = g.Key, Last = g.Max(x => x.ReceivedAt) })
            .ToDictionaryAsync(x => x.RouterId, x => (DateTime?)x.Last, ct);
        var eventsLastHour = await db.RadiusAccountingEvents.CountAsync(e => e.TenantId == tenantId && e.ReceivedAt > now.AddHours(-1), ct);

        var open = await db.RadiusAccounting.AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.AcctStopTime == null)
            .Select(a => new { a.RadAcctId, a.RouterId, a.UserName, a.AcctUpdateTime, a.AcctStartTime, a.SubscriptionId })
            .ToListAsync(ct);

        var routerHealth = new List<RouterAccountingHealth>();
        foreach (var router in routers)
        {
            var mine = open.Where(o => o.RouterId == router.Id).ToList();
            var stale = mine.Count(o => now - (o.AcctUpdateTime ?? o.AcctStartTime ?? now) > LiveWindow);
            int? onRouter = null;

            if (queryRouters && router.Status == RouterStatus.Online)
            {
                try
                {
                    var connection = healthChecker.ConnectionFor(router);
                    var hotspot = await routerClient.GetHotspotActiveAsync(connection, ct);
                    var ppp = await routerClient.GetPppActiveAsync(connection, ct);
                    var routerUsers = hotspot.Select(h => h.User).Concat(ppp.Select(p => p.Name)).ToList();
                    onRouter = routerUsers.Count;
                    var kiboUsers = mine.Select(m => m.UserName ?? "").ToList();

                    foreach (var user in routerUsers.Except(kiboUsers).Distinct())
                    {
                        findings.Add(new Finding(FindingSeverity.Critical, "Router vs accounting", user,
                            $"Online on {router.Name} but Kibo has no open accounting session. Usage is not being counted."));
                    }

                    foreach (var session in mine.Where(m => !routerUsers.Contains(m.UserName ?? "")))
                    {
                        findings.Add(new Finding(FindingSeverity.Warning, "Router vs accounting", session.UserName ?? "",
                            $"Open in Kibo but not active on {router.Name}; the accounting stop was probably lost.", session.RadAcctId));
                    }
                }
                catch (RouterOsException ex)
                {
                    findings.Add(new Finding(FindingSeverity.Warning, "Router API", router.Name, $"Could not read active sessions: {ex.Message}"));
                }
            }

            if (mine.Count > 0 && router.Status != RouterStatus.Online)
            {
                findings.Add(new Finding(FindingSeverity.Warning, "Accounting freshness", router.Name,
                    $"{mine.Count} session(s) open while the router is {router.Status}. Their usage is unavailable until it reconnects."));
            }

            lastPacketByRouter.TryGetValue(router.Id, out var lastPacket);
            if (mine.Count > 0 && (lastPacket == null || now - lastPacket.Value > LiveWindow))
            {
                findings.Add(new Finding(FindingSeverity.Critical, "Accounting freshness", router.Name,
                    $"No accounting packet for {(lastPacket == null ? "a long time" : $"{(int)(now - lastPacket.Value).TotalMinutes} min")} while sessions are open. Check RADIUS reachability."));
            }

            routerHealth.Add(new RouterAccountingHealth(router.Id, router.Name, router.Status, lastPacket, mine.Count, stale, onRouter));
        }

        var current = await db.Subscriptions.AsNoTracking()
            .Where(s => s.Status == SubscriptionStatus.Active || s.Status == SubscriptionStatus.Unused)
            .Select(s => new { s.Id, s.PackageName, s.UsedBytes, s.UsageUpdatedAt, s.DataQuotaBytes, s.ThrottledAt, s.FairUseEnforcement })
            .ToListAsync(ct);
        var ids = current.Select(c => c.Id).ToList();
        var normalized = await db.RadiusAccounting.AsNoTracking()
            .Where(a => a.SubscriptionId != null && ids.Contains(a.SubscriptionId.Value))
            .GroupBy(a => a.SubscriptionId!.Value)
            .Select(g => new { Id = g.Key, Bytes = g.Sum(a => (a.AcctInputOctets ?? 0) + (a.AcctOutputOctets ?? 0)) })
            .ToDictionaryAsync(x => x.Id, x => x.Bytes, ct);

        foreach (var subscription in current)
        {
            var sessionBytes = normalized.GetValueOrDefault(subscription.Id);
            if (sessionBytes != subscription.UsedBytes &&
                (subscription.UsageUpdatedAt == null || now - subscription.UsageUpdatedAt.Value > TimeSpan.FromMinutes(2)))
            {
                findings.Add(new Finding(FindingSeverity.Warning, "Sessions vs subscription", subscription.PackageName,
                    $"Subscription shows {subscription.UsedBytes:N0} bytes but its sessions total {sessionBytes:N0}; the usage worker is behind."));
            }

            if (subscription.DataQuotaBytes != null && sessionBytes > subscription.DataQuotaBytes && subscription.ThrottledAt == null &&
                subscription.FairUseEnforcement == FairUseEnforcement.RateChange && subscription.UsageUpdatedAt != null)
            {
                findings.Add(new Finding(FindingSeverity.Warning, "Fair use", subscription.PackageName,
                    "Over the fair-use threshold but not yet throttled."));
            }
        }

        var raw = await db.RadiusAccountingEvents.AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.RadAcctId != null && e.ReceivedAt > now.AddDays(-1))
            .GroupBy(e => e.RadAcctId!.Value)
            .Select(g => new { Id = g.Key, In = g.Max(e => e.InputBytes), Out = g.Max(e => e.OutputBytes) })
            .Join(db.RadiusAccounting, e => e.Id, a => a.RadAcctId, (e, a) => new
            {
                e.Id, e.In, e.Out, StoredIn = a.AcctInputOctets ?? 0, StoredOut = a.AcctOutputOctets ?? 0, a.UserName
            })
            .Where(x => x.In > x.StoredIn || x.Out > x.StoredOut)
            .ToListAsync(ct);
        foreach (var mismatch in raw)
        {
            findings.Add(new Finding(FindingSeverity.Critical, "Raw packets vs sessions", mismatch.UserName ?? "",
                "A received accounting packet reported more bytes than the stored session total.", mismatch.Id));
        }

        var coa = await db.EnforcementActions.AsNoTracking()
            .Where(a => a.CreatedAt > now.AddDays(-1) && a.Method == "coa")
            .GroupBy(a => a.Succeeded).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var coaTotal = coa.Sum(c => c.Count);
        var coaOk = coa.Where(c => c.Key).Sum(c => c.Count);

        return new AccountingHealth(now, lastPacketByRouter.Values.Where(v => v != null).DefaultIfEmpty().Max(), eventsLastHour,
            open.Count, open.Count(o => now - (o.AcctUpdateTime ?? o.AcctStartTime ?? now) > LiveWindow),
            coaTotal == 0 ? null : coaOk * 100.0 / coaTotal, coaTotal, routerHealth,
            findings.OrderByDescending(f => f.Severity).ToList());
    }

    public async Task<SessionEvidence?> EvidenceAsync(long radAcctId)
    {
        if (!currentUser.Can(Permissions.ViewRouters))
        {
            throw new UnauthorizedAccessException("Missing permission to view accounting evidence.");
        }

        await using var db = kiboDb.ForCurrentUser();
        var tenantId = db.TenantScope;
        var session = await db.RadiusAccounting.AsNoTracking().FirstOrDefaultAsync(a => a.RadAcctId == radAcctId && a.TenantId == tenantId);
        if (session == null)
        {
            return null;
        }

        var events = await db.RadiusAccountingEvents.AsNoTracking().Where(e => e.RadAcctId == radAcctId)
            .OrderBy(e => e.Id).Take(500).ToListAsync();
        return new SessionEvidence(session, events);
    }
}
