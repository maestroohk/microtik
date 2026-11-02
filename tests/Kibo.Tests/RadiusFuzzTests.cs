using System.Buffers.Binary;
using Kibo.Radius;

namespace Kibo.Tests;

public class RadiusFuzzTests
{
    [Fact]
    public void Random_and_mutated_packets_only_ever_fail_with_format_errors()
    {
        var random = new Random(20261102);
        var valid = new RadiusPacket { Code = RadiusCode.CoaRequest, Identifier = 1 }
            .Add(RadiusAttr.UserName, "256772000111")
            .AddVendor(RadiusAttr.MikrotikVendorId, RadiusAttr.MikrotikRateLimit, "128k/256k")
            .Encode();

        for (var i = 0; i < 20_000; i++)
        {
            byte[] packet;
            if (i % 2 == 0)
            {
                packet = new byte[random.Next(0, 80)];
                random.NextBytes(packet);
            }
            else
            {
                packet = valid.ToArray();
                for (var m = 0; m < random.Next(1, 4); m++)
                {
                    packet[random.Next(packet.Length)] = (byte)random.Next(256);
                }

                if (random.Next(4) == 0)
                {
                    packet = packet[..random.Next(packet.Length)];
                }
            }

            try
            {
                RadiusPacket.Decode(packet);
            }
            catch (FormatException)
            {
            }
        }
    }

    [Fact]
    public void Declared_length_longer_than_received_data_is_rejected()
    {
        var packet = new byte[24];
        packet[0] = RadiusCode.AccessAccept;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), 200);
        packet[20] = RadiusAttr.ReplyMessage;
        packet[21] = 4;

        Assert.Throws<FormatException>(() => RadiusPacket.Decode(packet));
    }

    [Fact]
    public void Vendor_attribute_with_bad_inner_length_is_ignored_not_read_out_of_bounds()
    {
        var packet = new RadiusPacket { Code = RadiusCode.AccessAccept }
            .Add(RadiusAttr.VendorSpecific, [0, 0, 0x3a, 0x8c, 8, 200, 1, 2])
            .Encode();

        var decoded = RadiusPacket.Decode(packet);
        Assert.Null(decoded.FindVendor(RadiusAttr.MikrotikVendorId, RadiusAttr.MikrotikRateLimit));
    }
}
