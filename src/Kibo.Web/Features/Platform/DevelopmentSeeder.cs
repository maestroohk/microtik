using Kibo.Web.Data;
using Kibo.Web.Data.Entities;
using Kibo.Web.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Kibo.Web.Features.Platform;

public class DevelopmentSeeder(KiboDb kiboDb, IConfiguration configuration, IPasswordHasher<User> hasher,
    SecretProtector secrets, TimeProvider clock, ILogger<DevelopmentSeeder> logger)
{
    public async Task SeedAsync()
    {
        await using var db = kiboDb.ForSystem();
        if (await db.Users.AnyAsync())
        {
            return;
        }

        var adminPassword = configuration["Seed:AdminPassword"];
        var ownerPassword = configuration["Seed:OwnerPassword"];
        if (string.IsNullOrEmpty(adminPassword) || string.IsNullOrEmpty(ownerPassword))
        {
            logger.LogWarning("Seed:AdminPassword and Seed:OwnerPassword are not configured; skipping development seed.");
            return;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        AddUser(db, null, "admin@kibo.test", "Platform Admin", UserRole.Owner, adminPassword, true, now);

        var kampala = new Tenant
        {
            Name = "Kampala Connect", Slug = "kampala-connect", Plan = TenantPlan.Growth, MaxRouters = 10,
            PppoeEnabled = true, ContactEmail = "owner@kampala-connect.test", ContactPhone = "256772000111", CreatedAt = now
        };
        var gulu = new Tenant
        {
            Name = "Gulu Wave Networks", Slug = "gulu-wave", Plan = TenantPlan.Starter, MaxRouters = 3,
            ContactEmail = "owner@gulu-wave.test", ContactPhone = "256701000222", CreatedAt = now
        };
        db.Tenants.AddRange(kampala, gulu);
        AddUser(db, kampala.Id, "owner@kampala-connect.test", "Grace Namuli", UserRole.Owner, ownerPassword, false, now);
        AddUser(db, kampala.Id, "noc@kampala-connect.test", "Isaac Okello", UserRole.NetworkEngineer, ownerPassword, false, now);
        AddUser(db, gulu.Id, "owner@gulu-wave.test", "Peter Ojok", UserRole.Owner, ownerPassword, false, now);

        var ntinda = new Site
        {
            TenantId = kampala.Id, Name = "Ntinda Market", Slug = "kampala-connect-ntinda", Location = "Ntinda, Kampala",
            PortalTitle = "Kampala Connect", PortalTagline = "Fast WiFi around Ntinda Market", PortalAccent = "#0f766e",
            SupportPhone = "0772 000111", CreatedAt = now
        };
        var entebbe = new Site
        {
            TenantId = kampala.Id, Name = "Entebbe Road Stage", Slug = "kampala-connect-entebbe-road",
            Location = "Entebbe Road, Kampala", PortalTitle = "Kampala Connect", PortalTagline = "Stay online on the go",
            PortalAccent = "#0f766e", SupportPhone = "0772 000111", CreatedAt = now
        };
        var guluTown = new Site
        {
            TenantId = gulu.Id, Name = "Gulu Town", Slug = "gulu-wave-town", Location = "Gulu",
            PortalTitle = "Gulu Wave", PortalTagline = "Internet for everyone", PortalAccent = "#7c3aed",
            SupportPhone = "0701 000222", CreatedAt = now
        };
        db.Sites.AddRange(ntinda, entebbe, guluTown);

        db.Packages.AddRange(
            Package(kampala.Id, "1 Hour", "Quick browsing", 500, 1, ValidityUnit.Hours, null, 2048, 1024, 1, now),
            Package(kampala.Id, "Daily 1GB", "24 hours, 1 GB", 1000, 1, ValidityUnit.Days, 1024, 4096, 2048, 2, now),
            Package(kampala.Id, "Weekly Unlimited", "7 days at up to 3 Mbps", 10000, 7, ValidityUnit.Days, null, 3072, 1024, 2, now),
            Package(kampala.Id, "Monthly 30GB", "30 GB, then 512 kbps", 50000, 1, ValidityUnit.CalendarMonths, 30720, 8192, 4096, 3, now,
                throttle: 512),
            Package(kampala.Id, "Night Owl", "Unlimited 10pm to 6am", 1500, 1, ValidityUnit.Days, null, 6144, 2048, 1, now,
                window: (new TimeOnly(22, 0), new TimeOnly(6, 0))),
            Package(kampala.Id, "Lab 5 Minutes / 20 MB", "For testing expiry and data caps", 100, 5, ValidityUnit.Minutes, 20, 1024, 512, 1, now),
            Package(gulu.Id, "Day Pass", "24 hours", 1000, 1, ValidityUnit.Days, null, 2048, 1024, 1, now));

        var simPassword = configuration["Lab:SimApiPassword"];
        if (!string.IsNullOrEmpty(simPassword))
        {
            AddLabRouter(db, kampala.Id, ntinda.Id, "Ntinda Hotspot (simulator)", 58721, "10.247.0.21", 53721,
                configuration["Lab:SimRadiusSecret1"], simPassword, now);
            AddLabRouter(db, kampala.Id, entebbe.Id, "Entebbe Road (simulator)", 58722, "10.247.0.22", 53722,
                configuration["Lab:SimRadiusSecret2"], simPassword, now);
        }

        await db.SaveChangesAsync();
        logger.LogInformation("Development seed created: 2 tenants, 3 sites, 7 packages");
    }

    private void AddUser(AppDbContext db, Guid? tenantId, string email, string name, UserRole role, string password,
        bool platformAdmin, DateTime now)
    {
        var user = new User
        {
            TenantId = tenantId, Email = email, FullName = name, Role = role, IsPlatformAdmin = platformAdmin, CreatedAt = now
        };
        user.PasswordHash = hasher.HashPassword(user, password);
        db.Users.Add(user);
    }

    private void AddLabRouter(AppDbContext db, Guid tenantId, Guid siteId, string name, int apiPort, string nasIp, int coaPort,
        string? radiusSecret, string apiPassword, DateTime now)
    {
        if (string.IsNullOrEmpty(radiusSecret))
        {
            return;
        }

        var router = new Router
        {
            TenantId = tenantId, SiteId = siteId, Name = name, ApiKind = RouterApiKind.Simulator,
            ManagementHost = "127.0.0.1", ManagementPort = apiPort, UseTls = false, ApiUsername = "kibo-api",
            ApiPasswordProtected = secrets.Protect(apiPassword), NasIpAddress = nasIp, RadiusSecret = radiusSecret,
            CoaPort = coaPort, HotspotInterface = "bridge-hotspot", UplinkInterface = "ether1", CreatedAt = now
        };
        db.Routers.Add(router);
        db.RadiusNas.Add(new RadiusNas
        {
            TenantId = tenantId, RouterId = router.Id, NasName = nasIp, ShortName = name, Secret = radiusSecret,
            Description = "Kibo lab simulator"
        });
    }

    private static Package Package(Guid tenantId, string name, string description, decimal price, int amount, ValidityUnit unit,
        long? dataMb, int downKbps, int upKbps, int devices, DateTime now, int? throttle = null,
        (TimeOnly Start, TimeOnly End)? window = null) => new()
    {
        TenantId = tenantId, Name = name, Description = description, Price = price, ValidityAmount = amount,
        ValidityUnit = unit, ValidityStart = ValidityStart.OnFirstLogin, DataQuotaMegabytes = dataMb,
        DownloadKbps = downKbps, UploadKbps = upKbps, MaxDevices = devices, ThrottledDownloadKbps = throttle,
        ThrottledUploadKbps = throttle, AccessWindowStart = window?.Start, AccessWindowEnd = window?.End,
        Currency = "UGX", SortOrder = (int)price, CreatedAt = now
    };
}
