namespace Kibo.Lab;

public class LabOptions
{
    public string Identity { get; set; } = "lab-sim";
    public string ApiUsername { get; set; } = "kibo-api";
    public string ApiPassword { get; set; } = "";
    public string RadiusServer { get; set; } = "127.0.0.1";
    public string RadiusSecret { get; set; } = "";
    public string NasIpAddress { get; set; } = "127.0.0.1";
    public string PublicBaseUrl { get; set; } = "http://localhost:58721";
    public string PortalBaseUrl { get; set; } = "http://localhost:5290";
    public string PortalSiteSlug { get; set; } = "kampala-connect-ntinda";
    public string MomoApiKey { get; set; } = "";
    public string MomoCallbackSecret { get; set; } = "";
}
