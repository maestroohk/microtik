using System.Text.Json;
using Kibo.Web.Data;
using Kibo.Web.Data.Entities;
using Kibo.Web.Features.Accounting;
using Kibo.Web.Features.Routers;
using Kibo.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Kibo.Web.Features.Reports;

public enum SignalLevel
{
    Good,
    Attention,
    Problem,
    Unknown
}

public record OperationsSignal(string Label, string Value, string Detail, SignalLevel Level, string? Link = null);

public class OperationsService(KiboDb kiboDb, CurrentUser currentUser, TimeProvider clock)
{
    public async Task<List<OperationsSignal>> LoadAsync()
    {
        var now = clock.GetUtcNow().UtcDateTime;
        await using var db = kiboDb.ForCurrentUser();
        var tenantId = db.TenantScope ?? throw new UnauthorizedAccessException("Select an operator first.");
        var signals = new List<OperationsSignal>();

        var routers = await db.Routers.AsNoTracking().ToListAsync();
        var ready = routers.Count(r => r.Status == RouterStatus.Online && r.OwnershipVerifiedAt != null && r.HotspotPresent == true &&
                                       r.RadiusConfigured == true && !HasCritical(r));
        var unverified = routers.Count(r => r.OwnershipVerifiedAt == null);
        signals.Add(new("Router readiness", $"{ready}/{routers.Count}",
            unverified > 0 ? $"{unverified} awaiting ownership proof" : $"{routers.Count(r => r.Status != RouterStatus.Online)} offline",
            routers.Count == 0 ? SignalLevel.Unknown : ready == routers.Count ? SignalLevel.Good : SignalLevel.Attention, "/routers"));

        var lastPacket = await db.RadiusAccountingEvents.Where(e => e.TenantId == tenantId).MaxAsync(e => (DateTime?)e.ReceivedAt);
        var openSessions = await db.RadiusAccounting.CountAsync(a => a.TenantId == tenantId && a.AcctStopTime == null);
        var staleCutoff = now - ReconciliationService.LiveWindow;
        var stale = await db.RadiusAccounting.CountAsync(a => a.TenantId == tenantId && a.AcctStopTime == null &&
                                                               (a.AcctUpdateTime ?? a.AcctStartTime) < staleCutoff);
        signals.Add(new("Accounting freshness",
            lastPacket == null ? "No data" : $"{(int)(now - lastPacket.Value).TotalSeconds}s ago",
            stale > 0 ? $"{stale} of {openSessions} open sessions not reporting" : $"{openSessions} open sessions reporting",
            lastPacket == null ? SignalLevel.Unknown : stale > 0 ? SignalLevel.Problem : SignalLevel.Good, "/accounting"));

        var nasNames = db.RadiusNas.Select(n => n.NasName);
        var lastAuth = await db.RadiusPostAuth.Where(p => nasNames.Contains(p.NasIpAddress!)).MaxAsync(p => (DateTime?)p.AuthDate);
        var rejectsHour = await db.RadiusPostAuth.CountAsync(p => nasNames.Contains(p.NasIpAddress!) && p.Reply == "Access-Reject" &&
                                                                  p.AuthDate > now.AddHours(-1));
        var acceptsHour = await db.RadiusPostAuth.CountAsync(p => nasNames.Contains(p.NasIpAddress!) && p.Reply == "Access-Accept" &&
                                                                  p.AuthDate > now.AddHours(-1));
        signals.Add(new("RADIUS logins (1h)", $"{acceptsHour} ok · {rejectsHour} rejected",
            lastAuth == null ? "No logins recorded yet" : $"Last login {Kibo.Web.Components.Shared.Format.Ago(lastAuth, now)}",
            rejectsHour > Math.Max(20, acceptsHour * 2) ? SignalLevel.Attention : lastAuth == null ? SignalLevel.Unknown : SignalLevel.Good));

        var coa = await db.EnforcementActions.Where(a => a.CreatedAt > now.AddDays(-1))
            .GroupBy(a => new { a.Kind, a.Succeeded }).Select(g => new { g.Key.Kind, g.Key.Succeeded, Count = g.Count() }).ToListAsync();
        var coaFailed = coa.Where(c => !c.Succeeded).Sum(c => c.Count);
        signals.Add(new("Enforcement (24h)", $"{coa.Where(c => c.Succeeded).Sum(c => c.Count)} ok · {coaFailed} failed",
            $"{coa.Where(c => c.Kind == "throttle").Sum(c => c.Count)} speed changes, {coa.Where(c => c.Kind == "disconnect").Sum(c => c.Count)} disconnects",
            coaFailed > 0 ? SignalLevel.Problem : SignalLevel.Good));

        var throttled = await db.Subscriptions.CountAsync(s => s.Status == SubscriptionStatus.Active && s.ThrottledAt != null);
        var overshoots = await db.Subscriptions.Where(s => s.ThrottleOvershootBytes != null && s.ThrottledAt > now.AddDays(-7))
            .Select(s => s.ThrottleOvershootBytes!.Value).ToListAsync();
        var exhausted = await db.Subscriptions.CountAsync(s => s.Status == SubscriptionStatus.Exhausted && s.EndedAt > now.AddDays(-1));
        signals.Add(new("Caps and fair use", $"{throttled} throttled · {exhausted} capped today",
            overshoots.Count == 0 ? "No fair-use transitions this week" : $"Median overshoot {Kibo.Web.Components.Shared.Format.Bytes(Median(overshoots))}, worst {Kibo.Web.Components.Shared.Format.Bytes(overshoots.Max())}",
            overshoots.Count > 0 && overshoots.Max() > 50L * 1024 * 1024 ? SignalLevel.Attention : SignalLevel.Good));

        if (currentUser.Can(Permissions.ViewFinance))
        {
            var stuck = await db.Payments.CountAsync(p => p.Status == PaymentStatus.Pending && p.CreatedAt < now.AddMinutes(-15));
            var pending = await db.Payments.CountAsync(p => p.Status == PaymentStatus.Pending);
            signals.Add(new("Payment reconciliation", $"{pending} pending", stuck > 0 ? $"{stuck} unresolved for over 15 minutes" : "None overdue",
                stuck > 0 ? SignalLevel.Problem : SignalLevel.Good, "/payments"));
        }

        var failedJobs = await db.BackgroundJobs.CountAsync(j => j.TenantId == tenantId && j.Status == JobStatus.Failed && j.CompletedAt > now.AddDays(-1));
        var failedProvisioning = await db.ProvisioningOperations.CountAsync(o => (o.Status == ProvisioningStatus.Failed || o.Status == ProvisioningStatus.RolledBack) &&
                                                                               o.CreatedAt > now.AddDays(-7));
        signals.Add(new("Background work", $"{failedJobs} failed jobs (24h)", $"{failedProvisioning} failed provisioning in 7 days",
            failedJobs + failedProvisioning > 0 ? SignalLevel.Attention : SignalLevel.Good));

        var lockedStaff = await db.Users.CountAsync(u => u.LockedUntil > now);
        var deviceLockouts = await db.RadiusPostAuth.Where(p => nasNames.Contains(p.NasIpAddress!) && p.Reply == "Access-Reject" &&
                                                                p.AuthDate > now.AddMinutes(-10))
            .GroupBy(p => p.CallingStationId).CountAsync(g => g.Count() >= 10);
        var critical = routers.Count(HasCritical);
        var drift = routers.Count(r => r.DriftDetectedAt != null);
        signals.Add(new("Security", $"{critical + lockedStaff + deviceLockouts} alerts",
            $"{critical} router(s) with critical findings, {drift} drifted, {deviceLockouts} devices locked out, {lockedStaff} staff locked",
            critical > 0 ? SignalLevel.Problem : lockedStaff + deviceLockouts + drift > 0 ? SignalLevel.Attention : SignalLevel.Good));

        var oldVersions = routers.Count(r => r.RouterOsVersion != null && Version.TryParse(r.RouterOsVersion.Split(' ')[0], out var v) && v < new Version(7, 10));
        var simulators = routers.Count(r => r.ApiKind == RouterApiKind.Simulator);
        signals.Add(new("RouterOS compatibility", oldVersions == 0 ? "No warnings" : $"{oldVersions} below 7.10",
            simulators > 0 ? $"{simulators} simulator(s): results are not RouterOS-verified" : "All routers are RouterOS",
            oldVersions > 0 ? SignalLevel.Attention : simulators > 0 ? SignalLevel.Attention : SignalLevel.Good));

        return signals;
    }

    private static bool HasCritical(Router router) =>
        router.PostureJson != null &&
        (JsonSerializer.Deserialize<List<PostureFinding>>(router.PostureJson) ?? []).Any(f => f.Severity == "Critical");

    private static long Median(List<long> values)
    {
        var sorted = values.Order().ToList();
        return sorted[sorted.Count / 2];
    }
}
