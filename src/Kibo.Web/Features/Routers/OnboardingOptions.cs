namespace Kibo.Web.Features.Routers;

public class OnboardingOptions
{
    public string RadiusServerAddress { get; set; } = "";
    public string PortalBaseUrl { get; set; } = "http://localhost:5290";
    public string ApiAllowedFrom { get; set; } = "";
}
