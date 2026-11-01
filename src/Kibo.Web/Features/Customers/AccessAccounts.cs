using Kibo.Web.Data;
using Kibo.Web.Data.Entities;
using Kibo.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Kibo.Web.Features.Customers;

public static class AccessAccounts
{
    public const string AccessCodeAlphabet = "23456789";

    public static async Task<Customer> FindOrCreateCustomerAsync(AppDbContext db, Guid tenantId, string phone,
        Guid? siteId, DateTime now, CancellationToken ct)
    {
        var customer = await db.Customers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.PhoneNumber == phone, ct);
        if (customer != null)
        {
            return customer;
        }

        customer = new Customer
        {
            TenantId = tenantId,
            PhoneNumber = phone,
            FullName = PhoneNumbers.Display(phone),
            HomeSiteId = siteId,
            CreatedAt = now
        };
        db.Customers.Add(customer);
        return customer;
    }

    public static async Task<AccessAccount> FindOrCreateHotspotAccountAsync(AppDbContext db, Customer customer,
        DateTime now, CancellationToken ct)
    {
        var account = await db.AccessAccounts.IgnoreQueryFilters()
            .FirstOrDefaultAsync(a => a.TenantId == customer.TenantId && a.CustomerId == customer.Id &&
                                      a.Kind == AccessAccountKind.Hotspot, ct);
        if (account != null)
        {
            return account;
        }

        account = new AccessAccount
        {
            TenantId = customer.TenantId,
            CustomerId = customer.Id,
            Kind = AccessAccountKind.Hotspot,
            Username = customer.PhoneNumber,
            Password = SecretProtector.NewCode(6, AccessCodeAlphabet),
            CreatedAt = now
        };
        db.AccessAccounts.Add(account);
        return account;
    }

    public static async Task<bool> HasCurrentEntitlementAsync(AppDbContext db, Guid accessAccountId, CancellationToken ct) =>
        await db.Subscriptions.IgnoreQueryFilters().AnyAsync(s => s.AccessAccountId == accessAccountId &&
            (s.Status == SubscriptionStatus.Active || s.Status == SubscriptionStatus.Unused), ct);
}
