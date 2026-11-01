using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using Kibo.Web.Data;
using Kibo.Web.Data.Entities;
using Kibo.Web.Features.Jobs;
using Kibo.Web.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Kibo.Web.Features.Platform;

public class TenantForm
{
    [Required, StringLength(120)]
    public string Name { get; set; } = "";

    [Required, StringLength(40), RegularExpression("^[a-z0-9-]+$", ErrorMessage = "Use lowercase letters, numbers and dashes.")]
    public string Slug { get; set; } = "";

    public TenantPlan Plan { get; set; } = TenantPlan.Starter;

    [Range(1, 500)]
    public int MaxRouters { get; set; } = 3;

    public bool PppoeEnabled { get; set; }

    [Required, StringLength(3)]
    public string Currency { get; set; } = "UGX";

    [Required, EmailAddress]
    public string OwnerEmail { get; set; } = "";

    [Required, StringLength(120)]
    public string OwnerName { get; set; } = "";

    [Required, StringLength(128, MinimumLength = 12)]
    public string OwnerPassword { get; set; } = "";

    [Required, StringLength(80)]
    public string FirstSiteName { get; set; } = "";
}

public record TenantSummary(Tenant Tenant, int Routers, int Customers, int ActiveSubscriptions);

public partial class TenantService(KiboDb kiboDb, CurrentUser currentUser, AuditLogger audit,
    IPasswordHasher<User> hasher, TimeProvider clock)
{
    public async Task<List<TenantSummary>> ListAsync()
    {
        DemandPlatformAdmin();
        await using var db = kiboDb.ForSystem();
        return await db.Tenants.AsNoTracking().OrderBy(t => t.Name)
            .Select(t => new TenantSummary(t,
                db.Routers.Count(r => r.TenantId == t.Id),
                db.Customers.Count(c => c.TenantId == t.Id),
                db.Subscriptions.Count(s => s.TenantId == t.Id && s.Status == SubscriptionStatus.Active)))
            .ToListAsync();
    }

    public async Task<OperationResult<Tenant>> CreateAsync(TenantForm form)
    {
        DemandPlatformAdmin();
        var now = clock.GetUtcNow().UtcDateTime;
        await using var db = kiboDb.ForSystem();
        var slug = form.Slug.Trim().ToLowerInvariant();
        var email = form.OwnerEmail.Trim().ToLowerInvariant();

        if (await db.Tenants.AnyAsync(t => t.Slug == slug))
        {
            return OperationResult<Tenant>.Fail("This tenant address is already taken.");
        }

        if (await db.Users.AnyAsync(u => u.Email == email))
        {
            return OperationResult<Tenant>.Fail("A user with this email already exists.");
        }

        var tenant = new Tenant
        {
            Name = form.Name.Trim(),
            Slug = slug,
            Plan = form.Plan,
            MaxRouters = form.MaxRouters,
            PppoeEnabled = form.PppoeEnabled,
            Currency = form.Currency.ToUpperInvariant(),
            ContactEmail = email,
            CreatedAt = now
        };
        var owner = new User
        {
            TenantId = tenant.Id,
            Email = email,
            FullName = form.OwnerName.Trim(),
            Role = UserRole.Owner,
            CreatedAt = now
        };
        owner.PasswordHash = hasher.HashPassword(owner, form.OwnerPassword);

        db.Tenants.Add(tenant);
        db.Users.Add(owner);
        db.Sites.Add(new Site
        {
            TenantId = tenant.Id,
            Name = form.FirstSiteName.Trim(),
            Slug = await UniqueSiteSlugAsync(db, $"{slug}-{Slugify(form.FirstSiteName)}"),
            PortalTitle = tenant.Name,
            PortalTagline = "Fast, affordable internet",
            CreatedAt = now
        });
        await db.SaveChangesAsync();
        await audit.WriteAsync("tenant.created", "Tenant", tenant.Id.ToString(), tenant.Name, tenant.Id);
        return OperationResult<Tenant>.Ok(tenant);
    }

    public async Task<OperationResult<Tenant>> SetSuspendedAsync(Guid tenantId, bool suspended, string? reason)
    {
        DemandPlatformAdmin();
        await using var db = kiboDb.ForSystem();
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId);
        if (tenant == null)
        {
            return OperationResult<Tenant>.Fail("Tenant not found.");
        }

        if (suspended && string.IsNullOrWhiteSpace(reason))
        {
            return OperationResult<Tenant>.Fail("Give a reason for the suspension. The operator will see it.");
        }

        tenant.Status = suspended ? TenantStatus.Suspended : TenantStatus.Active;
        tenant.SuspensionReason = suspended ? reason!.Trim() : null;
        if (suspended)
        {
            var online = await (
                    from session in db.RadiusAccounting
                    join nas in db.RadiusNas on session.NasIpAddress equals nas.NasName
                    where session.AcctStopTime == null && nas.TenantId == tenantId
                    select session.UserName)
                .Distinct()
                .ToListAsync();
            foreach (var username in online.Where(u => u != null))
            {
                JobQueue.Enqueue(db, JobKinds.DisconnectUser,
                    new DisconnectUserPayload(tenantId, username!, "Operator suspended"), clock.GetUtcNow().UtcDateTime, tenantId);
            }
        }

        await db.SaveChangesAsync();
        await audit.WriteAsync(suspended ? "tenant.suspended" : "tenant.restored", "Tenant", tenant.Id.ToString(),
            reason, tenant.Id);
        return OperationResult<Tenant>.Ok(tenant);
    }

    public async Task<OperationResult<Tenant>> UpdatePlanAsync(Guid tenantId, TenantPlan plan, int maxRouters, bool pppoe, bool agents)
    {
        DemandPlatformAdmin();
        await using var db = kiboDb.ForSystem();
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId);
        if (tenant == null)
        {
            return OperationResult<Tenant>.Fail("Tenant not found.");
        }

        tenant.Plan = plan;
        tenant.MaxRouters = maxRouters;
        tenant.PppoeEnabled = pppoe;
        tenant.AgentsEnabled = agents;
        await db.SaveChangesAsync();
        await audit.WriteAsync("tenant.plan_changed", "Tenant", tenant.Id.ToString(),
            $"{plan}, {maxRouters} routers, PPPoE {(pppoe ? "on" : "off")}, agents {(agents ? "on" : "off")}", tenant.Id);
        return OperationResult<Tenant>.Ok(tenant);
    }

    public async Task<Tenant?> GetCurrentTenantAsync()
    {
        if (currentUser.TenantId == null)
        {
            return null;
        }

        await using var db = kiboDb.ForSystem();
        return await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == currentUser.TenantId);
    }

    public static async Task<string> UniqueSiteSlugAsync(AppDbContext db, string baseSlug)
    {
        var slug = baseSlug.Length > 50 ? baseSlug[..50] : baseSlug;
        var candidate = slug;
        var suffix = 2;
        while (await db.Sites.IgnoreQueryFilters().AnyAsync(s => s.Slug == candidate))
        {
            candidate = $"{slug}-{suffix++}";
        }

        return candidate;
    }

    public static string Slugify(string text) => NonSlugCharacters().Replace(text.Trim().ToLowerInvariant(), "-").Trim('-');

    private void DemandPlatformAdmin()
    {
        if (!currentUser.IsPlatformAdmin)
        {
            throw new UnauthorizedAccessException("Platform administrator access is required.");
        }
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlugCharacters();
}
