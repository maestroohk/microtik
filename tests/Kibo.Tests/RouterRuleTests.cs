using System.Text.Json.Nodes;
using Kibo.Web.Features.Routers;

namespace Kibo.Tests;

public class RouterRuleTests
{
    private static JsonObject Rule(string id, string protocol, string action, string? limit = null, bool disabled = false)
    {
        var rule = new JsonObject
        {
            [".id"] = id, ["chain"] = "pre-hs-input", ["protocol"] = protocol, ["dst-port"] = "64872", ["hotspot"] = "!auth",
            ["action"] = action, ["disabled"] = disabled ? "true" : "false"
        };
        if (limit != null)
        {
            rule["dst-limit"] = limit;
        }

        return rule;
    }

    [Fact]
    public void A_router_without_dns_limits_gets_the_allowance_before_the_drops()
    {
        var changes = RouterProvisioningService.DnsLimitChanges([]);

        Assert.Equal(3, changes.Count);
        Assert.Equal(["return", "drop", "drop"], changes.Select(c => c.Values["action"]));
        Assert.Equal(["udp", "udp", "tcp"], changes.Select(c => c.Values["protocol"]));
        Assert.All(changes, c => Assert.Equal("!auth", c.Values["hotspot"]));
        Assert.Equal(RouterProvisioningService.UnauthenticatedDnsLimit, changes[0].Values["dst-limit"]);
        Assert.False(changes[0].Values.ContainsKey("place-before"));
    }

    [Fact]
    public void Rules_as_routeros_reports_them_need_no_changes()
    {
        List<JsonObject> filters = [Rule("*F", "udp", "return", "5,30,src-address/1m"), Rule("*10", "udp", "drop"), Rule("*11", "tcp", "drop")];

        Assert.Empty(RouterProvisioningService.DnsLimitChanges(filters));
        Assert.True(RouterProvisioningService.LimitsDnsBeforeLogin(filters));
    }

    [Fact]
    public void A_missing_allowance_is_placed_before_existing_drops_so_the_portal_still_resolves()
    {
        List<JsonObject> filters = [Rule("*10", "udp", "drop"), Rule("*11", "tcp", "drop")];

        var change = Assert.Single(RouterProvisioningService.DnsLimitChanges(filters));

        Assert.Equal("return", change.Values["action"]);
        Assert.Equal("*10", change.Values["place-before"]);
    }

    [Fact]
    public void Disabled_or_unlimited_rules_do_not_count()
    {
        List<JsonObject> filters = [Rule("*F", "udp", "return"), Rule("*10", "udp", "drop", disabled: true), Rule("*11", "tcp", "drop")];

        Assert.False(RouterProvisioningService.LimitsDnsBeforeLogin(filters));
        Assert.Equal(2, RouterProvisioningService.DnsLimitChanges(filters).Count);
    }

    [Theory]
    [InlineData("*", true)]
    [InlineData("*.*", true)]
    [InlineData("*.com", true)]
    [InlineData("*com", true)]
    [InlineData("*.co.ug", false)]
    [InlineData("*.mtn.co.ug", false)]
    [InlineData("portal.example.com", false)]
    public void Walled_garden_wildcards_that_open_a_whole_domain_are_flagged(string host, bool broad) =>
        Assert.Equal(broad, RouterProvisioningService.IsBroadWalledGardenHost(host));
}
