using Kibo.Web.Data;
using Kibo.Web.Data.Entities;
using Kibo.Web.Features.Jobs;
using Kibo.Web.Features.Vouchers;
using Microsoft.EntityFrameworkCore;

namespace Kibo.Web.Features.Subscriptions;

public class SubscriptionLifecycle(KiboDb kiboDb, TimeProvider clock, ILogger<SubscriptionLifecycle> logger)
{
    public static readonly TimeSpan StaleSessionAfter = TimeSpan.FromMinutes(5);

    public async Task<int> CloseStaleSessionsAsync(CancellationToken ct)
    {
        var cutoff = clock.GetUtcNow().UtcDateTime - StaleSessionAfter;
        await using var db = kiboDb.ForSystem();
        return await db.RadiusAccounting
            .Where(a => a.AcctStopTime == null && (a.AcctUpdateTime ?? a.AcctStartTime) < cutoff)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.AcctStopTime, a => a.AcctUpdateTime ?? a.AcctStartTime)
                .SetProperty(a => a.AcctTerminateCause, "Stale-Session"), ct);
    }

    public async Task ProcessBatchAsync(CancellationToken ct)
    {
        await using var db = kiboDb.ForSystem();
        var ids = await db.Subscriptions
            .Where(s => s.Status == SubscriptionStatus.Active || s.Status == SubscriptionStatus.Unused)
            .OrderBy(s => s.UsageUpdatedAt)
            .Select(s => s.Id)
            .Take(500)
            .ToListAsync(ct);

        foreach (var id in ids)
        {
            try
            {
                await ProcessAsync(id, ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                logger.LogInformation("Subscription {SubscriptionId} changed concurrently, retrying next cycle", id);
            }
        }
    }

    public async Task ProcessAsync(Guid subscriptionId, CancellationToken ct)
    {
        await using var db = kiboDb.ForSystem();
        var subscription = await db.Subscriptions.Include(s => s.AccessAccount)
            .FirstOrDefaultAsync(s => s.Id == subscriptionId, ct);
        if (subscription?.AccessAccount == null || !Entitlements.IsCurrent(subscription.Status))
        {
            return;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var account = subscription.AccessAccount;
        var usage = await RadiusProvisioner.ReadUsageAsync(db, subscription.TenantId, account.Username,
            subscription.CountingSince ?? subscription.PurchasedAt, ct);

        var previousStatus = subscription.Status;
        subscription.UsedBytes = usage.Bytes;
        subscription.UsedSeconds = usage.Seconds;
        subscription.UsageUpdatedAt = now;

        var decision = Entitlements.Evaluate(subscription, usage, now);
        var radiusChanged = false;

        if (previousStatus == SubscriptionStatus.Unused && decision.Status == SubscriptionStatus.Active)
        {
            Entitlements.Activate(subscription, usage.FirstSessionStart!.Value);
            radiusChanged = true;
            decision = Entitlements.Evaluate(subscription, usage, now);
        }

        if (decision.Status is SubscriptionStatus.Expired or SubscriptionStatus.Exhausted)
        {
            subscription.Status = decision.Status;
            subscription.EndedAt = now;
            subscription.EndReason = decision.EndReason;
            await PromoteNextAsync(db, subscription.AccessAccountId, now, ct);
            JobQueue.Enqueue(db, JobKinds.DisconnectUser,
                new DisconnectUserPayload(subscription.TenantId, account.Username, decision.EndReason ?? "Entitlement ended"),
                now, subscription.TenantId);
            radiusChanged = true;
        }
        else if (subscription.ThrottledAt == null && subscription.DataQuotaBytes != null &&
                 usage.Bytes >= subscription.DataQuotaBytes && Entitlements.HasThrottleFallback(subscription))
        {
            subscription.ThrottledAt = now;
            var throttledRate = Entitlements.RateLimit(subscription.ThrottledUploadKbps ?? subscription.UploadKbps,
                subscription.ThrottledDownloadKbps ?? subscription.DownloadKbps);
            JobQueue.Enqueue(db, JobKinds.ThrottleUser,
                new ThrottleUserPayload(subscription.TenantId, account.Username, throttledRate), now, subscription.TenantId);
        }

        if (subscription.Source == SubscriptionSource.Voucher && previousStatus != subscription.Status)
        {
            await UpdateVoucherAsync(db, subscription, now, ct);
        }

        if (radiusChanged)
        {
            await db.SaveChangesAsync(ct);
            await RadiusProvisioner.SyncAccountAsync(db, subscription.AccessAccountId, now, ct);
        }

        await db.SaveChangesAsync(ct);
    }

    private static async Task UpdateVoucherAsync(AppDbContext db, Subscription subscription, DateTime now, CancellationToken ct)
    {
        var voucher = await db.Vouchers.Include(v => v.Batch).FirstOrDefaultAsync(v => v.SubscriptionId == subscription.Id, ct);
        if (voucher == null)
        {
            return;
        }

        if (subscription.Status == SubscriptionStatus.Expired && subscription.ActivatedAt == null)
        {
            voucher.Status = VoucherStatus.Expired;
            return;
        }

        if (voucher.Status is VoucherStatus.Available or VoucherStatus.Sold && subscription.ActivatedAt != null)
        {
            if (voucher.Status == VoucherStatus.Available)
            {
                VoucherService.AddSaleLedger(db, voucher.Batch!, voucher, null, now, "system: first redemption");
            }

            voucher.Status = VoucherStatus.Redeemed;
            voucher.RedeemedAt = subscription.ActivatedAt;
        }
    }

    public static async Task PromoteNextAsync(AppDbContext db, Guid accessAccountId, DateTime now, CancellationToken ct)
    {
        var next = await db.Subscriptions
            .Where(s => s.AccessAccountId == accessAccountId && s.Status == SubscriptionStatus.Scheduled)
            .OrderBy(s => s.PurchasedAt)
            .FirstOrDefaultAsync(ct);

        if (next != null)
        {
            Entitlements.MakeCurrent(next, now);
        }
    }
}

public class SubscriptionLifecycleWorker(IServiceScopeFactory scopeFactory, ILogger<SubscriptionLifecycleWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        do
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var lifecycle = scope.ServiceProvider.GetRequiredService<SubscriptionLifecycle>();
                var closed = await lifecycle.CloseStaleSessionsAsync(stoppingToken);
                if (closed > 0)
                {
                    logger.LogInformation("Closed {Count} stale accounting sessions", closed);
                }

                await lifecycle.ProcessBatchAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Subscription lifecycle cycle failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
