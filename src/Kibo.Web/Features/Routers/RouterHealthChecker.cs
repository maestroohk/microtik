using Kibo.Web.Data;
using Kibo.Web.Data.Entities;
using Kibo.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Kibo.Web.Features.Routers;

public record RouterCheckResult(bool Online, string Message, RouterResource? Resource);

public class RouterHealthChecker(
    KiboDb kiboDb,
    RouterOsClient client,
    SecretProtector secrets,
    RouterLiveUpdates liveUpdates,
    TimeProvider clock,
    ILogger<RouterHealthChecker> logger)
{
    private static readonly TimeSpan HealthyInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(15);

    public RouterConnection ConnectionFor(Router router) => new(
        router.ManagementHost,
        router.ManagementPort,
        router.UseTls,
        router.AllowSelfSignedCertificate,
        router.ApiUsername,
        secrets.Unprotect(router.ApiPasswordProtected));

    public async Task<string> ExportAsync(Router router, CancellationToken ct) =>
        await client.ExportConfigurationAsync(ConnectionFor(router), ct);

    public async Task<RouterCheckResult> CheckAsync(Guid routerId, CancellationToken ct)
    {
        await using var db = kiboDb.ForSystem();
        var router = await db.Routers.FirstOrDefaultAsync(r => r.Id == routerId, ct);
        if (router == null)
        {
            return new RouterCheckResult(false, "Router not found.", null);
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var previousStatus = router.Status;
        var previousUptime = await db.RouterHealthSamples
            .Where(s => s.RouterId == router.Id)
            .OrderByDescending(s => s.TakenAt)
            .Select(s => (long?)s.UptimeSeconds)
            .FirstOrDefaultAsync(ct);

        RouterCheckResult result;
        try
        {
            var connection = ConnectionFor(router);
            var resource = await client.GetResourceAsync(connection, ct);
            var identity = await client.GetIdentityAsync(connection, ct);
            var hotspotServers = await client.GetHotspotServersAsync(connection, ct);
            var radiusEntries = await client.GetRadiusEntriesAsync(connection, ct);
            var active = await client.GetHotspotActiveAsync(connection, ct);

            long rx = 0, tx = 0;
            if (!string.IsNullOrEmpty(router.UplinkInterface))
            {
                try
                {
                    (rx, tx) = await client.MonitorTrafficAsync(connection, router.UplinkInterface, ct);
                }
                catch (RouterOsException ex) when (ex.Kind == RouterFailureKind.ApiError)
                {
                    logger.LogInformation("Traffic monitor unavailable on {Router}: {Message}", router.Name, ex.Message);
                }
            }

            var capabilitiesChanged = router.RouterOsVersion != resource.Version || router.BoardName != resource.BoardName;
            router.Identity = identity;
            router.RouterOsVersion = resource.Version;
            router.BoardName = resource.BoardName;
            router.Architecture = resource.Architecture;
            router.SerialNumber ??= await client.GetSerialNumberAsync(connection, ct);
            router.SupportsRestApi = true;
            router.HotspotPresent = hotspotServers.Any(h => !h.Disabled);
            router.RadiusConfigured = radiusEntries.Any(r => !r.Disabled && r.Service.Contains("hotspot"));
            router.Status = RouterStatus.Online;
            router.LastSeenAt = now;
            router.LastError = null;
            router.ConsecutiveFailures = 0;

            db.RouterHealthSamples.Add(new RouterHealthSample
            {
                TenantId = router.TenantId,
                RouterId = router.Id,
                TakenAt = now,
                CpuLoadPercent = resource.CpuLoadPercent,
                FreeMemoryBytes = resource.FreeMemoryBytes,
                TotalMemoryBytes = resource.TotalMemoryBytes,
                UptimeSeconds = resource.UptimeSeconds,
                ActiveHotspotUsers = active.Count,
                RxBitsPerSecond = rx,
                TxBitsPerSecond = tx,
                IsSynthetic = router.ApiKind == RouterApiKind.Simulator
            });

            if (previousStatus != RouterStatus.Online)
            {
                AddEvent(db, router, RouterEventKind.Connected, $"Connected to {identity} running RouterOS {resource.Version}.", now);
            }

            if (previousUptime != null && resource.UptimeSeconds < previousUptime)
            {
                AddEvent(db, router, RouterEventKind.Rebooted,
                    $"Uptime dropped from {previousUptime}s to {resource.UptimeSeconds}s, so the router restarted.", now);
            }

            if (capabilitiesChanged)
            {
                AddEvent(db, router, RouterEventKind.CapabilitiesDetected,
                    $"{resource.BoardName} ({resource.Architecture}), RouterOS {resource.Version}, " +
                    $"hotspot {(router.HotspotPresent == true ? "present" : "not configured")}, " +
                    $"RADIUS {(router.RadiusConfigured == true ? "configured" : "not configured")}.", now);
            }

            result = new RouterCheckResult(true, $"Online. RouterOS {resource.Version}, CPU {resource.CpuLoadPercent}%.", resource);
        }
        catch (RouterOsException ex)
        {
            router.ConsecutiveFailures++;
            router.LastError = ex.Message;
            router.Status = ex.Kind == RouterFailureKind.AuthenticationFailed
                ? RouterStatus.AuthenticationFailed
                : RouterStatus.Offline;

            if (previousStatus != router.Status)
            {
                var kind = router.Status == RouterStatus.AuthenticationFailed
                    ? RouterEventKind.AuthenticationFailed
                    : RouterEventKind.Disconnected;
                AddEvent(db, router, kind, ex.Message, now);
            }

            logger.LogInformation("Router {RouterId} check failed ({Kind}): {Message}", router.Id, ex.Kind, ex.Message);
            result = new RouterCheckResult(false, ex.Message, null);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            router.Status = RouterStatus.AuthenticationFailed;
            router.LastError = "Stored API credentials cannot be decrypted. Re-enter the API password.";
            result = new RouterCheckResult(false, router.LastError, null);
        }

        router.LastCheckedAt = now;
        router.NextCheckAt = now + NextInterval(router.ConsecutiveFailures);
        await db.SaveChangesAsync(ct);
        await liveUpdates.PublishAsync(router.TenantId, router.Id);
        return result;
    }

    public static TimeSpan NextInterval(int consecutiveFailures)
    {
        if (consecutiveFailures == 0)
        {
            return HealthyInterval;
        }

        var seconds = Math.Min(MaxBackoff.TotalSeconds, 30 * Math.Pow(2, Math.Min(consecutiveFailures - 1, 10)));
        return TimeSpan.FromSeconds(seconds);
    }

    private static void AddEvent(AppDbContext db, Router router, RouterEventKind kind, string message, DateTime now)
    {
        db.RouterEvents.Add(new RouterEvent
        {
            TenantId = router.TenantId,
            RouterId = router.Id,
            Kind = kind,
            Message = message,
            OccurredAt = now
        });
    }
}
