using Kibo.Web.Data;
using Kibo.Web.Data.Entities;
using Kibo.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Kibo.Web.Features.Platform;

public class AuditQuery(KiboDb kiboDb, CurrentUser currentUser)
{
    public async Task<List<AuditLog>> ListAsync(bool platformWide, string? search, int take = 200)
    {
        if (platformWide && !currentUser.IsPlatformAdmin)
        {
            throw new UnauthorizedAccessException("Platform administrator access is required.");
        }

        if (!platformWide && !currentUser.Can(Permissions.ViewAudit))
        {
            throw new UnauthorizedAccessException("Missing permission to view the audit log.");
        }

        await using var db = platformWide ? kiboDb.ForSystem() : kiboDb.ForCurrentUser();
        var query = db.AuditLogs.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = $"%{search.Trim()}%";
            query = query.Where(a => EF.Functions.ILike(a.Action, term) || EF.Functions.ILike(a.UserName, term) ||
                                     (a.Details != null && EF.Functions.ILike(a.Details, term)));
        }

        return await query.OrderByDescending(a => a.Id).Take(take).ToListAsync();
    }
}
