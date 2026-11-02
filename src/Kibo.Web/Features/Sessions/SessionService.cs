using Kibo.Web.Data;
using Kibo.Web.Data.Entities;
using Kibo.Web.Features.Platform;
using Kibo.Web.Features.Routers;
using Kibo.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Kibo.Web.Features.Sessions;

public record ActiveSessionView(long Id, string Username, string? CustomerName, string RouterName, string? MacAddress,
    string? IpAddress, DateTime StartedAt, DateTime? LastUpdate, long Bytes, long Seconds);

public class SessionService(
    KiboDb kiboDb,
    CurrentUser currentUser,
    RouterOsClient routerClient,
    RouterHealthChecker healthChecker,
    DynamicAuthorizationClient dynamicAuthorization,
    AuditLogger audit,
    ILogger<SessionService> logger)
{
    public async Task<List<ActiveSessionView>> ListActiveAsync(string? search = null, int take = 200)
    {
        await using var db = kiboDb.ForCurrentUser();
        var tenantId = db.TenantScope ?? throw new UnauthorizedAccessException("Select a tenant first.");

        var query =
            from session in db.RadiusAccounting
            join nas in db.RadiusNas on session.NasIpAddress equals nas.NasName
            join router in db.Routers on nas.RouterId equals router.Id
            where session.AcctStopTime == null && nas.TenantId == tenantId
            select new { session, router.Name };

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(x => x.session.UserName!.Contains(term) || x.session.CallingStationId!.Contains(term));
        }

        var rows = await query.OrderByDescending(x => x.session.AcctStartTime).Take(take).ToListAsync();
        var usernames = rows.Select(r => r.session.UserName).Distinct().ToList();
        var names = await db.AccessAccounts.Where(a => usernames.Contains(a.Username) && a.Customer != null)
            .Select(a => new { a.Username, a.Customer!.FullName })
            .ToDictionaryAsync(a => a.Username, a => a.FullName);

        return rows.Select(r => new ActiveSessionView(
            r.session.RadAcctId,
            r.session.UserName ?? "",
            names.GetValueOrDefault(r.session.UserName ?? ""),
            r.Name,
            r.session.CallingStationId,
            r.session.FramedIpAddress,
            r.session.AcctStartTime ?? DateTime.MinValue,
            r.session.AcctUpdateTime,
            (r.session.AcctInputOctets ?? 0) + (r.session.AcctOutputOctets ?? 0),
            r.session.AcctSessionTime ?? 0)).ToList();
    }

    public async Task<OperationResult<int>> DisconnectFromDashboardAsync(string username)
    {
        if (!currentUser.Can(Permissions.DisconnectSessions))
        {
            throw new UnauthorizedAccessException("Missing permission to disconnect sessions.");
        }

        var tenantId = currentUser.TenantId ?? throw new UnauthorizedAccessException("Select a tenant first.");
        var count = await DisconnectUserAsync(tenantId, username, "Disconnected by operator", CancellationToken.None);
        await audit.WriteAsync("session.disconnected", "AccessAccount", username, $"{count} session(s)");
        return OperationResult<int>.Ok(count);
    }

    public async Task<int> ChangeRateAsync(Guid tenantId, string username, string rateLimit, CancellationToken ct)
    {
        var routers = await RoutersWithOpenSessionsAsync(tenantId, username, ct);
        var changed = 0;
        foreach (var router in routers)
        {
            var result = await dynamicAuthorization.ChangeRateAsync(router, username, rateLimit, ct);
            await RecordAsync(tenantId, router.Id, username, "throttle", "coa", result == DynamicAuthorizationResult.Acknowledged,
                $"{rateLimit}: {result}");
            if (result == DynamicAuthorizationResult.Acknowledged)
            {
                changed++;
                continue;
            }

            logger.LogInformation("CoA to {Router} for {Username} was {Result}; disconnecting instead", router.Name, username, result);
            await DisconnectUserAsync(tenantId, username, "Fair-usage threshold reached", ct);
            return changed;
        }

        return changed;
    }

    private async Task<List<Router>> RoutersWithOpenSessionsAsync(Guid tenantId, string username, CancellationToken ct)
    {
        await using var db = kiboDb.ForSystem();
        return await (
                from session in db.RadiusAccounting
                join router in db.Routers on session.RouterId equals router.Id
                where session.AcctStopTime == null && session.UserName == username && session.TenantId == tenantId
                select router)
            .Distinct()
            .ToListAsync(ct);
    }

    public async Task<int> DisconnectUserAsync(Guid tenantId, string username, string reason, CancellationToken ct)
    {
        var routers = await RoutersWithOpenSessionsAsync(tenantId, username, ct);
        var removed = 0;
        var failures = new List<string>();
        foreach (var router in routers)
        {
            try
            {
                var connection = healthChecker.ConnectionFor(router);
                var count = 0;
                var active = await routerClient.GetHotspotActiveAsync(connection, ct);
                foreach (var session in active.Where(a => a.User == username))
                {
                    await routerClient.RemoveHotspotActiveAsync(connection, session.Id, ct);
                    count++;
                }

                var ppp = await routerClient.GetPppActiveAsync(connection, ct);
                foreach (var session in ppp.Where(a => a.Name == username))
                {
                    await routerClient.RemovePppActiveAsync(connection, session.Id, ct);
                    count++;
                }

                removed += count;
                await RecordAsync(tenantId, router.Id, username, "disconnect", "routeros-api", true, $"{count} session(s): {reason}");
            }
            catch (RouterOsException ex)
            {
                var fallback = await dynamicAuthorization.DisconnectAsync(router, username, ct);
                var ok = fallback == DynamicAuthorizationResult.Acknowledged;
                await RecordAsync(tenantId, router.Id, username, "disconnect", "radius-disconnect", ok,
                    $"API failed ({ex.Kind}); RADIUS {fallback}: {reason}");
                if (ok)
                {
                    removed++;
                }
                else
                {
                    failures.Add($"{router.Name}: {ex.Message}; RADIUS disconnect {fallback.ToString().ToLowerInvariant()}");
                }
            }
        }

        logger.LogInformation("Disconnected {Count} session(s) for {Username}: {Reason}", removed, username, reason);
        if (failures.Count > 0)
        {
            throw new InvalidOperationException($"Could not disconnect on all routers. {string.Join("; ", failures)}");
        }

        return removed;
    }

    private async Task RecordAsync(Guid tenantId, Guid routerId, string username, string kind, string method, bool succeeded,
        string detail)
    {
        await using var db = kiboDb.ForSystem();
        db.EnforcementActions.Add(new EnforcementAction
        {
            TenantId = tenantId,
            RouterId = routerId,
            Username = username,
            Kind = kind,
            Method = method,
            Succeeded = succeeded,
            Detail = detail.Length > 500 ? detail[..500] : detail,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync(CancellationToken.None);
    }
}
