using System.Security.Cryptography;
using System.Text;
using Kibo.Radius;

namespace Kibo.Tests;

public class RadiusPacketTests
{
    [Fact]
    public void Coa_request_round_trips_mikrotik_rate_limit_and_verifies_with_the_shared_secret()
    {
        var request = new RadiusPacket { Code = RadiusCode.CoaRequest, Identifier = 7 };
        request.Add(RadiusAttr.UserName, "256772000111");
        request.AddVendor(RadiusAttr.MikrotikVendorId, RadiusAttr.MikrotikRateLimit, "128k/256k");
        request.SignRequestAuthenticator("s3cret");
        var raw = request.Encode();

        var decoded = RadiusPacket.Decode(raw);

        Assert.Equal(RadiusCode.CoaRequest, decoded.Code);
        Assert.Equal("256772000111", decoded.Find(RadiusAttr.UserName)!.AsString());
        Assert.Equal("128k/256k", decoded.FindVendor(RadiusAttr.MikrotikVendorId, RadiusAttr.MikrotikRateLimit)!.AsString());
        Assert.True(RadiusPacket.VerifyRequestWithZeroAuthenticator(raw, "s3cret"));
        Assert.False(RadiusPacket.VerifyRequestWithZeroAuthenticator(raw, "wrong"));
    }

    [Fact]
    public void Response_authenticator_is_bound_to_the_request()
    {
        var requestAuthenticator = RandomNumberGenerator.GetBytes(16);
        var ack = new RadiusPacket { Code = RadiusCode.CoaAck, Identifier = 9 };
        var raw = ack.EncodeResponse(requestAuthenticator, "s3cret");

        Assert.True(RadiusPacket.Decode(raw).VerifyResponse(raw, requestAuthenticator, "s3cret"));
        Assert.False(RadiusPacket.Decode(raw).VerifyResponse(raw, RandomNumberGenerator.GetBytes(16), "s3cret"));
    }

    [Fact]
    public void Pap_password_encryption_follows_rfc_2865()
    {
        var authenticator = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        var encrypted = RadiusPacket.EncryptPassword("123456", "secret", authenticator);

        var hash = MD5.HashData([.. Encoding.UTF8.GetBytes("secret"), .. authenticator]);
        var padded = new byte[16];
        Encoding.UTF8.GetBytes("123456").CopyTo(padded, 0);
        var expected = padded.Select((b, i) => (byte)(b ^ hash[i])).ToArray();

        Assert.Equal(expected, encrypted);
    }

    [Fact]
    public void Malformed_packets_are_rejected()
    {
        Assert.Throws<FormatException>(() => RadiusPacket.Decode(new byte[10]));
        var bad = new byte[22];
        bad[3] = 22;
        bad[20] = 1;
        bad[21] = 40;
        Assert.Throws<FormatException>(() => RadiusPacket.Decode(bad));
    }
}
