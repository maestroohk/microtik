using System.ComponentModel.DataAnnotations;
using Kibo.Web.Data;
using Kibo.Web.Data.Entities;
using Kibo.Web.Features.Platform;
using Kibo.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Kibo.Web.Features.Sites;

public class SiteForm
{
    [Required, StringLength(80)]
    public string Name { get; set; } = "";

    [StringLength(120)]
    public string Location { get; set; } = "";

    [Required, StringLength(60)]
    public string PortalTitle { get; set; } = "";

    [StringLength(120)]
    public string PortalTagline { get; set; } = "";

    [Required, RegularExpression("^#[0-9a-fA-F]{6}$", ErrorMessage = "Use a hex colour such as #0a6b76.")]
    public string PortalAccent { get; set; } = "#0a6b76";

    [StringLength(30)]
    public string SupportPhone { get; set; } = "";
}

public class SiteService(KiboDb kiboDb, CurrentUser currentUser, AuditLogger audit, TimeProvider clock)
{
    public async Task<List<Site>> ListAsync()
    {
        await using var db = kiboDb.ForCurrentUser();
        return await db.Sites.AsNoTracking().OrderBy(s => s.Name).ToListAsync();
    }

    public async Task<Site?> GetAsync(Guid id)
    {
        await using var db = kiboDb.ForCurrentUser();
        return await db.Sites.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id);
    }

    public async Task<OperationResult<Site>> SaveAsync(Guid? id, SiteForm form)
    {
        if (!currentUser.Can(Permissions.ManageSettings))
        {
            throw new UnauthorizedAccessException("Missing permission to manage sites.");
        }

        await using var db = kiboDb.ForCurrentUser();
        Site site;
        if (id == null)
        {
            var tenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == db.TenantScope);
            site = new Site
            {
                CreatedAt = clock.GetUtcNow().UtcDateTime,
                Slug = await TenantService.UniqueSiteSlugAsync(db, $"{tenant.Slug}-{TenantService.Slugify(form.Name)}")
            };
            db.Sites.Add(site);
        }
        else
        {
            site = await db.Sites.FirstOrDefaultAsync(s => s.Id == id) ?? throw new InvalidOperationException("Site not found.");
        }

        site.Name = form.Name.Trim();
        site.Location = form.Location.Trim();
        site.PortalTitle = form.PortalTitle.Trim();
        site.PortalTagline = form.PortalTagline.Trim();
        site.PortalAccent = form.PortalAccent.ToLowerInvariant();
        site.SupportPhone = form.SupportPhone.Trim();
        await db.SaveChangesAsync();
        await audit.WriteAsync(id == null ? "site.created" : "site.updated", "Site", site.Id.ToString(), site.Name);
        return OperationResult<Site>.Ok(site);
    }
}
