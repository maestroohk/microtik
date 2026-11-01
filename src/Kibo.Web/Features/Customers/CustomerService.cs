using System.ComponentModel.DataAnnotations;
using Kibo.Web.Data;
using Kibo.Web.Data.Entities;
using Kibo.Web.Features.Jobs;
using Kibo.Web.Features.Platform;
using Kibo.Web.Features.Subscriptions;
using Kibo.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Kibo.Web.Features.Customers;

public class CustomerForm
{
    [Required, StringLength(120)]
    public string FullName { get; set; } = "";

    [Required, StringLength(20)]
    public string PhoneNumber { get; set; } = "";

    [EmailAddress, StringLength(200)]
    public string? Email { get; set; }

    public Guid? HomeSiteId { get; set; }

    [StringLength(1000)]
    public string? Notes { get; set; }
}

public record CustomerListRow(Customer Customer, string? CurrentPackage, SubscriptionStatus? CurrentStatus, DateTime? ExpiresAt,
    decimal LifetimeSpend);

public record CustomerProfile(Customer Customer, List<AccessAccount> Accounts, List<Subscription> Subscriptions,
    List<SupportNote> Notes, List<RadiusAccounting> RecentSessions, List<Payment> Payments, List<RadiusPostAuth> RecentAuth);

public class CustomerService(KiboDb kiboDb, CurrentUser currentUser, AuditLogger audit, TimeProvider clock)
{
    public async Task<List<CustomerListRow>> ListAsync(string? search, CustomerStatus? status)
    {
        Demand(Permissions.ViewCustomers);
        await using var db = kiboDb.ForCurrentUser();
        var query = db.Customers.AsNoTracking();
        if (status != null)
        {
            query = query.Where(c => c.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            var phone = PhoneNumbers.NormalizeUganda(term) ?? term;
            query = query.Where(c => EF.Functions.ILike(c.FullName, $"%{term}%") || c.PhoneNumber.Contains(phone) ||
                                     (c.Email != null && EF.Functions.ILike(c.Email, $"%{term}%")));
        }

        var customers = await query.OrderByDescending(c => c.CreatedAt).Take(300).ToListAsync();
        var ids = customers.Select(c => c.Id).ToList();
        var current = await db.Subscriptions.AsNoTracking()
            .Where(s => s.AccessAccount!.CustomerId != null && ids.Contains(s.AccessAccount.CustomerId!.Value) &&
                        (s.Status == SubscriptionStatus.Active || s.Status == SubscriptionStatus.Unused))
            .Select(s => new { CustomerId = s.AccessAccount!.CustomerId!.Value, s.PackageName, s.Status, s.ExpiresAt })
            .ToListAsync();
        var spend = await db.Payments.AsNoTracking()
            .Where(p => p.CustomerId != null && ids.Contains(p.CustomerId.Value) && p.Status == PaymentStatus.Succeeded)
            .GroupBy(p => p.CustomerId!.Value)
            .Select(g => new { CustomerId = g.Key, Total = g.Sum(p => p.Amount) })
            .ToDictionaryAsync(x => x.CustomerId, x => x.Total);

        return customers.Select(c =>
        {
            var sub = current.FirstOrDefault(s => s.CustomerId == c.Id);
            return new CustomerListRow(c, sub?.PackageName, sub?.Status, sub?.ExpiresAt, spend.GetValueOrDefault(c.Id));
        }).ToList();
    }

    public async Task<CustomerProfile?> GetAsync(Guid id)
    {
        Demand(Permissions.ViewCustomers);
        await using var db = kiboDb.ForCurrentUser();
        var customer = await db.Customers.AsNoTracking().Include(c => c.HomeSite).Include(c => c.Devices)
            .FirstOrDefaultAsync(c => c.Id == id);
        if (customer == null)
        {
            return null;
        }

        var accounts = await db.AccessAccounts.AsNoTracking().Where(a => a.CustomerId == id).ToListAsync();
        var accountIds = accounts.Select(a => a.Id).ToList();
        var usernames = accounts.Select(a => a.Username).ToList();
        var subscriptions = await db.Subscriptions.AsNoTracking().Where(s => accountIds.Contains(s.AccessAccountId))
            .OrderByDescending(s => s.PurchasedAt).Take(50).ToListAsync();
        var notes = await db.SupportNotes.AsNoTracking().Where(n => n.CustomerId == id).OrderByDescending(n => n.CreatedAt).ToListAsync();
        var nasNames = db.RadiusNas.Select(n => n.NasName);
        var sessions = await db.RadiusAccounting.AsNoTracking()
            .Where(a => usernames.Contains(a.UserName!) && nasNames.Contains(a.NasIpAddress!))
            .OrderByDescending(a => a.AcctStartTime).Take(25).ToListAsync();
        var payments = await db.Payments.AsNoTracking().Where(p => p.CustomerId == id).OrderByDescending(p => p.CreatedAt).Take(25).ToListAsync();
        var auth = await db.RadiusPostAuth.AsNoTracking()
            .Where(p => usernames.Contains(p.UserName) && nasNames.Contains(p.NasIpAddress!))
            .OrderByDescending(p => p.AuthDate).Take(15).ToListAsync();
        return new CustomerProfile(customer, accounts, subscriptions, notes, sessions, payments, auth);
    }

    public async Task<OperationResult<Customer>> CreateAsync(CustomerForm form)
    {
        Demand(Permissions.ManageCustomers);
        var phone = PhoneNumbers.NormalizeUganda(form.PhoneNumber);
        if (phone == null)
        {
            return OperationResult<Customer>.Fail("Enter a valid Ugandan phone number.");
        }

        await using var db = kiboDb.ForCurrentUser();
        if (await db.Customers.AnyAsync(c => c.PhoneNumber == phone))
        {
            return OperationResult<Customer>.Fail("A customer with this phone number already exists.");
        }

        var customer = new Customer
        {
            FullName = form.FullName.Trim(),
            PhoneNumber = phone,
            Email = string.IsNullOrWhiteSpace(form.Email) ? null : form.Email.Trim(),
            HomeSiteId = form.HomeSiteId,
            Notes = form.Notes,
            CreatedAt = clock.GetUtcNow().UtcDateTime
        };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        await audit.WriteAsync("customer.created", "Customer", customer.Id.ToString(), customer.FullName);
        return OperationResult<Customer>.Ok(customer);
    }

    public async Task<OperationResult<Customer>> UpdateAsync(Guid id, CustomerForm form)
    {
        Demand(Permissions.ManageCustomers);
        await using var db = kiboDb.ForCurrentUser();
        var customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == id);
        if (customer == null)
        {
            return OperationResult<Customer>.Fail("Customer not found.");
        }

        customer.FullName = form.FullName.Trim();
        customer.Email = string.IsNullOrWhiteSpace(form.Email) ? null : form.Email.Trim();
        customer.HomeSiteId = form.HomeSiteId;
        customer.Notes = form.Notes;
        await db.SaveChangesAsync();
        await audit.WriteAsync("customer.updated", "Customer", customer.Id.ToString(), customer.FullName);
        return OperationResult<Customer>.Ok(customer);
    }

    public async Task<OperationResult<Customer>> SetStatusAsync(Guid id, CustomerStatus status, string reason)
    {
        Demand(Permissions.ManageCustomers);
        var now = clock.GetUtcNow().UtcDateTime;
        await using var db = kiboDb.ForCurrentUser();
        var customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == id);
        if (customer == null)
        {
            return OperationResult<Customer>.Fail("Customer not found.");
        }

        customer.Status = status;
        db.SupportNotes.Add(new SupportNote
        {
            CustomerId = id, Author = currentUser.Email, CreatedAt = now,
            Text = $"Status changed to {status}. {reason}".Trim()
        });
        var accounts = await db.AccessAccounts.Where(a => a.CustomerId == id).ToListAsync();
        await db.SaveChangesAsync();
        foreach (var account in accounts)
        {
            await RadiusProvisioner.SyncAccountAsync(db, account.Id, now);
            if (status != CustomerStatus.Active)
            {
                JobQueue.Enqueue(db, JobKinds.DisconnectUser,
                    new DisconnectUserPayload(customer.TenantId, account.Username, $"Customer {status.ToString().ToLowerInvariant()}"),
                    now, customer.TenantId);
            }
        }

        await db.SaveChangesAsync();
        await audit.WriteAsync($"customer.{status.ToString().ToLowerInvariant()}", "Customer", id.ToString(), reason);
        return OperationResult<Customer>.Ok(customer);
    }

    public async Task<OperationResult<string>> ResetAccessCodeAsync(Guid accountId)
    {
        Demand(Permissions.ManageCustomers);
        var now = clock.GetUtcNow().UtcDateTime;
        await using var db = kiboDb.ForCurrentUser();
        var account = await db.AccessAccounts.FirstOrDefaultAsync(a => a.Id == accountId);
        if (account == null)
        {
            return OperationResult<string>.Fail("Account not found.");
        }

        account.Password = SecretProtector.NewCode(6, AccessAccounts.AccessCodeAlphabet);
        await db.SaveChangesAsync();
        await RadiusProvisioner.SyncAccountAsync(db, account.Id, now);
        await db.SaveChangesAsync();
        await audit.WriteAsync("access_account.code_reset", "AccessAccount", account.Id.ToString(), account.Username);
        return OperationResult<string>.Ok(account.Password);
    }

    public async Task AddNoteAsync(Guid customerId, string text)
    {
        Demand(Permissions.ManageCustomers);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        await using var db = kiboDb.ForCurrentUser();
        if (!await db.Customers.AnyAsync(c => c.Id == customerId))
        {
            return;
        }

        db.SupportNotes.Add(new SupportNote
        {
            CustomerId = customerId, Author = currentUser.Email, Text = text.Trim(), CreatedAt = clock.GetUtcNow().UtcDateTime
        });
        await db.SaveChangesAsync();
    }

    private void Demand(string permission)
    {
        if (!currentUser.Can(permission))
        {
            throw new UnauthorizedAccessException($"Missing permission {permission}.");
        }
    }
}
