using System.Net;
using System.Net.Sockets;
using Kibo.Lab.Radius;
using Kibo.Radius;
using Microsoft.Extensions.Options;

namespace Kibo.Lab.Router;

public class DisconnectListener(SimulatedRouter router, IOptions<LabOptions> options, ILogger<DisconnectListener> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 3799));
        while (!stoppingToken.IsCancellationRequested)
        {
            var received = await udp.ReceiveAsync(stoppingToken);
            try
            {
                var reply = await HandleAsync(received.Buffer, stoppingToken);
                if (reply != null)
                {
                    await udp.SendAsync(reply, received.RemoteEndPoint, stoppingToken);
                }
            }
            catch (FormatException ex)
            {
                logger.LogWarning("Ignored malformed CoA/Disconnect packet: {Message}", ex.Message);
            }
        }
    }

    private async Task<byte[]?> HandleAsync(byte[] raw, CancellationToken ct)
    {
        var secret = options.Value.RadiusSecret;
        var request = RadiusPacket.Decode(raw);
        if (request.Code is not (RadiusCode.DisconnectRequest or RadiusCode.CoaRequest))
        {
            return null;
        }

        if (!RadiusPacket.VerifyRequestWithZeroAuthenticator(raw, secret))
        {
            logger.LogWarning("Dropped Disconnect/CoA request with a bad authenticator");
            return null;
        }

        var framed = request.Find(RadiusAttr.FramedIpAddress);
        var framedIp = framed == null ? null : new IPAddress(framed.Value).ToString();
        var username = framedIp == null ? null : router.UserAtAddress(framedIp);
        var response = new RadiusPacket { Identifier = request.Identifier };
        var rate = request.FindVendor(RadiusAttr.MikrotikVendorId, RadiusAttr.MikrotikRateLimit)?.AsString();
        if (request.Code == RadiusCode.CoaRequest)
        {
            response.Code = username != null && rate != null && router.ChangeRate(username, rate) > 0
                ? RadiusCode.CoaAck
                : RadiusCode.CoaNak;
        }
        else if (username != null && await router.RemoveUserAsync(username, "Admin-Reset", ct) > 0)
        {
            response.Code = RadiusCode.DisconnectAck;
        }
        else
        {
            response.Code = RadiusCode.DisconnectNak;
            response.Add(101, framedIp == null ? 406u : 503u);
        }

        return response.EncodeResponse(request.Authenticator, secret);
    }
}
