using System.Net;
using Kibo.Web.Data;
using Kibo.Web.Data.Entities;
using Kibo.Web.Features.Platform;
using Kibo.Web.Features.Subscriptions;
using Kibo.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Kibo.Web.Features.Pppoe;

public class PppoeService(KiboDb kiboDb, CurrentUser currentUser, AuditLogger audit, TimeProvider clock)
{
    public const string PasswordAlphabet = "abcdefghjkmnpqrstuvwxyz23456789";

    public async Task<OperationResult<AccessAccount>> CreateAccountAsync(Guid customerId, string? username, string? staticIp)
    {
        if (!currentUser.Can(Permissions.ManageCustomers))
        {
            throw new UnauthorizedAccessException("Missing permission to manage customers.");
        }

        await using var db = kiboDb.ForCurrentUser();
        var tenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == db.TenantScope);
        if (!tenant.PppoeEnabled)
        {
            return OperationResult<AccessAccount>.Fail("PPPoE is not enabled for your subscription plan.");
        }

        var customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == customerId);
        if (customer == null)
        {
            return OperationResult<AccessAccount>.Fail("Customer not found.");
        }

        var name = string.IsNullOrWhiteSpace(username) ? $"ppp{customer.PhoneNumber[^9..]}" : username.Trim().ToLowerInvariant();
        if (name.Length is < 3 or > 40 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'))
        {
            return OperationResult<AccessAccount>.Fail("Use 3–40 letters, numbers, dots, dashes or underscores for the PPPoE username.");
        }

        if (await db.AccessAccounts.AnyAsync(a => a.Username == name))
        {
            return OperationResult<AccessAccount>.Fail("That PPPoE username is already in use.");
        }

        if (!string.IsNullOrWhiteSpace(staticIp) && !IPAddress.TryParse(staticIp.Trim(), out _))
        {
            return OperationResult<AccessAccount>.Fail("The static IP must be an IPv4 address.");
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var account = new AccessAccount
        {
            CustomerId = customer.Id,
            Kind = AccessAccountKind.Pppoe,
            Username = name,
            Password = SecretProtector.NewCode(10, PasswordAlphabet),
            StaticIpAddress = string.IsNullOrWhiteSpace(staticIp) ? null : staticIp.Trim(),
            CreatedAt = now
        };
        db.AccessAccounts.Add(account);
        await db.SaveChangesAsync();
        await RadiusProvisioner.SyncAccountAsync(db, account.Id, now);
        await db.SaveChangesAsync();
        await audit.WriteAsync("pppoe.account_created", "AccessAccount", account.Id.ToString(), name);
        return OperationResult<AccessAccount>.Ok(account);
    }
}
