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

    public async Task<int> DisconnectUserAsync(Guid tenantId, string username, string reason, CancellationToken ct)
    {
        await using var db = kiboDb.ForSystem();
        var routers = await (
                from session in db.RadiusAccounting
                join nas in db.RadiusNas on session.NasIpAddress equals nas.NasName
                join router in db.Routers on nas.RouterId equals router.Id
                where session.AcctStopTime == null && session.UserName == username && nas.TenantId == tenantId
                select router)
            .Distinct()
            .ToListAsync(ct);

        var removed = 0;
        var failures = new List<string>();
        foreach (var router in routers)
        {
            try
            {
                var connection = healthChecker.ConnectionFor(router);
                var active = await routerClient.GetHotspotActiveAsync(connection, ct);
                foreach (var session in active.Where(a => a.User == username))
                {
                    await routerClient.RemoveHotspotActiveAsync(connection, session.Id, ct);
                    removed++;
                }

                var ppp = await routerClient.GetPppActiveAsync(connection, ct);
                foreach (var session in ppp.Where(a => a.Name == username))
                {
                    await routerClient.RemovePppActiveAsync(connection, session.Id, ct);
                    removed++;
                }
            }
            catch (RouterOsException ex)
            {
                failures.Add($"{router.Name}: {ex.Message}");
            }
        }

        logger.LogInformation("Disconnected {Count} session(s) for {Username}: {Reason}", removed, username, reason);
        if (failures.Count > 0)
        {
            throw new InvalidOperationException($"Could not disconnect on all routers. {string.Join("; ", failures)}");
        }

        return removed;
    }
}
