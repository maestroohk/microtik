using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Kibo.Radius;
using Microsoft.Extensions.Options;

namespace Kibo.Lab.Radius;

public class RadiusTimeoutException(string message) : Exception(message);

public class RadiusClient(IOptions<LabOptions> options, ILogger<RadiusClient> logger)
{
    private int nextIdentifier = RandomNumberGenerator.GetInt32(256);
    private const int Attempts = 3;
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(3);

    private LabOptions Settings => options.Value;

    public Task<RadiusPacket> AuthenticateAsync(RadiusPacket request, CancellationToken ct) =>
        SendAsync(request, 1812, isAccounting: false, ct);

    public Task<RadiusPacket> AccountAsync(RadiusPacket request, CancellationToken ct) =>
        SendAsync(request, 1813, isAccounting: true, ct);

    public byte NewIdentifier() => (byte)(Interlocked.Increment(ref nextIdentifier) & 0xff);

    private async Task<RadiusPacket> SendAsync(RadiusPacket request, int port, bool isAccounting, CancellationToken ct)
    {
        request.Identifier = NewIdentifier();
        if (isAccounting)
        {
            request.SignRequestAuthenticator(Settings.RadiusSecret);
        }
        else
        {
            request.SignMessageAuthenticator(Settings.RadiusSecret);
        }

        var raw = request.Encode();
        var server = new IPEndPoint(IPAddress.Parse(Settings.RadiusServer), port);
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));

        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            await udp.SendAsync(raw, server, ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(AttemptTimeout);
            try
            {
                while (true)
                {
                    var result = await udp.ReceiveAsync(timeout.Token);
                    var response = RadiusPacket.Decode(result.Buffer);
                    if (response.Identifier != request.Identifier)
                    {
                        continue;
                    }

                    if (!response.VerifyResponse(result.Buffer, request.Authenticator, Settings.RadiusSecret))
                    {
                        logger.LogWarning("Dropped RADIUS response with an invalid authenticator");
                        continue;
                    }

                    return response;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                logger.LogInformation("RADIUS {Kind} attempt {Attempt} timed out", isAccounting ? "accounting" : "auth", attempt);
            }
        }

        throw new RadiusTimeoutException("RADIUS server is not responding");
    }
}
