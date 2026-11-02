using Kibo.Web.Security;

namespace Kibo.Tests;

public class TotpTests
{
    private const string RfcSecret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";

    [Theory]
    [InlineData(59, "287082")]
    [InlineData(1111111109, "081804")]
    [InlineData(2000000000, "279037")]
    public void Codes_match_rfc_6238_sha1_vectors(long unixTime, string expected) =>
        Assert.Equal(expected, Totp.Code(RfcSecret, Totp.CurrentStep(DateTimeOffset.FromUnixTimeSeconds(unixTime))));

    [Fact]
    public void A_code_cannot_be_used_twice()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1111111109);
        var code = Totp.Code(RfcSecret, Totp.CurrentStep(now));

        var step = Totp.Verify(RfcSecret, code, now, 0);
        Assert.NotNull(step);
        Assert.Null(Totp.Verify(RfcSecret, code, now, step!.Value));
    }

    [Fact]
    public void Codes_outside_the_clock_window_are_rejected()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1111111109);
        var old = Totp.Code(RfcSecret, Totp.CurrentStep(now) - 3);
        Assert.Null(Totp.Verify(RfcSecret, old, now, 0));
        Assert.Null(Totp.Verify(RfcSecret, "12345", now, 0));
    }

    [Fact]
    public void Generated_secrets_round_trip_through_base32()
    {
        var secret = Totp.NewSecret();
        Assert.Equal(20, Totp.DecodeBase32(secret).Length);
    }
}
