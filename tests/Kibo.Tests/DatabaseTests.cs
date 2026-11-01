using Kibo.Web.Data;
using Kibo.Web.Data.Entities;
using Kibo.Web.Features.Payments;
using Kibo.Web.Features.Subscriptions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kibo.Tests;

[Collection("database")]
public class DatabaseTests(TestDatabase database)
{
    [Fact]
    public async Task Tenant_scoped_context_never_returns_another_tenants_rows()
    {
        var (a, _, _) = await database.CreateTenantAsync("Alpha", "10.99.0.1");
        var (b, _, _) = await database.CreateTenantAsync("Bravo", "10.99.0.2");
        await using (var setup = database.System())
        {
            setup.Customers.Add(new Customer { TenantId = a.Id, FullName = "Alice", PhoneNumber = "256700000001", CreatedAt = DateTime.UtcNow });
            setup.Customers.Add(new Customer { TenantId = b.Id, FullName = "Bob", PhoneNumber = "256700000002", CreatedAt = DateTime.UtcNow });
            await setup.SaveChangesAsync();
        }

        await using var scoped = database.KiboDb().ForTenant(a.Id);
        var names = await scoped.Customers.Select(c => c.FullName).ToListAsync();
        var routers = await scoped.Routers.Select(r => r.TenantId).Distinct().ToListAsync();

        Assert.Contains("Alice", names);
        Assert.DoesNotContain("Bob", names);
        Assert.All(routers, id => Assert.Equal(a.Id, id));
    }

    [Fact]
    public async Task Tenant_scoped_context_refuses_to_write_into_another_tenant()
    {
        var (a, _, _) = await database.CreateTenantAsync("Charlie", "10.99.0.3");
        var (b, _, _) = await database.CreateTenantAsync("Delta", "10.99.0.4");

        await using var scoped = database.KiboDb().ForTenant(a.Id);
        scoped.Customers.Add(new Customer { TenantId = b.Id, FullName = "Mallory", PhoneNumber = "256700000003", CreatedAt = DateTime.UtcNow });

        await Assert.ThrowsAsync<InvalidOperationException>(() => scoped.SaveChangesAsync());
    }

    [Fact]
    public async Task Radius_only_authorises_a_username_on_routers_of_the_owning_tenant()
    {
        var (a, _, _) = await database.CreateTenantAsync("Echo", "10.99.0.5");
        await database.CreateTenantAsync("Foxtrot", "10.99.0.6");
        await AddAccessAsync(a.Id, "SHARED01", expiresAt: DateTime.UtcNow.AddHours(1));

        Assert.True(await EvaluateAsync("SHARED01", "10.99.0.5") is { Allowed: true });
        Assert.Null(await EvaluateAsync("SHARED01", "10.99.0.6"));
        Assert.Null(await EvaluateAsync("SHARED01", "10.250.250.250"));
    }

    [Fact]
    public async Task Radius_returns_remaining_time_and_bytes_and_rejects_when_used_up()
    {
        var (t, _, _) = await database.CreateTenantAsync("Golf", "10.99.0.7");
        var since = DateTime.UtcNow.AddMinutes(-30);
        await AddAccessAsync(t.Id, "DATA0001", expiresAt: DateTime.UtcNow.AddMinutes(10), dataLimit: 1000, countingSince: since);
        await AddSessionAsync("DATA0001", "10.99.0.7", since.AddMinutes(1), 400);

        var partly = await EvaluateAsync("DATA0001", "10.99.0.7");
        Assert.NotNull(partly);
        Assert.True(partly.Allowed);
        Assert.Equal(600, partly.TotalLimit);
        Assert.InRange(partly.SessionTimeout!.Value, 590, 601);

        await AddSessionAsync("DATA0001", "10.99.0.7", since.AddMinutes(2), 700);
        var exhausted = await EvaluateAsync("DATA0001", "10.99.0.7");
        Assert.False(exhausted!.Allowed);
        Assert.Contains("data", exhausted.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Radius_switches_to_fair_use_speed_instead_of_rejecting()
    {
        var (t, _, _) = await database.CreateTenantAsync("Hotel", "10.99.0.8");
        var since = DateTime.UtcNow.AddMinutes(-30);
        await AddAccessAsync(t.Id, "FUP00001", expiresAt: DateTime.UtcNow.AddDays(1), dataLimit: 1000, countingSince: since,
            throttled: "256k/512k");
        await AddSessionAsync("FUP00001", "10.99.0.8", since.AddMinutes(1), 5000);

        var result = await EvaluateAsync("FUP00001", "10.99.0.8");

        Assert.True(result!.Allowed);
        Assert.Equal("256k/512k", result.RateLimit);
        Assert.Null(result.TotalLimit);
    }

    [Fact]
    public async Task Radius_rejects_expired_access_and_suspended_tenants()
    {
        var (t, _, _) = await database.CreateTenantAsync("India", "10.99.0.9");
        await AddAccessAsync(t.Id, "OLD00001", expiresAt: DateTime.UtcNow.AddMinutes(-1));
        await AddAccessAsync(t.Id, "NEW00001", expiresAt: DateTime.UtcNow.AddHours(1));

        Assert.False((await EvaluateAsync("OLD00001", "10.99.0.9"))!.Allowed);
        Assert.True((await EvaluateAsync("NEW00001", "10.99.0.9"))!.Allowed);

        await using (var db = database.System())
        {
            await db.Tenants.Where(x => x.Id == t.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, TenantStatus.Suspended));
        }

        Assert.False((await EvaluateAsync("NEW00001", "10.99.0.9"))!.Allowed);
    }

    [Fact]
    public async Task Duplicate_and_concurrent_payment_verifications_grant_access_exactly_once()
    {
        var (tenant, site, _) = await database.CreateTenantAsync("Juliet", "10.99.0.10");
        Payment payment;
        await using (var db = database.System())
        {
            var package = new Package
            {
                TenantId = tenant.Id, Name = "Day", Price = 1000, ValidityAmount = 1, ValidityUnit = ValidityUnit.Days,
                DownloadKbps = 1024, UploadKbps = 512, CreatedAt = DateTime.UtcNow
            };
            payment = new Payment
            {
                TenantId = tenant.Id, SiteId = site.Id, PackageId = package.Id, Reference = $"T{Guid.NewGuid():N}"[..16],
                Provider = "fake", ProviderReference = $"P{Guid.NewGuid():N}", PhoneNumber = "256772999000", Amount = 1000,
                Status = PaymentStatus.Pending, PortalToken = Guid.NewGuid().ToString("N"), CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddMinutes(10)
            };
            db.AddRange(package, payment);
            await db.SaveChangesAsync();
        }

        var provider = new FakeProvider(ProviderPaymentStatus.Succeeded, 1000);
        var tasks = Enumerable.Range(0, 6).Select(async _ =>
        {
            var service = new PaymentService(database.KiboDb(), [provider], TimeProvider.System, NullLogger<PaymentService>.Instance);
            try
            {
                await service.VerifyAndSettleAsync(payment.Id, CancellationToken.None);
            }
            catch (DbUpdateException)
            {
            }
        });
        await Task.WhenAll(tasks);

        await using var check = database.System();
        Assert.Equal(PaymentStatus.Succeeded, (await check.Payments.FirstAsync(p => p.Id == payment.Id)).Status);
        Assert.Equal(1, await check.Subscriptions.CountAsync(s => s.SourceReference == payment.Reference));
        Assert.Equal(1, await check.LedgerEntries.CountAsync(l => l.PaymentId == payment.Id));
    }

    [Fact]
    public async Task Pending_or_declined_payments_never_create_access()
    {
        var (tenant, site, _) = await database.CreateTenantAsync("Kilo", "10.99.0.11");
        Guid declinedId;
        await using (var db = database.System())
        {
            var package = new Package { TenantId = tenant.Id, Name = "Hour", Price = 500, ValidityAmount = 1, ValidityUnit = ValidityUnit.Hours, CreatedAt = DateTime.UtcNow };
            var declined = new Payment
            {
                TenantId = tenant.Id, SiteId = site.Id, PackageId = package.Id, Reference = $"D{Guid.NewGuid():N}"[..16],
                Provider = "fake", ProviderReference = $"P{Guid.NewGuid():N}", PhoneNumber = "256772999001", Amount = 500,
                Status = PaymentStatus.Pending, PortalToken = Guid.NewGuid().ToString("N"), CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddMinutes(10)
            };
            db.AddRange(package, declined);
            await db.SaveChangesAsync();
            declinedId = declined.Id;
        }

        var amountMismatch = new FakeProvider(ProviderPaymentStatus.Succeeded, 1);
        await new PaymentService(database.KiboDb(), [amountMismatch], TimeProvider.System, NullLogger<PaymentService>.Instance)
            .VerifyAndSettleAsync(declinedId, CancellationToken.None);

        await using var check = database.System();
        var payment = await check.Payments.FirstAsync(p => p.Id == declinedId);
        Assert.Equal(PaymentStatus.Failed, payment.Status);
        Assert.Equal(0, await check.Subscriptions.CountAsync(s => s.SourceReference == payment.Reference));
        Assert.Equal(0, await check.LedgerEntries.CountAsync(l => l.PaymentId == declinedId));
    }

    [Fact]
    public async Task Ledger_entries_cannot_be_changed_or_deleted()
    {
        var (tenant, _, _) = await database.CreateTenantAsync("Lima", "10.99.0.12");
        await using var db = database.System();
        db.LedgerEntries.Add(new LedgerEntry { TenantId = tenant.Id, Kind = LedgerEntryKind.Sale, Amount = 100, Description = "x", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        await Assert.ThrowsAnyAsync<Exception>(() =>
            db.LedgerEntries.Where(l => l.TenantId == tenant.Id).ExecuteUpdateAsync(s => s.SetProperty(l => l.Amount, 1m)));
        await Assert.ThrowsAnyAsync<Exception>(() => db.LedgerEntries.Where(l => l.TenantId == tenant.Id).ExecuteDeleteAsync());
    }

    private async Task AddAccessAsync(Guid tenantId, string username, DateTime expiresAt, long? dataLimit = null,
        DateTime? countingSince = null, string? throttled = null)
    {
        await using var db = database.System();
        db.RadiusAccess.Add(new RadiusAccess
        {
            TenantId = tenantId, Username = username, AccessAccountId = Guid.NewGuid(), SubscriptionId = Guid.NewGuid(),
            Password = username, Enabled = true, ExpiresAt = expiresAt, DataLimitBytes = dataLimit,
            CountingSince = countingSince ?? DateTime.UtcNow.AddHours(-1), RateLimit = "512k/1024k", ThrottledRateLimit = throttled,
            SimultaneousUse = 1, TimeZone = "Africa/Kampala", UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private async Task AddSessionAsync(string username, string nas, DateTime start, long bytes)
    {
        await using var db = database.System();
        var id = Guid.NewGuid().ToString("N");
        db.RadiusAccounting.Add(new RadiusAccounting
        {
            AcctSessionId = id, AcctUniqueId = id, UserName = username, NasIpAddress = nas, AcctStartTime = start,
            AcctUpdateTime = start, AcctSessionTime = 60, AcctInputOctets = bytes / 2, AcctOutputOctets = bytes - bytes / 2
        });
        await db.SaveChangesAsync();
    }

    private async Task<Evaluation?> EvaluateAsync(string username, string nas)
    {
        await using var db = database.System();
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT allowed, message, session_timeout, rate_limit, total_limit FROM radius.evaluate(@u, @n)";
        var u = command.CreateParameter();
        u.ParameterName = "u";
        u.Value = username;
        var n = command.CreateParameter();
        n.ParameterName = "n";
        n.Value = nas;
        command.Parameters.Add(u);
        command.Parameters.Add(n);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new Evaluation(
            reader.GetBoolean(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetInt64(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetInt64(4));
    }

    private record Evaluation(bool Allowed, string? Message, long? SessionTimeout, string? RateLimit, long? TotalLimit);

    private class FakeProvider(ProviderPaymentStatus status, decimal amount) : IPaymentProvider
    {
        public string Name => "fake";

        public Task<ProviderInitiation> InitiateAsync(string paymentReference, string phoneNumber, decimal amount, string currency,
            string description, CancellationToken ct) => Task.FromResult(new ProviderInitiation(true, "x", null));

        public async Task<ProviderStatus> GetStatusAsync(string providerReference, CancellationToken ct)
        {
            await Task.Delay(Random.Shared.Next(5, 40), ct);
            return new ProviderStatus(status, amount, "UGX", null);
        }

        public ProviderCallback? ParseAuthenticatedCallback(string body, IHeaderDictionary headers) => null;
    }
}
