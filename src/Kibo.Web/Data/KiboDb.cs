using Kibo.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Kibo.Web.Data;

public class KiboDb(DbContextOptions<AppDbContext> options, CurrentUser currentUser)
{
    public AppDbContext ForCurrentUser()
    {
        if (currentUser.IsPlatformAdmin && currentUser.TenantId == null)
        {
            return new AppDbContext(options, null);
        }

        if (currentUser.TenantId == null)
        {
            throw new UnauthorizedAccessException("The current user does not belong to a tenant.");
        }

        return new AppDbContext(options, currentUser.TenantId);
    }

    public AppDbContext ForTenant(Guid tenantId) => new(options, tenantId);

    public AppDbContext ForSystem() => new(options, null);
}
