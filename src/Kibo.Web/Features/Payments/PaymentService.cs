using System.Security.Cryptography;
using Kibo.Web.Data;
using Kibo.Web.Data.Entities;
using Kibo.Web.Features.Customers;
using Kibo.Web.Features.Jobs;
using Kibo.Web.Features.Packages;
using Kibo.Web.Features.Subscriptions;
using Microsoft.EntityFrameworkCore;

namespace Kibo.Web.Features.Payments;

public record PortalPurchaseRequest(string SiteSlug, Guid PackageId, string PhoneNumber, string? MacAddress, string? IpAddress);

public class PaymentService(
    KiboDb kiboDb,
    IEnumerable<IPaymentProvider> providers,
    TimeProvider clock,
    ILogger<PaymentService> logger)
{
    public static readonly TimeSpan PaymentLifetime = TimeSpan.FromMinutes(10);

    public async Task<OperationResult<Payment>> StartPortalPurchaseAsync(PortalPurchaseRequest request, CancellationToken ct)
    {
        var phone = PhoneNumbers.NormalizeUganda(request.PhoneNumber);
        if (phone == null)
        {
            return OperationResult<Payment>.Fail("Enter a valid mobile money number, for example 0772 123456.");
        }

        var now = clock.GetUtcNow().UtcDateTime;
        await using var db = kiboDb.ForSystem();
        var site = await db.Sites.FirstOrDefaultAsync(s => s.Slug == request.SiteSlug, ct);
        if (site == null)
        {
            return OperationResult<Payment>.Fail("This hotspot is not recognised.");
        }

        var tenant = await db.Tenants.FirstAsync(t => t.Id == site.TenantId, ct);
        if (tenant.Status != TenantStatus.Active)
        {
            return OperationResult<Payment>.Fail("This hotspot is temporarily unavailable.");
        }

        var package = await db.Packages.FirstOrDefaultAsync(p => p.Id == request.PackageId && p.TenantId == site.TenantId &&
                                                                 p.IsActive && p.ShowOnPortal &&
                                                                 (p.SiteId == null || p.SiteId == site.Id), ct);
        if (package == null || !PackageCatalog.IsAvailable(package, now))
        {
            return OperationResult<Payment>.Fail("This package is no longer available.");
        }

        var recentPending = await db.Payments.CountAsync(p => p.TenantId == site.TenantId && p.PhoneNumber == phone &&
                                                              p.Status == PaymentStatus.Pending && p.CreatedAt > now.AddMinutes(-10), ct);
        if (recentPending >= 3)
        {
            return OperationResult<Payment>.Fail("You have several payments waiting for approval. Approve or cancel them on your phone first.");
        }

        var provider = providers.First();
        var payment = new Payment
        {
            TenantId = site.TenantId,
            SiteId = site.Id,
            PackageId = package.Id,
            Reference = NewReference(now),
            Method = PaymentMethod.MobileMoney,
            Provider = provider.Name,
            PhoneNumber = phone,
            Amount = package.Price,
            Currency = package.Currency,
            Status = PaymentStatus.Created,
            ClientMacAddress = request.MacAddress,
            ClientIpAddress = request.IpAddress,
            PortalToken = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24)),
            CreatedAt = now,
            ExpiresAt = now + PaymentLifetime
        };
        db.Payments.Add(payment);
        await db.SaveChangesAsync(ct);

        var initiation = await provider.InitiateAsync(payment.Reference, phone, payment.Amount, payment.Currency,
            $"{tenant.Name}: {package.Name}", ct);
        if (!initiation.Accepted)
        {
            payment.Status = PaymentStatus.Failed;
            payment.FailureReason = initiation.Error;
            payment.CompletedAt = clock.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(ct);
            logger.LogWarning("Payment {Reference} initiation failed: {Error}", payment.Reference, initiation.Error);
            return OperationResult<Payment>.Fail("We could not reach mobile money right now. Please try again in a minute.");
        }

        payment.Status = PaymentStatus.Pending;
        payment.ProviderReference = initiation.ProviderReference;
        JobQueue.Enqueue(db, JobKinds.VerifyPayment, new VerifyPaymentPayload(payment.Id), now, payment.TenantId,
            delay: TimeSpan.FromSeconds(20), maxAttempts: 3);
        await db.SaveChangesAsync(ct);
        return OperationResult<Payment>.Ok(payment);
    }

    public async Task<Payment?> FindByPortalTokenAsync(string token, CancellationToken ct)
    {
        await using var db = kiboDb.ForSystem();
        return await db.Payments.AsNoTracking().FirstOrDefaultAsync(p => p.PortalToken == token, ct);
    }

    public async Task<string> HandleCallbackAsync(string providerName, string body, IHeaderDictionary headers, CancellationToken ct)
    {
        var provider = providers.FirstOrDefault(p => p.Name == providerName);
        var now = clock.GetUtcNow().UtcDateTime;
        var callback = provider?.ParseAuthenticatedCallback(body, headers);

        await using var db = kiboDb.ForSystem();
        var log = new PaymentCallback
        {
            Provider = providerName,
            ProviderReference = callback?.ProviderReference,
            PaymentReference = callback?.PaymentReference,
            SignatureValid = callback != null,
            Payload = body.Length > 4000 ? body[..4000] : body,
            ReceivedAt = now
        };
        db.PaymentCallbacks.Add(log);

        if (provider == null || callback == null)
        {
            log.Outcome = "rejected: signature invalid or provider unknown";
            await db.SaveChangesAsync(ct);
            return log.Outcome;
        }

        var payment = await db.Payments.FirstOrDefaultAsync(p => p.Provider == providerName &&
                                                                 p.ProviderReference == callback.ProviderReference, ct);
        if (payment == null)
        {
            log.Outcome = "ignored: unknown transaction";
            await db.SaveChangesAsync(ct);
            return log.Outcome;
        }

        log.Outcome = "accepted: verified with provider";
        await db.SaveChangesAsync(ct);

        try
        {
            await VerifyAndSettleAsync(payment.Id, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Immediate verification of {Reference} failed; queued for retry", payment.Reference);
            JobQueue.Enqueue(db, JobKinds.VerifyPayment, new VerifyPaymentPayload(payment.Id), now, payment.TenantId,
                delay: TimeSpan.FromSeconds(15));
            await db.SaveChangesAsync(ct);
        }

        return log.Outcome;
    }

    public async Task VerifyAndSettleAsync(Guid paymentId, CancellationToken ct)
    {
        await using var db = kiboDb.ForSystem();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var payment = await db.Payments
            .FromSqlInterpolated($"SELECT *, xmin FROM payments WHERE id = {paymentId} FOR UPDATE")
            .FirstOrDefaultAsync(ct);
        if (payment == null || payment.Status != PaymentStatus.Pending || payment.ProviderReference == null)
        {
            return;
        }

        var provider = providers.First(p => p.Name == payment.Provider);
        var now = clock.GetUtcNow().UtcDateTime;
        payment.LastVerifiedAt = now;
        payment.VerificationAttempts++;

        var status = await provider.GetStatusAsync(payment.ProviderReference, ct);
        switch (status.Status)
        {
            case ProviderPaymentStatus.Succeeded when status.Amount == payment.Amount && status.Currency == payment.Currency:
                await SettleAsync(db, payment, now, ct);
                break;
            case ProviderPaymentStatus.Succeeded:
                payment.Status = PaymentStatus.Failed;
                payment.FailureReason = $"Amount mismatch: provider reported {status.Amount} {status.Currency}.";
                payment.CompletedAt = now;
                logger.LogWarning("Payment {Reference} amount mismatch", payment.Reference);
                break;
            case ProviderPaymentStatus.Failed:
            case ProviderPaymentStatus.NotFound:
                payment.Status = PaymentStatus.Failed;
                payment.FailureReason = status.Reason ?? "Declined";
                payment.CompletedAt = now;
                break;
            case ProviderPaymentStatus.Pending when now >= payment.ExpiresAt:
                payment.Status = PaymentStatus.Expired;
                payment.FailureReason = "Not approved in time";
                payment.CompletedAt = now;
                break;
            case ProviderPaymentStatus.Pending:
                JobQueue.Enqueue(db, JobKinds.VerifyPayment, new VerifyPaymentPayload(payment.Id), now, payment.TenantId,
                    delay: TimeSpan.FromSeconds(30), maxAttempts: 3);
                break;
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private static async Task SettleAsync(AppDbContext db, Payment payment, DateTime now, CancellationToken ct)
    {
        var package = await db.Packages.FirstAsync(p => p.Id == payment.PackageId, ct);
        var customer = await AccessAccounts.FindOrCreateCustomerAsync(db, payment.TenantId, payment.PhoneNumber, payment.SiteId, now, ct);
        var account = payment.AccessAccountId == null
            ? await AccessAccounts.FindOrCreateHotspotAccountAsync(db, customer, now, ct)
            : await db.AccessAccounts.FirstAsync(a => a.Id == payment.AccessAccountId, ct);
        var hasCurrent = await AccessAccounts.HasCurrentEntitlementAsync(db, account.Id, ct);

        var subscription = Entitlements.CreateFromPackage(package, account, now, SubscriptionSource.Payment,
            payment.Reference, hasCurrent, payment.Amount);
        db.Subscriptions.Add(subscription);

        payment.Status = PaymentStatus.Succeeded;
        payment.CompletedAt = now;
        payment.CustomerId = customer.Id;
        payment.AccessAccountId = account.Id;
        payment.SubscriptionId = subscription.Id;

        db.LedgerEntries.Add(new LedgerEntry
        {
            TenantId = payment.TenantId,
            Kind = LedgerEntryKind.Sale,
            Amount = payment.Amount,
            Currency = payment.Currency,
            PaymentId = payment.Id,
            SiteId = payment.SiteId,
            SubscriptionId = subscription.Id,
            Description = $"{package.Name} via {payment.Provider} ({payment.Reference})",
            CreatedBy = "system",
            CreatedAt = now
        });

        await db.SaveChangesAsync(ct);
        await RadiusProvisioner.SyncAccountAsync(db, account.Id, now, ct);
    }

    private static string NewReference(DateTime now) => $"KB{now:yyMMdd}{Convert.ToHexString(RandomNumberGenerator.GetBytes(4))}";
}
