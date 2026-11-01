namespace Kibo.Web.Data.Entities;

public enum CustomerStatus
{
    Active,
    Suspended,
    Closed
}

public class Customer : TenantEntity
{
    public string FullName { get; set; } = "";
    public string PhoneNumber { get; set; } = "";
    public string? Email { get; set; }
    public Guid? HomeSiteId { get; set; }
    public Site? HomeSite { get; set; }
    public CustomerStatus Status { get; set; } = CustomerStatus.Active;
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<AccessAccount> AccessAccounts { get; set; } = [];
    public List<CustomerDevice> Devices { get; set; } = [];
}

public class CustomerDevice : TenantEntity
{
    public Guid CustomerId { get; set; }
    public string MacAddress { get; set; } = "";
    public string Label { get; set; } = "";
    public DateTime FirstSeenAt { get; set; }
    public DateTime LastSeenAt { get; set; }
}

public class SupportNote : TenantEntity
{
    public Guid CustomerId { get; set; }
    public string Author { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public enum ServiceType
{
    Hotspot,
    Pppoe
}

public enum AccessAccountKind
{
    Hotspot,
    Pppoe,
    Voucher
}

public enum AccessAccountStatus
{
    Active,
    Suspended,
    Disabled
}

public class AccessAccount : TenantEntity
{
    public Guid? CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public AccessAccountKind Kind { get; set; }
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public AccessAccountStatus Status { get; set; } = AccessAccountStatus.Active;
    public string? StaticIpAddress { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<Subscription> Subscriptions { get; set; } = [];
}

public enum ValidityUnit
{
    Minutes,
    Hours,
    Days,
    CalendarMonths
}

public enum ValidityStart
{
    OnPurchase,
    OnFirstLogin
}

public class Package : TenantEntity
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public ServiceType ServiceType { get; set; } = ServiceType.Hotspot;
    public decimal Price { get; set; }
    public string Currency { get; set; } = "UGX";
    public int ValidityAmount { get; set; } = 1;
    public ValidityUnit ValidityUnit { get; set; } = ValidityUnit.Days;
    public ValidityStart ValidityStart { get; set; } = ValidityStart.OnFirstLogin;
    public int ActivationDeadlineDays { get; set; } = 30;
    public int? TimeQuotaMinutes { get; set; }
    public long? DataQuotaMegabytes { get; set; }
    public int? MaxSessionMinutes { get; set; }
    public int DownloadKbps { get; set; }
    public int UploadKbps { get; set; }
    public int? ThrottledDownloadKbps { get; set; }
    public int? ThrottledUploadKbps { get; set; }
    public int MaxDevices { get; set; } = 1;
    public TimeOnly? AccessWindowStart { get; set; }
    public TimeOnly? AccessWindowEnd { get; set; }
    public int GraceMinutes { get; set; }
    public bool IsRecurring { get; set; }
    public bool IsPromotional { get; set; }
    public DateTime? AvailableFrom { get; set; }
    public DateTime? AvailableUntil { get; set; }
    public bool ShowOnPortal { get; set; } = true;
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
    public Guid? SiteId { get; set; }
    public DateTime CreatedAt { get; set; }
}

public enum SubscriptionStatus
{
    Scheduled,
    Unused,
    Active,
    Exhausted,
    Expired,
    Cancelled,
    Suspended
}

public enum SubscriptionSource
{
    Payment,
    Voucher,
    Manual,
    Renewal
}

public class Subscription : TenantEntity
{
    public Guid AccessAccountId { get; set; }
    public AccessAccount? AccessAccount { get; set; }
    public Guid PackageId { get; set; }
    public Package? Package { get; set; }
    public string PackageName { get; set; } = "";
    public SubscriptionStatus Status { get; set; }
    public SubscriptionSource Source { get; set; }
    public string? SourceReference { get; set; }
    public decimal PricePaid { get; set; }
    public string Currency { get; set; } = "UGX";

    public int ValidityAmount { get; set; }
    public ValidityUnit ValidityUnit { get; set; }
    public ValidityStart ValidityStart { get; set; }
    public long? DataQuotaBytes { get; set; }
    public long? TimeQuotaSeconds { get; set; }
    public int? MaxSessionSeconds { get; set; }
    public int DownloadKbps { get; set; }
    public int UploadKbps { get; set; }
    public int? ThrottledDownloadKbps { get; set; }
    public int? ThrottledUploadKbps { get; set; }
    public int MaxDevices { get; set; }
    public TimeOnly? AccessWindowStart { get; set; }
    public TimeOnly? AccessWindowEnd { get; set; }
    public int GraceSeconds { get; set; }
    public bool IsRecurring { get; set; }

    public DateTime PurchasedAt { get; set; }
    public DateTime ActivateBy { get; set; }
    public DateTime? CountingSince { get; set; }
    public DateTime? ActivatedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public long UsedBytes { get; set; }
    public long UsedSeconds { get; set; }
    public DateTime? UsageUpdatedAt { get; set; }
    public DateTime? ThrottledAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public string? EndReason { get; set; }
    public uint Version { get; set; }
}

public class RadiusAccess
{
    public string Username { get; set; } = "";
    public Guid TenantId { get; set; }
    public Guid AccessAccountId { get; set; }
    public Guid? SubscriptionId { get; set; }
    public string Password { get; set; } = "";
    public bool Enabled { get; set; }
    public string ServiceType { get; set; } = "hotspot";
    public DateTime? CountingSince { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public long? ValiditySeconds { get; set; }
    public DateTime? ActivateBy { get; set; }
    public long? DataLimitBytes { get; set; }
    public long? TimeLimitSeconds { get; set; }
    public int? MaxSessionSeconds { get; set; }
    public string RateLimit { get; set; } = "";
    public string? ThrottledRateLimit { get; set; }
    public int SimultaneousUse { get; set; } = 1;
    public TimeOnly? WindowStart { get; set; }
    public TimeOnly? WindowEnd { get; set; }
    public string TimeZone { get; set; } = "UTC";
    public string? FramedIpAddress { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class RadiusNas
{
    public int Id { get; set; }
    public string NasName { get; set; } = "";
    public string ShortName { get; set; } = "";
    public string Type { get; set; } = "other";
    public string Secret { get; set; } = "";
    public string? Description { get; set; }
    public Guid TenantId { get; set; }
    public Guid RouterId { get; set; }
}

public class RadiusAccounting
{
    public long RadAcctId { get; set; }
    public string AcctSessionId { get; set; } = "";
    public string AcctUniqueId { get; set; } = "";
    public string? UserName { get; set; }
    public string? NasIpAddress { get; set; }
    public string? NasPortId { get; set; }
    public DateTime? AcctStartTime { get; set; }
    public DateTime? AcctUpdateTime { get; set; }
    public DateTime? AcctStopTime { get; set; }
    public long? AcctSessionTime { get; set; }
    public long? AcctInputOctets { get; set; }
    public long? AcctOutputOctets { get; set; }
    public string? CalledStationId { get; set; }
    public string? CallingStationId { get; set; }
    public string? AcctTerminateCause { get; set; }
    public string? FramedIpAddress { get; set; }
    public string? Class { get; set; }
}

public class RadiusPostAuth
{
    public long Id { get; set; }
    public string UserName { get; set; } = "";
    public string? Reply { get; set; }
    public string? CallingStationId { get; set; }
    public string? NasIpAddress { get; set; }
    public DateTime AuthDate { get; set; }
}
