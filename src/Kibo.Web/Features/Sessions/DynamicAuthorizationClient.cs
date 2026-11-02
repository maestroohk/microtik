using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Kibo.Radius;
using Kibo.Web.Data.Entities;

namespace Kibo.Web.Features.Sessions;

public enum DynamicAuthorizationResult
{
    Acknowledged,
    Rejected,
    NoResponse
}

public class DynamicAuthorizationClient(ILogger<DynamicAuthorizationClient> logger)
{
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(3);
    private const int Attempts = 2;

    public Task<DynamicAuthorizationResult> DisconnectAsync(Router router, string username, string? framedIp, CancellationToken ct)
    {
        var request = new RadiusPacket { Code = RadiusCode.DisconnectRequest };
        AddSessionIdentity(request, username, framedIp);
        return SendAsync(router, request, RadiusCode.DisconnectAck, ct);
    }

    public Task<DynamicAuthorizationResult> ChangeRateAsync(Router router, string username, string? framedIp, string rateLimit,
        CancellationToken ct)
    {
        var request = new RadiusPacket { Code = RadiusCode.CoaRequest };
        AddSessionIdentity(request, username, framedIp);
        request.AddVendor(RadiusAttr.MikrotikVendorId, RadiusAttr.MikrotikRateLimit, rateLimit);
        return SendAsync(router, request, RadiusCode.CoaAck, ct);
    }

    private static void AddSessionIdentity(RadiusPacket request, string username, string? framedIp)
    {
        if (IPAddress.TryParse(framedIp, out var address) && address.AddressFamily == AddressFamily.InterNetwork)
        {
            request.Add(RadiusAttr.FramedIpAddress, address);
        }
        else
        {
            request.Add(RadiusAttr.UserName, username);
        }
    }

    private async Task<DynamicAuthorizationResult> SendAsync(Router router, RadiusPacket request, byte ackCode, CancellationToken ct)
    {
        request.Identifier = (byte)RandomNumberGenerator.GetInt32(256);
        request.Add(RadiusAttr.EventTimestamp, (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        request.SignRequestAuthenticator(router.RadiusSecret);
        var raw = request.Encode();

        var addresses = await Dns.GetHostAddressesAsync(router.ManagementHost, ct);
        var target = new IPEndPoint(addresses.First(a => a.AddressFamily == AddressFamily.InterNetwork), router.CoaPort);
        using var udp = new UdpClient(AddressFamily.InterNetwork);

        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            await udp.SendAsync(raw, target, ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(AttemptTimeout);
            try
            {
                while (true)
                {
                    var result = await udp.ReceiveAsync(timeout.Token);
                    var response = RadiusPacket.Decode(result.Buffer);
                    if (response.Identifier != request.Identifier ||
                        !response.VerifyResponse(result.Buffer, request.Authenticator, router.RadiusSecret))
                    {
                        continue;
                    }

                    return response.Code == ackCode ? DynamicAuthorizationResult.Acknowledged : DynamicAuthorizationResult.Rejected;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                logger.LogInformation("No CoA/Disconnect reply from {Router} (attempt {Attempt})", router.Name, attempt);
            }
            catch (SocketException ex)
            {
                logger.LogInformation("CoA/Disconnect to {Router} failed: {Message}", router.Name, ex.Message);
                return DynamicAuthorizationResult.NoResponse;
            }
        }

        return DynamicAuthorizationResult.NoResponse;
    }
}
