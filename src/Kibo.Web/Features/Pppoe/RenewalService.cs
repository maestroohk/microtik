using System.Security.Cryptography;
using Kibo.Web.Data;
using Kibo.Web.Data.Entities;
using Kibo.Web.Features.Jobs;
using Kibo.Web.Features.Payments;
using Microsoft.EntityFrameworkCore;

namespace Kibo.Web.Features.Pppoe;

public class RenewalService(KiboDb kiboDb, IEnumerable<IPaymentProvider> providers, TimeProvider clock, ILogger<RenewalService> logger)
{
    public static readonly TimeSpan RenewalLeadTime = TimeSpan.FromDays(2);
    public static readonly TimeSpan RetryEvery = TimeSpan.FromHours(12);

    public async Task<int> RequestDueRenewalsAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        await using var db = kiboDb.ForSystem();
        var due = await db.Subscriptions.AsNoTracking()
            .Where(s => s.IsRecurring && s.Status == SubscriptionStatus.Active && s.ExpiresAt != null &&
                        s.ExpiresAt <= now + RenewalLeadTime)
            .Where(s => !db.Subscriptions.Any(next => next.AccessAccountId == s.AccessAccountId &&
                                                      next.Status == SubscriptionStatus.Scheduled))
            .Select(s => new { s.Id, s.TenantId, s.AccessAccountId, s.PackageId, s.ExpiresAt })
            .Take(100)
            .ToListAsync(ct);

        var requested = 0;
        foreach (var subscription in due)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var locked = await db.Database
                .SqlQuery<Guid>($"SELECT id AS \"Value\" FROM subscriptions WHERE id = {subscription.Id} FOR UPDATE SKIP LOCKED")
                .ToListAsync(ct);
            if (locked.Count == 0)
            {
                continue;
            }

            var recent = await db.Payments.AnyAsync(p => p.AccessAccountId == subscription.AccessAccountId &&
                                                         p.PackageId == subscription.PackageId &&
                                                         p.CreatedAt > now - RetryEvery, ct);
            if (recent)
            {
                continue;
            }

            var account = await db.AccessAccounts.Include(a => a.Customer).FirstAsync(a => a.Id == subscription.AccessAccountId, ct);
            var package = await db.Packages.FirstOrDefaultAsync(p => p.Id == subscription.PackageId && p.IsActive, ct);
            var tenant = await db.Tenants.FirstAsync(t => t.Id == subscription.TenantId, ct);
            if (account.Customer == null || package == null || tenant.Status != TenantStatus.Active ||
                account.Customer.Status != CustomerStatus.Active)
            {
                continue;
            }

            var provider = providers.First();
            var payment = new Payment
            {
                TenantId = subscription.TenantId,
                SiteId = account.Customer.HomeSiteId,
                PackageId = package.Id,
                CustomerId = account.Customer.Id,
                AccessAccountId = account.Id,
                Reference = $"RN{now:yyMMdd}{Convert.ToHexString(RandomNumberGenerator.GetBytes(4))}",
                Method = PaymentMethod.MobileMoney,
                Provider = provider.Name,
                PhoneNumber = account.Customer.PhoneNumber,
                Amount = package.Price,
                Currency = package.Currency,
                Status = PaymentStatus.Created,
                PortalToken = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24)),
                CreatedAt = now,
                ExpiresAt = now.AddMinutes(30)
            };
            db.Payments.Add(payment);
            await db.SaveChangesAsync(ct);

            var initiation = await provider.InitiateAsync(payment.Reference, payment.PhoneNumber, payment.Amount, payment.Currency,
                $"{tenant.Name}: renew {package.Name}", ct);
            if (initiation.Accepted)
            {
                payment.Status = PaymentStatus.Pending;
                payment.ProviderReference = initiation.ProviderReference;
                JobQueue.Enqueue(db, JobKinds.VerifyPayment, new VerifyPaymentPayload(payment.Id), now, payment.TenantId,
                    delay: TimeSpan.FromMinutes(1), maxAttempts: 3);
                requested++;
            }
            else
            {
                payment.Status = PaymentStatus.Failed;
                payment.FailureReason = initiation.Error;
                payment.CompletedAt = now;
                logger.LogWarning("Renewal request for {Account} failed: {Error}", account.Username, initiation.Error);
            }

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }

        return requested;
    }
}

public class RenewalWorker(IServiceScopeFactory scopeFactory, ILogger<RenewalWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        do
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var requested = await scope.ServiceProvider.GetRequiredService<RenewalService>().RequestDueRenewalsAsync(stoppingToken);
                if (requested > 0)
                {
                    logger.LogInformation("Sent {Count} renewal payment requests", requested);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Renewal cycle failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
