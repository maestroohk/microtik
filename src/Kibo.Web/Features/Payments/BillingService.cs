using System.Security.Cryptography;
using Kibo.Web.Data;
using Kibo.Web.Data.Entities;
using Kibo.Web.Features.Customers;
using Kibo.Web.Features.Jobs;
using Kibo.Web.Features.Platform;
using Kibo.Web.Features.Subscriptions;
using Kibo.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Kibo.Web.Features.Payments;

public record PaymentRow(Payment Payment, string PackageName, string? SiteName);

public record PaymentTotals(decimal Succeeded, int SucceededCount, int Pending, int Failed, decimal Refunded);

public class BillingService(KiboDb kiboDb, CurrentUser currentUser, AuditLogger audit, TimeProvider clock)
{
    public async Task<(List<PaymentRow> Rows, PaymentTotals Totals)> ListPaymentsAsync(PaymentStatus? status, string? search,
        DateTime fromUtc, DateTime toUtc)
    {
        Demand(Permissions.ViewFinance);
        await using var db = kiboDb.ForCurrentUser();
        var query = db.Payments.AsNoTracking().Where(p => p.CreatedAt >= fromUtc && p.CreatedAt < toUtc);
        var totalsSource = await query.Select(p => new { p.Status, p.Amount }).ToListAsync();

        if (status != null)
        {
            query = query.Where(p => p.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            var phone = PhoneNumbers.NormalizeUganda(term) ?? term;
            query = query.Where(p => p.Reference.Contains(term) || p.PhoneNumber.Contains(phone) ||
                                     (p.ProviderReference != null && p.ProviderReference.Contains(term)));
        }

        var payments = await query.OrderByDescending(p => p.CreatedAt).Take(300).ToListAsync();
        var packages = await db.Packages.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name);
        var sites = await db.Sites.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Name);
        var rows = payments.Select(p => new PaymentRow(p, packages.GetValueOrDefault(p.PackageId, "—"),
            p.SiteId == null ? null : sites.GetValueOrDefault(p.SiteId.Value))).ToList();

        var totals = new PaymentTotals(
            totalsSource.Where(t => t.Status == PaymentStatus.Succeeded).Sum(t => t.Amount),
            totalsSource.Count(t => t.Status == PaymentStatus.Succeeded),
            totalsSource.Count(t => t.Status is PaymentStatus.Pending or PaymentStatus.Created),
            totalsSource.Count(t => t.Status is PaymentStatus.Failed or PaymentStatus.Expired),
            totalsSource.Where(t => t.Status == PaymentStatus.Refunded).Sum(t => t.Amount));
        return (rows, totals);
    }

    public async Task<OperationResult<Payment>> RefundAsync(Guid paymentId, string reason)
    {
        Demand(Permissions.ManageFinance);
        if (string.IsNullOrWhiteSpace(reason))
        {
            return OperationResult<Payment>.Fail("Give a reason for the refund.");
        }

        var now = clock.GetUtcNow().UtcDateTime;
        await using var db = kiboDb.ForCurrentUser();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var payment = await db.Payments.FirstOrDefaultAsync(p => p.Id == paymentId);
        if (payment == null || payment.Status != PaymentStatus.Succeeded)
        {
            return OperationResult<Payment>.Fail("Only successful payments can be refunded.");
        }

        payment.Status = PaymentStatus.Refunded;
        db.LedgerEntries.Add(new LedgerEntry
        {
            TenantId = payment.TenantId,
            Kind = LedgerEntryKind.Refund,
            Amount = payment.Amount,
            Currency = payment.Currency,
            PaymentId = payment.Id,
            SiteId = payment.SiteId,
            SubscriptionId = payment.SubscriptionId,
            Description = $"Refund of {payment.Reference}: {reason.Trim()}",
            CreatedBy = currentUser.Email,
            CreatedAt = now
        });

        if (payment.SubscriptionId != null)
        {
            var subscription = await db.Subscriptions.Include(s => s.AccessAccount)
                .FirstOrDefaultAsync(s => s.Id == payment.SubscriptionId);
            if (subscription != null && subscription.Status is SubscriptionStatus.Active or SubscriptionStatus.Unused or SubscriptionStatus.Scheduled)
            {
                var wasCurrent = Entitlements.IsCurrent(subscription.Status);
                subscription.Status = SubscriptionStatus.Cancelled;
                subscription.EndedAt = now;
                subscription.EndReason = "Payment refunded";
                if (wasCurrent)
                {
                    await SubscriptionLifecycle.PromoteNextAsync(db, subscription.AccessAccountId, now, CancellationToken.None);
                    JobQueue.Enqueue(db, JobKinds.DisconnectUser,
                        new DisconnectUserPayload(subscription.TenantId, subscription.AccessAccount!.Username, "Payment refunded"),
                        now, subscription.TenantId);
                }

                await db.SaveChangesAsync();
                await RadiusProvisioner.SyncAccountAsync(db, subscription.AccessAccountId, now, CancellationToken.None);
            }
        }

        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        await audit.WriteAsync("payment.refunded", "Payment", payment.Id.ToString(), $"{payment.Reference}: {reason}");
        return OperationResult<Payment>.Ok(payment);
    }

    public async Task<OperationResult<Subscription>> SellForCashAsync(Guid customerId, Guid packageId, string? note)
    {
        Demand(Permissions.ManageCustomers);
        var now = clock.GetUtcNow().UtcDateTime;
        await using var db = kiboDb.ForCurrentUser();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == customerId);
        var package = await db.Packages.FirstOrDefaultAsync(p => p.Id == packageId && p.IsActive);
        if (customer == null || package == null)
        {
            return OperationResult<Subscription>.Fail("Customer or package not found.");
        }

        if (customer.Status != CustomerStatus.Active)
        {
            return OperationResult<Subscription>.Fail("Reactivate the customer before selling a package.");
        }

        AccessAccount? account;
        if (package.ServiceType == ServiceType.Pppoe)
        {
            account = await db.AccessAccounts.FirstOrDefaultAsync(a => a.CustomerId == customer.Id && a.Kind == AccessAccountKind.Pppoe);
            if (account == null)
            {
                return OperationResult<Subscription>.Fail("Create a PPPoE login for this customer before selling a PPPoE package.");
            }
        }
        else
        {
            account = await AccessAccounts.FindOrCreateHotspotAccountAsync(db, customer, now, CancellationToken.None);
        }

        var hasCurrent = await AccessAccounts.HasCurrentEntitlementAsync(db, account.Id, CancellationToken.None);
        var reference = $"CASH{now:yyMMdd}{Convert.ToHexString(RandomNumberGenerator.GetBytes(4))}";
        var subscription = Entitlements.CreateFromPackage(package, account, now, SubscriptionSource.Manual, reference, hasCurrent,
            package.Price);
        db.Subscriptions.Add(subscription);
        db.LedgerEntries.Add(new LedgerEntry
        {
            TenantId = customer.TenantId,
            Kind = LedgerEntryKind.Sale,
            Amount = package.Price,
            Currency = package.Currency,
            SiteId = customer.HomeSiteId,
            SubscriptionId = subscription.Id,
            Description = $"Cash sale {reference}: {package.Name}{(string.IsNullOrWhiteSpace(note) ? "" : $" ({note.Trim()})")}",
            CreatedBy = currentUser.Email,
            CreatedAt = now
        });
        await db.SaveChangesAsync();
        await RadiusProvisioner.SyncAccountAsync(db, account.Id, now, CancellationToken.None);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        await audit.WriteAsync("subscription.cash_sale", "Customer", customer.Id.ToString(), $"{package.Name} {reference}");
        return OperationResult<Subscription>.Ok(subscription);
    }

    private void Demand(string permission)
    {
        if (!currentUser.Can(permission))
        {
            throw new UnauthorizedAccessException($"Missing permission {permission}.");
        }
    }
}
