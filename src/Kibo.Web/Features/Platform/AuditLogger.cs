using Kibo.Web.Data;
using Kibo.Web.Data.Entities;
using Kibo.Web.Security;

namespace Kibo.Web.Features.Platform;

public class AuditLogger(KiboDb kiboDb, CurrentUser currentUser, TimeProvider clock)
{
    public async Task WriteAsync(string action, string entityType, string? entityId, string? details = null,
        Guid? tenantId = null)
    {
        await using var db = kiboDb.ForSystem();
        db.AuditLogs.Add(new AuditLog
        {
            TenantId = tenantId ?? currentUser.TenantId,
            UserId = currentUser.UserId,
            UserName = currentUser.IsAuthenticated ? currentUser.Email : "system",
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            Details = details,
            CreatedAt = clock.GetUtcNow().UtcDateTime
        });
        await db.SaveChangesAsync();
    }
}
