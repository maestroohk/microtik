namespace Kibo.Web.Data.Entities;

public abstract class TenantEntity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }
}

public enum TenantStatus
{
    Active,
    Suspended
}

public enum TenantPlan
{
    Starter,
    Growth,
    Enterprise
}

public class Tenant
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public TenantStatus Status { get; set; } = TenantStatus.Active;
    public string? SuspensionReason { get; set; }
    public TenantPlan Plan { get; set; } = TenantPlan.Starter;
    public int MaxRouters { get; set; } = 3;
    public bool PppoeEnabled { get; set; }
    public bool AgentsEnabled { get; set; } = true;
    public string CountryCode { get; set; } = "UG";
    public string Currency { get; set; } = "UGX";
    public string TimeZone { get; set; } = "Africa/Kampala";
    public string ContactEmail { get; set; } = "";
    public string ContactPhone { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public enum UserRole
{
    Owner,
    Administrator,
    NetworkEngineer,
    Support,
    Finance,
    Agent
}

public class User
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid? TenantId { get; set; }
    public Tenant? Tenant { get; set; }
    public string Email { get; set; } = "";
    public string FullName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public UserRole Role { get; set; }
    public bool IsPlatformAdmin { get; set; }
    public bool IsActive { get; set; } = true;
    public int FailedLoginCount { get; set; }
    public DateTime? LockedUntil { get; set; }
    public string? TotpSecretProtected { get; set; }
    public long TotpLastStep { get; set; }
    public DateTime? MfaEnabledAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class AuditLog
{
    public long Id { get; set; }
    public Guid? TenantId { get; set; }
    public Guid? UserId { get; set; }
    public string UserName { get; set; } = "";
    public string Action { get; set; } = "";
    public string EntityType { get; set; } = "";
    public string? EntityId { get; set; }
    public string? Details { get; set; }
    public DateTime CreatedAt { get; set; }
}
