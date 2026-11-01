using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Kibo.Radius;

public static class RadiusCode
{
    public const byte AccessRequest = 1;
    public const byte AccessAccept = 2;
    public const byte AccessReject = 3;
    public const byte AccountingRequest = 4;
    public const byte AccountingResponse = 5;
    public const byte DisconnectRequest = 40;
    public const byte DisconnectAck = 41;
    public const byte DisconnectNak = 42;
    public const byte CoaRequest = 43;
    public const byte CoaAck = 44;
    public const byte CoaNak = 45;
}

public static class RadiusAttr
{
    public const byte UserName = 1;
    public const byte UserPassword = 2;
    public const byte ChapPassword = 3;
    public const byte NasIpAddress = 4;
    public const byte NasPort = 5;
    public const byte ServiceType = 6;
    public const byte FramedIpAddress = 8;
    public const byte ReplyMessage = 18;
    public const byte Class = 25;
    public const byte VendorSpecific = 26;
    public const byte SessionTimeout = 27;
    public const byte IdleTimeout = 28;
    public const byte CalledStationId = 30;
    public const byte CallingStationId = 31;
    public const byte NasIdentifier = 32;
    public const byte AcctStatusType = 40;
    public const byte AcctDelayTime = 41;
    public const byte AcctInputOctets = 42;
    public const byte AcctOutputOctets = 43;
    public const byte AcctSessionId = 44;
    public const byte AcctSessionTime = 46;
    public const byte AcctTerminateCause = 49;
    public const byte AcctInputGigawords = 52;
    public const byte AcctOutputGigawords = 53;
    public const byte EventTimestamp = 55;
    public const byte ChapChallenge = 60;
    public const byte NasPortType = 61;
    public const byte AcctInterimInterval = 85;
    public const byte MessageAuthenticator = 80;

    public const uint MikrotikVendorId = 14988;
    public const byte MikrotikRateLimit = 8;
    public const byte MikrotikTotalLimit = 17;
    public const byte MikrotikTotalLimitGigawords = 18;
}

public record RadiusAttribute(byte Type, byte[] Value, uint VendorId = 0, byte VendorType = 0)
{
    public string AsString() => Encoding.UTF8.GetString(Value);
    public uint AsUInt() => BinaryPrimitives.ReadUInt32BigEndian(Value);
}

public class RadiusPacket
{
    public byte Code { get; set; }
    public byte Identifier { get; set; }
    public byte[] Authenticator { get; set; } = new byte[16];
    public List<RadiusAttribute> Attributes { get; } = [];

    public RadiusPacket Add(byte type, byte[] value)
    {
        Attributes.Add(new RadiusAttribute(type, value));
        return this;
    }

    public RadiusPacket Add(byte type, string value) => Add(type, Encoding.UTF8.GetBytes(value));

    public RadiusPacket Add(byte type, uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return Add(type, bytes);
    }

    public RadiusPacket Add(byte type, IPAddress address) => Add(type, address.GetAddressBytes());

    public RadiusPacket AddVendor(uint vendorId, byte vendorType, string value)
    {
        var data = Encoding.UTF8.GetBytes(value);
        var payload = new byte[6 + data.Length];
        BinaryPrimitives.WriteUInt32BigEndian(payload, vendorId);
        payload[4] = vendorType;
        payload[5] = (byte)(data.Length + 2);
        data.CopyTo(payload, 6);
        return Add(RadiusAttr.VendorSpecific, payload);
    }

    public RadiusAttribute? Find(byte type) => Attributes.FirstOrDefault(a => a.Type == type && a.VendorId == 0);

    public RadiusAttribute? FindVendor(uint vendorId, byte vendorType) =>
        Attributes.FirstOrDefault(a => a.VendorId == vendorId && a.VendorType == vendorType);

    public byte[] Encode()
    {
        using var body = new MemoryStream();
        foreach (var attribute in Attributes)
        {
            body.WriteByte(attribute.Type);
            body.WriteByte((byte)(attribute.Value.Length + 2));
            body.Write(attribute.Value);
        }

        var attributes = body.ToArray();
        var packet = new byte[20 + attributes.Length];
        packet[0] = Code;
        packet[1] = Identifier;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)packet.Length);
        Authenticator.CopyTo(packet, 4);
        attributes.CopyTo(packet, 20);
        return packet;
    }

    public static RadiusPacket Decode(byte[] data)
    {
        if (data.Length < 20)
        {
            throw new FormatException("RADIUS packet too short.");
        }

        var length = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2));
        var packet = new RadiusPacket
        {
            Code = data[0],
            Identifier = data[1],
            Authenticator = data[4..20]
        };

        var offset = 20;
        while (offset + 2 <= length)
        {
            var type = data[offset];
            var attributeLength = data[offset + 1];
            if (attributeLength < 2 || offset + attributeLength > length)
            {
                throw new FormatException("Malformed RADIUS attribute.");
            }

            var value = data[(offset + 2)..(offset + attributeLength)];
            if (type == RadiusAttr.VendorSpecific && value.Length >= 6)
            {
                var vendorId = BinaryPrimitives.ReadUInt32BigEndian(value);
                var vendorOffset = 4;
                while (vendorOffset + 2 <= value.Length)
                {
                    var vendorType = value[vendorOffset];
                    var vendorLength = value[vendorOffset + 1];
                    if (vendorLength < 2 || vendorOffset + vendorLength > value.Length)
                    {
                        break;
                    }

                    packet.Attributes.Add(new RadiusAttribute(type, value[(vendorOffset + 2)..(vendorOffset + vendorLength)],
                        vendorId, vendorType));
                    vendorOffset += vendorLength;
                }
            }
            else
            {
                packet.Attributes.Add(new RadiusAttribute(type, value));
            }

            offset += attributeLength;
        }

        return packet;
    }

    public static byte[] EncryptPassword(string password, string secret, byte[] requestAuthenticator)
    {
        var plain = Encoding.UTF8.GetBytes(password);
        var padded = new byte[Math.Max(16, (plain.Length + 15) / 16 * 16)];
        plain.CopyTo(padded, 0);
        var result = new byte[padded.Length];
        var previous = requestAuthenticator;
        var secretBytes = Encoding.UTF8.GetBytes(secret);
        for (var block = 0; block < padded.Length; block += 16)
        {
            var hash = MD5.HashData([.. secretBytes, .. previous]);
            for (var i = 0; i < 16; i++)
            {
                result[block + i] = (byte)(padded[block + i] ^ hash[i]);
            }

            previous = result[block..(block + 16)];
        }

        return result;
    }

    public void SignMessageAuthenticator(string secret)
    {
        Attributes.RemoveAll(a => a.Type == RadiusAttr.MessageAuthenticator);
        Add(RadiusAttr.MessageAuthenticator, new byte[16]);
        var hmac = HMACMD5.HashData(Encoding.UTF8.GetBytes(secret), Encode());
        Attributes[^1] = new RadiusAttribute(RadiusAttr.MessageAuthenticator, hmac);
    }

    public void SignRequestAuthenticator(string secret)
    {
        Authenticator = new byte[16];
        Authenticator = MD5.HashData([.. Encode(), .. Encoding.UTF8.GetBytes(secret)]);
    }

    public bool VerifyResponse(byte[] raw, byte[] requestAuthenticator, string secret)
    {
        var copy = raw.ToArray();
        requestAuthenticator.CopyTo(copy, 4);
        var expected = MD5.HashData([.. copy, .. Encoding.UTF8.GetBytes(secret)]);
        return CryptographicOperations.FixedTimeEquals(expected, raw.AsSpan(4, 16));
    }

    public static bool VerifyRequestWithZeroAuthenticator(byte[] raw, string secret)
    {
        var copy = raw.ToArray();
        Array.Clear(copy, 4, 16);
        var expected = MD5.HashData([.. copy, .. Encoding.UTF8.GetBytes(secret)]);
        return CryptographicOperations.FixedTimeEquals(expected, raw.AsSpan(4, 16));
    }

    public byte[] EncodeResponse(byte[] requestAuthenticator, string secret)
    {
        Authenticator = requestAuthenticator;
        var raw = Encode();
        var hash = MD5.HashData([.. raw, .. Encoding.UTF8.GetBytes(secret)]);
        hash.CopyTo(raw, 4);
        return raw;
    }
}
