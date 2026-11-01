using Kibo.Web.Data;
using Kibo.Web.Data.Entities;
using Kibo.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Kibo.Tests;

public class TestDatabase : IAsyncLifetime
{
    public DbContextOptions<AppDbContext> Options { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(ConnectionString())
            .UseSnakeCaseNamingConvention()
            .Options;

        await using var db = new AppDbContext(Options, null);
        await db.Database.EnsureDeletedAsync();
        await DatabaseSetup.MigrateAsync(db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public KiboDb KiboDb() => new(Options, new CurrentUser());

    public AppDbContext System() => new(Options, null);

    public async Task<(Tenant Tenant, Site Site, RadiusNas Nas)> CreateTenantAsync(string name, string nasIp)
    {
        await using var db = System();
        var tenant = new Tenant { Name = name, Slug = $"{name.ToLowerInvariant()}-{Guid.NewGuid():N}"[..30], CreatedAt = DateTime.UtcNow };
        var site = new Site { TenantId = tenant.Id, Name = $"{name} site", Slug = $"s-{Guid.NewGuid():N}", CreatedAt = DateTime.UtcNow };
        var router = new Router
        {
            TenantId = tenant.Id, SiteId = site.Id, Name = $"{name} router", ManagementHost = "127.0.0.1", NasIpAddress = nasIp,
            RadiusSecret = "secret", ApiPasswordProtected = "x", CreatedAt = DateTime.UtcNow
        };
        var nas = new RadiusNas { TenantId = tenant.Id, RouterId = router.Id, NasName = nasIp, ShortName = name, Secret = "secret" };
        db.AddRange(tenant, site, router, nas);
        await db.SaveChangesAsync();
        return (tenant, site, nas);
    }

    private static string ConnectionString()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("KIBO_TEST_CONNECTION");
        if (!string.IsNullOrEmpty(fromEnvironment))
        {
            return fromEnvironment;
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "deploy", ".env")))
        {
            directory = directory.Parent;
        }

        if (directory == null)
        {
            throw new InvalidOperationException("Set KIBO_TEST_CONNECTION or create deploy/.env to run database tests.");
        }

        var password = File.ReadAllLines(Path.Combine(directory.FullName, "deploy", ".env"))
            .Select(line => line.Split('=', 2))
            .First(parts => parts[0] == "KIBO_DB_PASSWORD")[1];
        return $"Host=127.0.0.1;Port=55432;Database=kibo_test;Username=kibo;Password={password}";
    }
}

[CollectionDefinition("database")]
public class DatabaseCollection : ICollectionFixture<TestDatabase>;
