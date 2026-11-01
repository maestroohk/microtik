using System.Security.Cryptography;
using System.Text;
using Kibo.Web.Features.Portal;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;

namespace Kibo.Tests;

public class PortalTests
{
    private static PortalService CreatePortal(string environment = "Production") =>
        new(null!, new EphemeralDataProtectionProvider(), new FakeEnvironment(environment), TimeProvider.System);

    [Fact]
    public void Chap_hash_matches_mikrotik_md5_of_id_password_challenge()
    {
        var portal = CreatePortal();
        var context = new RouterContext("AA:BB:CC:DD:EE:FF", "10.5.50.2", "http://10.5.50.1/login", "http://example.com",
            "\\322", "\\370\\200\\225", null);

        var url = portal.BuildRouterLoginUrl(context, "256772000111", "123456");

        var expected = Convert.ToHexStringLower(MD5.HashData([0xD2, .. Encoding.UTF8.GetBytes("123456"), 0xF8, 0x80, 0x95]));
        Assert.NotNull(url);
        Assert.Contains($"password={expected}", url);
        Assert.DoesNotContain("123456&", url);
    }

    [Theory]
    [InlineData("http://10.5.50.1/login", true)]
    [InlineData("http://192.168.88.1/login", true)]
    [InlineData("http://hotspot.lan/login", true)]
    [InlineData("https://evil.example.com/login", false)]
    [InlineData("http://8.8.8.8/login", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData(null, false)]
    public void Router_login_url_must_point_at_a_local_hotspot(string? url, bool allowed) =>
        Assert.Equal(allowed, CreatePortal().IsAllowedLoginUrl(url));

    [Fact]
    public void Loopback_login_url_is_only_allowed_in_development()
    {
        Assert.False(CreatePortal("Production").IsAllowedLoginUrl("http://localhost:58721/login"));
        Assert.True(CreatePortal("Development").IsAllowedLoginUrl("http://localhost:58721/login"));
    }

    [Theory]
    [InlineData("abcd-2345", "ABCD2345")]
    [InlineData(" k7m3 q9tx ", "K7M3Q9TX")]
    [InlineData("abc", null)]
    public void Voucher_codes_are_normalised(string input, string? expected) =>
        Assert.Equal(expected, CreatePortal().NormalizeVoucherCode(input));

    private class FakeEnvironment(string name) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Kibo.Tests";
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
