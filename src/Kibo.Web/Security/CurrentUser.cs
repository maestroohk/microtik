using System.Security.Claims;
using Kibo.Web.Data.Entities;

namespace Kibo.Web.Security;

public class CurrentUser
{
    public Guid? UserId { get; private set; }
    public Guid? TenantId { get; private set; }
    public string Name { get; private set; } = "system";
    public string Email { get; private set; } = "";
    public UserRole? Role { get; private set; }
    public bool IsPlatformAdmin { get; private set; }
    public bool IsAuthenticated => UserId != null;

    public void Load(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true)
        {
            return;
        }

        UserId = Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
        Name = principal.FindFirstValue(ClaimTypes.Name) ?? "";
        Email = principal.FindFirstValue(ClaimTypes.Email) ?? "";
        IsPlatformAdmin = principal.HasClaim(KiboClaims.PlatformAdmin, "true");

        var tenant = principal.FindFirstValue(KiboClaims.TenantId);
        TenantId = string.IsNullOrEmpty(tenant) ? null : Guid.Parse(tenant);

        var role = principal.FindFirstValue(ClaimTypes.Role);
        Role = Enum.TryParse<UserRole>(role, out var parsed) ? parsed : null;
    }

    public void ImpersonateTenant(Guid? tenantId)
    {
        if (!IsPlatformAdmin)
        {
            throw new UnauthorizedAccessException("Only platform administrators can switch tenant context.");
        }

        TenantId = tenantId;
    }

    public bool Can(string permission) => IsPlatformAdmin || (Role != null && Permissions.For(Role.Value).Contains(permission));
}

public static class KiboClaims
{
    public const string TenantId = "kibo:tenant";
    public const string PlatformAdmin = "kibo:platform-admin";
}
