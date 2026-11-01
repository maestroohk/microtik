using Kibo.Web.Data;
using Kibo.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Kibo.Web.Features.Routers;

public class RouterMonitorWorker(IServiceScopeFactory scopeFactory, TimeProvider clock, ILogger<RouterMonitorWorker> logger)
    : BackgroundService
{
    private const int MaxParallelChecks = 8;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        do
        {
            try
            {
                await CheckDueRoutersAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Router monitor cycle failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task CheckDueRoutersAsync(CancellationToken ct)
    {
        List<Guid> due;
        using (var scope = scopeFactory.CreateScope())
        {
            var kiboDb = scope.ServiceProvider.GetRequiredService<KiboDb>();
            await using var db = kiboDb.ForSystem();
            var now = clock.GetUtcNow().UtcDateTime;
            due = await db.Routers
                .Where(r => r.NextCheckAt == null || r.NextCheckAt <= now)
                .Where(r => db.Tenants.Any(t => t.Id == r.TenantId && t.Status == TenantStatus.Active))
                .OrderBy(r => r.NextCheckAt)
                .Select(r => r.Id)
                .Take(200)
                .ToListAsync(ct);
        }

        await Parallel.ForEachAsync(due, new ParallelOptions { MaxDegreeOfParallelism = MaxParallelChecks, CancellationToken = ct },
            async (routerId, token) =>
            {
                using var scope = scopeFactory.CreateScope();
                var checker = scope.ServiceProvider.GetRequiredService<RouterHealthChecker>();
                await checker.CheckAsync(routerId, token);
            });
    }
}
