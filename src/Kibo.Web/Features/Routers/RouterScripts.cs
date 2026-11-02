using System.Text;
using Kibo.Web.Data.Entities;

namespace Kibo.Web.Features.Routers;

public record OnboardingSettings(string RadiusServerAddress, string PortalBaseUrl, string PortalHost, string ApiAllowedFrom, int PortalPort = 443);

public static class RouterScripts
{
    public static string Onboarding(Router router, Site site, OnboardingSettings settings)
    {
        var hotspotInterface = string.IsNullOrWhiteSpace(router.HotspotInterface) ? "bridge-hotspot" : router.HotspotInterface;
        var script = new StringBuilder();
        script.AppendLine($"# Kibo onboarding for \"{router.Name}\" (site: {site.Name})");
        script.AppendLine("# Review every line before pasting into a RouterOS v7 terminal. Nothing is applied automatically.");
        script.AppendLine("# Assumes the hotspot itself already exists (/ip hotspot setup) on " + hotspotInterface + ".");
        script.AppendLine();
        script.AppendLine("# 0. Ownership proof. Kibo enables RADIUS for this router only after it reads this note over the API.");
        script.AppendLine($"/system note set show-at-login=no note=\"kibo-claim:{router.ClaimToken}\"");
        script.AppendLine();
        script.AppendLine("# 1. Restricted API user for Kibo (read + hotspot session control only)");
        script.AppendLine("/user group add name=kibo-api policy=read,write,api,rest-api,test,!ftp,!local,!telnet,!ssh,!reboot,!policy,!password,!sniff,!sensitive,!romon,!winbox,!web");
        script.AppendLine($"/user add name={router.ApiUsername} group=kibo-api address={settings.ApiAllowedFrom} password=\"<paste the API password you entered in Kibo>\"");
        script.AppendLine("/ip service set www-ssl disabled=no address=" + settings.ApiAllowedFrom);
        script.AppendLine();
        script.AppendLine("# 2. RADIUS client pointing at Kibo, with this router's own secret");
        script.AppendLine($"/radius add address={settings.RadiusServerAddress} secret=\"{router.RadiusSecret}\" service=hotspot,ppp src-address={router.NasIpAddress} timeout=3s comment=kibo");
        script.AppendLine($"/radius incoming set accept=yes port={router.CoaPort}");
        script.AppendLine();
        script.AppendLine("# 3. Hotspot profile: RADIUS auth, 1-minute interim accounting, CHAP login from the external portal");
        script.AppendLine("/ip hotspot profile set [find where name=[/ip hotspot get [find interface=" + hotspotInterface + "] profile]] use-radius=yes radius-accounting=yes radius-interim-update=1m login-by=http-chap,http-pap,cookie nas-port-type=wireless-802.11");
        script.AppendLine();
        script.AppendLine("# 4. Walled garden: the portal and payment pages must load before login");
        script.AppendLine($"/ip hotspot walled-garden add dst-host={settings.PortalHost} comment=kibo-portal");
        script.AppendLine($"/ip hotspot walled-garden ip add action=accept dst-host={settings.PortalHost} protocol=tcp dst-port={settings.PortalPort} comment=kibo-managed");
        script.AppendLine();
        script.AppendLine("# 5. PPPoE accounting through RADIUS (only if you sell PPPoE on this router)");
        script.AppendLine("/ppp aaa set use-radius=yes accounting=yes interim-update=1m");
        script.AppendLine();
        script.AppendLine("# 6. Upload login.html (download it from Kibo) to the hotspot directory, replacing the default one.");
        script.AppendLine($"#    It redirects customers to {settings.PortalBaseUrl}/p/{site.Slug}");
        return script.ToString();
    }

    public static string LoginPage(Site site, string portalBaseUrl)
    {
        var target = $"{portalBaseUrl.TrimEnd('/')}/p/{site.Slug}" +
                     "?mac=$(mac-esc)&ip=$(ip)&login=$(link-login-only-esc)&dst=$(link-orig-esc)" +
                     "&chapid=$(chap-id)&chapch=$(chap-challenge)&error=$(error-esc)";
        return $$"""
            <!doctype html>
            <html>
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width,initial-scale=1">
            <meta http-equiv="refresh" content="0; url={{target}}">
            <title>{{site.PortalTitle}}</title>
            </head>
            <body style="font-family:system-ui,sans-serif;text-align:center;padding:48px 16px">
            <p>Opening {{site.PortalTitle}}…</p>
            <p><a href="{{target}}">Tap here if nothing happens</a></p>
            </body>
            </html>
            """;
    }
}
