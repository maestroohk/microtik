namespace Kibo.Web.Data.Entities;

public class Site : TenantEntity
{
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Location { get; set; } = "";
    public string PortalTitle { get; set; } = "";
    public string PortalTagline { get; set; } = "";
    public string PortalAccent { get; set; } = "#0f766e";
    public string SupportPhone { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public enum RouterStatus
{
    Pending,
    Online,
    Offline,
    AuthenticationFailed
}

public enum RouterApiKind
{
    RestApi,
    Simulator
}

public class Router : TenantEntity
{
    public Guid SiteId { get; set; }
    public Site? Site { get; set; }
    public string Name { get; set; } = "";
    public RouterApiKind ApiKind { get; set; } = RouterApiKind.RestApi;
    public string ManagementHost { get; set; } = "";
    public int ManagementPort { get; set; } = 443;
    public bool UseTls { get; set; } = true;
    public bool AllowSelfSignedCertificate { get; set; }
    public string ApiUsername { get; set; } = "";
    public string ApiPasswordProtected { get; set; } = "";
    public string NasIpAddress { get; set; } = "";
    public string RadiusSecret { get; set; } = "";
    public int CoaPort { get; set; } = 3799;
    public string HotspotInterface { get; set; } = "";
    public string UplinkInterface { get; set; } = "";
    public bool ProvisioningEnabled { get; set; }
    public RouterStatus Status { get; set; } = RouterStatus.Pending;
    public string? Identity { get; set; }
    public string? BoardName { get; set; }
    public string? RouterOsVersion { get; set; }
    public string? Architecture { get; set; }
    public string? SerialNumber { get; set; }
    public bool? SupportsRestApi { get; set; }
    public bool? HotspotPresent { get; set; }
    public bool? RadiusConfigured { get; set; }
    public string? LastError { get; set; }
    public int ConsecutiveFailures { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public DateTime? LastCheckedAt { get; set; }
    public DateTime? NextCheckAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class RouterHealthSample
{
    public long Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid RouterId { get; set; }
    public DateTime TakenAt { get; set; }
    public int CpuLoadPercent { get; set; }
    public long FreeMemoryBytes { get; set; }
    public long TotalMemoryBytes { get; set; }
    public long UptimeSeconds { get; set; }
    public int ActiveHotspotUsers { get; set; }
    public long RxBitsPerSecond { get; set; }
    public long TxBitsPerSecond { get; set; }
    public bool IsSynthetic { get; set; }
}

public enum RouterEventKind
{
    Connected,
    Disconnected,
    AuthenticationFailed,
    Rebooted,
    CapabilitiesDetected,
    ConfigurationChanged,
    ProvisioningFailed
}

public class RouterEvent
{
    public long Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid RouterId { get; set; }
    public RouterEventKind Kind { get; set; }
    public string Message { get; set; } = "";
    public DateTime OccurredAt { get; set; }
}
