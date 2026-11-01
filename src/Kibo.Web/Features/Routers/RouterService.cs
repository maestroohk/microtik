using System.ComponentModel.DataAnnotations;
using System.Net;
using Kibo.Web.Data;
using Kibo.Web.Data.Entities;
using Kibo.Web.Features.Platform;
using Kibo.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Kibo.Web.Features.Routers;

public class RouterForm
{
    [Required, StringLength(80)]
    public string Name { get; set; } = "";

    [Required]
    public Guid SiteId { get; set; }

    public RouterApiKind ApiKind { get; set; } = RouterApiKind.RestApi;

    [Required, StringLength(200)]
    public string ManagementHost { get; set; } = "";

    [Range(1, 65535)]
    public int ManagementPort { get; set; } = 443;

    public bool UseTls { get; set; } = true;
    public bool AllowSelfSignedCertificate { get; set; } = true;

    [Required, StringLength(64)]
    public string ApiUsername { get; set; } = "kibo-api";

    [StringLength(128)]
    public string ApiPassword { get; set; } = "";

    [Required, StringLength(45)]
    public string NasIpAddress { get; set; } = "";

    [Range(1, 65535)]
    public int CoaPort { get; set; } = 3799;

    [StringLength(64)]
    public string HotspotInterface { get; set; } = "";

    [StringLength(64)]
    public string UplinkInterface { get; set; } = "";
}

public class RouterService(KiboDb kiboDb, CurrentUser currentUser, SecretProtector secrets, AuditLogger audit,
    RouterHealthChecker healthChecker, TimeProvider clock)
{
    public async Task<List<Router>> ListAsync()
    {
        await using var db = kiboDb.ForCurrentUser();
        return await db.Routers.AsNoTracking().Include(r => r.Site).OrderBy(r => r.Name).ToListAsync();
    }

    public async Task<Router?> GetAsync(Guid id)
    {
        await using var db = kiboDb.ForCurrentUser();
        return await db.Routers.AsNoTracking().Include(r => r.Site).FirstOrDefaultAsync(r => r.Id == id);
    }

    public async Task<List<RouterEvent>> GetEventsAsync(Guid routerId, int take = 30)
    {
        await using var db = kiboDb.ForCurrentUser();
        return await db.RouterEvents.AsNoTracking().Where(e => e.RouterId == routerId)
            .OrderByDescending(e => e.OccurredAt).Take(take).ToListAsync();
    }

    public async Task<List<RouterHealthSample>> GetHealthAsync(Guid routerId, TimeSpan window)
    {
        var since = clock.GetUtcNow().UtcDateTime - window;
        await using var db = kiboDb.ForCurrentUser();
        return await db.RouterHealthSamples.AsNoTracking()
            .Where(s => s.RouterId == routerId && s.TakenAt >= since)
            .OrderBy(s => s.TakenAt).ToListAsync();
    }

    public async Task<OperationResult<Router>> RegisterAsync(RouterForm form)
    {
        Demand(Permissions.ManageRouters);
        var validation = ValidateAddresses(form);
        if (validation != null)
        {
            return OperationResult<Router>.Fail(validation);
        }

        if (string.IsNullOrWhiteSpace(form.ApiPassword))
        {
            return OperationResult<Router>.Fail("An API password is required for a new router.");
        }

        await using var db = kiboDb.ForCurrentUser();
        var tenant = await db.Tenants.FirstAsync(t => t.Id == db.TenantScope);
        var routerCount = await db.Routers.CountAsync();
        if (routerCount >= tenant.MaxRouters)
        {
            return OperationResult<Router>.Fail(
                $"Your {tenant.Plan} plan allows {tenant.MaxRouters} routers. Upgrade the plan to add more.");
        }

        if (!await db.Sites.AnyAsync(s => s.Id == form.SiteId))
        {
            return OperationResult<Router>.Fail("Choose a site for this router.");
        }

        if (await db.Routers.AnyAsync(r => r.Name == form.Name))
        {
            return OperationResult<Router>.Fail("A router with this name already exists.");
        }

        if (await db.RadiusNas.IgnoreQueryFilters().AnyAsync(n => n.NasName == form.NasIpAddress))
        {
            return OperationResult<Router>.Fail("This RADIUS source address is already registered to another router.");
        }

        var router = new Router
        {
            CreatedAt = clock.GetUtcNow().UtcDateTime,
            RadiusSecret = SecretProtector.NewSecret(24),
            ApiPasswordProtected = secrets.Protect(form.ApiPassword)
        };
        Apply(router, form);
        db.Routers.Add(router);
        db.RadiusNas.Add(new RadiusNas
        {
            NasName = router.NasIpAddress,
            ShortName = router.Name,
            Secret = router.RadiusSecret,
            Description = $"Kibo router {router.Id}",
            RouterId = router.Id
        });
        await db.SaveChangesAsync();
        await audit.WriteAsync("router.registered", "Router", router.Id.ToString(), $"{router.Name} ({router.ManagementHost})");
        return OperationResult<Router>.Ok(router);
    }

    public async Task<OperationResult<Router>> UpdateAsync(Guid id, RouterForm form)
    {
        Demand(Permissions.ManageRouters);
        var validation = ValidateAddresses(form);
        if (validation != null)
        {
            return OperationResult<Router>.Fail(validation);
        }

        await using var db = kiboDb.ForCurrentUser();
        var router = await db.Routers.FirstOrDefaultAsync(r => r.Id == id);
        if (router == null)
        {
            return OperationResult<Router>.Fail("Router not found.");
        }

        var nas = await db.RadiusNas.FirstAsync(n => n.RouterId == router.Id);
        if (nas.NasName != form.NasIpAddress &&
            await db.RadiusNas.IgnoreQueryFilters().AnyAsync(n => n.NasName == form.NasIpAddress))
        {
            return OperationResult<Router>.Fail("This RADIUS source address is already registered to another router.");
        }

        Apply(router, form);
        if (!string.IsNullOrWhiteSpace(form.ApiPassword))
        {
            router.ApiPasswordProtected = secrets.Protect(form.ApiPassword);
        }

        nas.NasName = router.NasIpAddress;
        nas.ShortName = router.Name;
        await db.SaveChangesAsync();
        await audit.WriteAsync("router.updated", "Router", router.Id.ToString(), router.Name);
        return OperationResult<Router>.Ok(router);
    }

    public async Task<OperationResult<string>> RotateRadiusSecretAsync(Guid id)
    {
        Demand(Permissions.ManageRouters);
        await using var db = kiboDb.ForCurrentUser();
        var router = await db.Routers.FirstOrDefaultAsync(r => r.Id == id);
        if (router == null)
        {
            return OperationResult<string>.Fail("Router not found.");
        }

        var nas = await db.RadiusNas.FirstAsync(n => n.RouterId == router.Id);
        router.RadiusSecret = SecretProtector.NewSecret(24);
        nas.Secret = router.RadiusSecret;
        await db.SaveChangesAsync();
        await audit.WriteAsync("router.radius_secret_rotated", "Router", router.Id.ToString(), router.Name);
        return OperationResult<string>.Ok(router.RadiusSecret);
    }

    public async Task<OperationResult<bool>> DeleteAsync(Guid id)
    {
        Demand(Permissions.ManageRouters);
        await using var db = kiboDb.ForCurrentUser();
        var router = await db.Routers.FirstOrDefaultAsync(r => r.Id == id);
        if (router == null)
        {
            return OperationResult<bool>.Fail("Router not found.");
        }

        await db.RadiusNas.Where(n => n.RouterId == id).ExecuteDeleteAsync();
        db.Routers.Remove(router);
        await db.SaveChangesAsync();
        await audit.WriteAsync("router.deleted", "Router", id.ToString(), router.Name);
        return OperationResult<bool>.Ok(true);
    }

    public async Task<RouterCheckResult> CheckNowAsync(Guid id)
    {
        Demand(Permissions.ViewRouters);
        await using var db = kiboDb.ForCurrentUser();
        if (!await db.Routers.AnyAsync(r => r.Id == id))
        {
            return new RouterCheckResult(false, "Router not found.", null);
        }

        return await healthChecker.CheckAsync(id, CancellationToken.None);
    }

    public async Task<OperationResult<string>> ExportConfigurationAsync(Guid id)
    {
        Demand(Permissions.ManageRouters);
        await using var db = kiboDb.ForCurrentUser();
        var router = await db.Routers.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id);
        if (router == null)
        {
            return OperationResult<string>.Fail("Router not found.");
        }

        try
        {
            var export = await healthChecker.ExportAsync(router, CancellationToken.None);
            await audit.WriteAsync("router.config_exported", "Router", router.Id.ToString(), router.Name);
            return OperationResult<string>.Ok(export);
        }
        catch (RouterOsException ex)
        {
            return OperationResult<string>.Fail(ex.Message);
        }
    }

    private static void Apply(Router router, RouterForm form)
    {
        router.Name = form.Name.Trim();
        router.SiteId = form.SiteId;
        router.ApiKind = form.ApiKind;
        router.ManagementHost = form.ManagementHost.Trim();
        router.ManagementPort = form.ManagementPort;
        router.UseTls = form.UseTls;
        router.AllowSelfSignedCertificate = form.AllowSelfSignedCertificate;
        router.ApiUsername = form.ApiUsername.Trim();
        router.NasIpAddress = form.NasIpAddress.Trim();
        router.CoaPort = form.CoaPort;
        router.HotspotInterface = form.HotspotInterface.Trim();
        router.UplinkInterface = form.UplinkInterface.Trim();
    }

    private static string? ValidateAddresses(RouterForm form)
    {
        if (!IPAddress.TryParse(form.NasIpAddress.Trim(), out _))
        {
            return "The RADIUS source address must be an IP address (the router's tunnel or LAN address).";
        }

        var host = form.ManagementHost.Trim();
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown)
        {
            return "The management host must be a host name or IP address.";
        }

        return null;
    }

    private void Demand(string permission)
    {
        if (!currentUser.Can(permission))
        {
            throw new UnauthorizedAccessException($"Missing permission {permission}.");
        }
    }
}
