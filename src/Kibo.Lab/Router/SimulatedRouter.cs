using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Kibo.Lab.Radius;
using Microsoft.Extensions.Options;

namespace Kibo.Lab.Router;

public enum Activity
{
    Idle,
    Browsing,
    Streaming
}

public class SimDevice
{
    public required string Mac { get; init; }
    public required string Ip { get; init; }
    public required string Name { get; init; }
    public byte ChapId { get; set; }
    public byte[] ChapChallenge { get; set; } = [];
    public Activity Activity { get; set; } = Activity.Browsing;
    public string? LastMessage { get; set; }
}

public class HotspotSession
{
    public required string Id { get; init; }
    public required string User { get; init; }
    public required SimDevice Device { get; init; }
    public DateTime StartedAt { get; init; }
    public long? SessionTimeoutSeconds { get; init; }
    public long? TotalLimitBytes { get; init; }
    public long UploadBitsPerSecond { get; init; }
    public long DownloadBitsPerSecond { get; init; }
    public int InterimSeconds { get; init; } = 60;
    public byte[]? Class { get; init; }
    public long BytesIn { get; set; }
    public long BytesOut { get; set; }
    public DateTime LastInterim { get; set; }
    public string RateLimit { get; init; } = "";
    public bool IsPppoe { get; init; }
    public string? FramedIp { get; init; }

    public long UptimeSeconds(DateTime now) => (long)(now - StartedAt).TotalSeconds;
}

public record LoginResult(bool Success, string Message, HotspotSession? Session);

public class SimulatedRouter(RadiusClient radius, IOptions<LabOptions> options, ILogger<SimulatedRouter> logger)
{
    private readonly ConcurrentDictionary<string, SimDevice> devices = new();
    private readonly ConcurrentDictionary<string, HotspotSession> sessions = new();
    private int sessionCounter = RandomNumberGenerator.GetInt32(1000, 9000);
    private int ipCounter = 10;

    public DateTime BootedAt { get; private set; } = DateTime.UtcNow;
    public long UplinkRxBytes { get; private set; }
    public long UplinkTxBytes { get; private set; }
    public long LastRxBitsPerSecond { get; private set; }
    public long LastTxBitsPerSecond { get; private set; }
    public bool RadiusReachable { get; private set; } = true;
    public string LastRadiusError { get; private set; } = "";

    public IReadOnlyCollection<SimDevice> Devices => devices.Values.OrderBy(d => d.Name).ToList();
    public IReadOnlyCollection<HotspotSession> Sessions => sessions.Values.OrderBy(s => s.StartedAt).ToList();
    public LabOptions Settings => options.Value;

    public SimDevice AddDevice()
    {
        var number = Interlocked.Increment(ref ipCounter);
        var mac = $"02:4B:{RandomNumberGenerator.GetInt32(256):X2}:{RandomNumberGenerator.GetInt32(256):X2}:{RandomNumberGenerator.GetInt32(256):X2}:{number:X2}";
        var device = new SimDevice { Mac = mac, Ip = $"10.5.50.{number}", Name = $"Phone {number - 10}" };
        devices[mac] = device;
        return device;
    }

    public SimDevice? FindDevice(string? mac) => mac != null && devices.TryGetValue(mac, out var d) ? d : null;

    public void IssueChallenge(SimDevice device)
    {
        device.ChapId = (byte)RandomNumberGenerator.GetInt32(256);
        device.ChapChallenge = RandomNumberGenerator.GetBytes(16);
    }

    public static string OctalEscape(byte[] bytes) => string.Concat(bytes.Select(b => "\\" + Convert.ToString(b, 8).PadLeft(3, '0')));

    public async Task<LoginResult> LoginAsync(SimDevice device, string username, string password, CancellationToken ct,
        bool pppoe = false)
    {
        if (sessions.Values.Any(s => s.Device.Mac == device.Mac && s.IsPppoe == pppoe))
        {
            return new LoginResult(false, "This device is already logged in", null);
        }

        var request = new RadiusPacket { Code = RadiusCode.AccessRequest, Authenticator = RandomNumberGenerator.GetBytes(16) };
        request.Add(RadiusAttr.UserName, username);
        var isChapHash = password.Length == 32 && password.All(Uri.IsHexDigit) && device.ChapChallenge.Length > 0;
        if (isChapHash)
        {
            request.Add(RadiusAttr.ChapPassword, [device.ChapId, .. Convert.FromHexString(password)]);
            request.Add(RadiusAttr.ChapChallenge, device.ChapChallenge);
        }
        else
        {
            request.Add(RadiusAttr.UserPassword, RadiusPacket.EncryptPassword(password, Settings.RadiusSecret, request.Authenticator));
        }

        request.Add(RadiusAttr.ServiceType, pppoe ? 2u : 1u)
            .Add(RadiusAttr.NasIpAddress, IPAddress.Parse(Settings.NasIpAddress))
            .Add(RadiusAttr.NasIdentifier, Settings.Identity)
            .Add(RadiusAttr.NasPortType, 19u)
            .Add(RadiusAttr.NasPort, (uint)(sessionCounter & 0xffff))
            .Add(RadiusAttr.CallingStationId, device.Mac)
            .Add(RadiusAttr.CalledStationId, pppoe ? "pppoe-in1" : "hotspot1");
        if (!pppoe)
        {
            request.Add(RadiusAttr.FramedIpAddress, IPAddress.Parse(device.Ip));
        }

        RadiusPacket response;
        try
        {
            response = await radius.AuthenticateAsync(request, ct);
            RadiusReachable = true;
        }
        catch (RadiusTimeoutException ex)
        {
            RadiusReachable = false;
            LastRadiusError = ex.Message;
            return new LoginResult(false, "RADIUS server is not responding", null);
        }

        if (response.Code != RadiusCode.AccessAccept)
        {
            var reason = response.Find(RadiusAttr.ReplyMessage)?.AsString() ?? "invalid username or password";
            return new LoginResult(false, reason, null);
        }

        var rate = response.FindVendor(RadiusAttr.MikrotikVendorId, RadiusAttr.MikrotikRateLimit)?.AsString() ?? "";
        var (up, down) = ParseRate(rate);
        var totalLow = response.FindVendor(RadiusAttr.MikrotikVendorId, RadiusAttr.MikrotikTotalLimit)?.AsUInt();
        var totalHigh = response.FindVendor(RadiusAttr.MikrotikVendorId, RadiusAttr.MikrotikTotalLimitGigawords)?.AsUInt() ?? 0;

        var session = new HotspotSession
        {
            Id = $"*{Interlocked.Increment(ref sessionCounter):X}",
            User = username,
            Device = device,
            StartedAt = DateTime.UtcNow,
            SessionTimeoutSeconds = response.Find(RadiusAttr.SessionTimeout)?.AsUInt(),
            TotalLimitBytes = totalLow == null ? null : ((long)totalHigh << 32) + totalLow.Value,
            UploadBitsPerSecond = up,
            DownloadBitsPerSecond = down,
            InterimSeconds = (int)(response.Find(RadiusAttr.AcctInterimInterval)?.AsUInt() ?? 60),
            Class = response.Find(RadiusAttr.Class)?.Value,
            RateLimit = rate,
            IsPppoe = pppoe,
            FramedIp = response.Find(RadiusAttr.FramedIpAddress) is { } framed ? new IPAddress(framed.Value).ToString() : null,
            LastInterim = DateTime.UtcNow
        };
        sessions[session.Id] = session;
        await SendAccountingAsync(session, 1, null, ct);
        logger.LogInformation("Hotspot login {User} from {Mac}, timeout {Timeout}s, limit {Limit}, rate {Rate}",
            username, device.Mac, session.SessionTimeoutSeconds, session.TotalLimitBytes, rate);
        return new LoginResult(true, "Logged in", session);
    }

    public async Task<bool> RemoveSessionAsync(string id, string cause, CancellationToken ct)
    {
        if (!sessions.TryRemove(id, out var session))
        {
            return false;
        }

        session.Device.LastMessage = $"Disconnected: {cause}";
        await SendAccountingAsync(session, 2, cause, ct);
        return true;
    }

    public async Task<int> RemoveUserAsync(string username, string cause, CancellationToken ct)
    {
        var removed = 0;
        foreach (var session in sessions.Values.Where(s => s.User == username).ToList())
        {
            if (await RemoveSessionAsync(session.Id, cause, ct))
            {
                removed++;
            }
        }

        return removed;
    }

    public async Task RebootAsync(CancellationToken ct)
    {
        foreach (var session in sessions.Values)
        {
            session.Device.LastMessage = "Router restarted";
        }

        sessions.Clear();
        BootedAt = DateTime.UtcNow;
        UplinkRxBytes = 0;
        UplinkTxBytes = 0;
        await SendAccountingOnAsync(ct);
    }

    public async Task SendAccountingOnAsync(CancellationToken ct)
    {
        var request = new RadiusPacket { Code = RadiusCode.AccountingRequest };
        request.Add(RadiusAttr.AcctStatusType, 7u)
            .Add(RadiusAttr.NasIpAddress, IPAddress.Parse(Settings.NasIpAddress))
            .Add(RadiusAttr.NasIdentifier, Settings.Identity)
            .Add(RadiusAttr.AcctSessionId, "0")
            .Add(RadiusAttr.EventTimestamp, (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        try
        {
            await radius.AccountAsync(request, ct);
            RadiusReachable = true;
        }
        catch (RadiusTimeoutException ex)
        {
            RadiusReachable = false;
            LastRadiusError = ex.Message;
        }
    }

    public async Task TickAsync(TimeSpan elapsed, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        long rxThisTick = 0, txThisTick = 0;
        foreach (var session in sessions.Values.ToList())
        {
            var share = session.Device.Activity switch
            {
                Activity.Idle => 0.01,
                Activity.Browsing => 0.25,
                _ => 0.85
            };
            var download = (long)(session.DownloadBitsPerSecond / 8.0 * share * elapsed.TotalSeconds * (0.85 + Random.Shared.NextDouble() * 0.3));
            var upload = (long)(session.UploadBitsPerSecond / 8.0 * share * 0.2 * elapsed.TotalSeconds);
            session.BytesOut += download;
            session.BytesIn += upload;
            rxThisTick += upload;
            txThisTick += download;

            if (session.TotalLimitBytes != null && session.BytesIn + session.BytesOut >= session.TotalLimitBytes)
            {
                await RemoveSessionAsync(session.Id, "Session-Timeout", ct);
                session.Device.LastMessage = "Traffic limit reached";
                continue;
            }

            if (session.SessionTimeoutSeconds != null && session.UptimeSeconds(now) >= session.SessionTimeoutSeconds)
            {
                await RemoveSessionAsync(session.Id, "Session-Timeout", ct);
                session.Device.LastMessage = "Session timeout reached";
                continue;
            }

            if ((now - session.LastInterim).TotalSeconds >= session.InterimSeconds)
            {
                session.LastInterim = now;
                await SendAccountingAsync(session, 3, null, ct);
            }
        }

        UplinkRxBytes += txThisTick + 2_000;
        UplinkTxBytes += rxThisTick + 1_000;
        LastTxBitsPerSecond = (long)(txThisTick * 8 / Math.Max(elapsed.TotalSeconds, 0.1));
        LastRxBitsPerSecond = (long)(rxThisTick * 8 / Math.Max(elapsed.TotalSeconds, 0.1));
    }

    private async Task SendAccountingAsync(HotspotSession session, uint statusType, string? cause, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var request = new RadiusPacket { Code = RadiusCode.AccountingRequest };
        request.Add(RadiusAttr.AcctStatusType, statusType)
            .Add(RadiusAttr.UserName, session.User)
            .Add(RadiusAttr.AcctSessionId, $"{BootedAt.Ticks:x}-{session.Id.TrimStart('*')}")
            .Add(RadiusAttr.NasIpAddress, IPAddress.Parse(Settings.NasIpAddress))
            .Add(RadiusAttr.NasIdentifier, Settings.Identity)
            .Add(RadiusAttr.NasPortType, 19u)
            .Add(RadiusAttr.CallingStationId, session.Device.Mac)
            .Add(RadiusAttr.CalledStationId, session.IsPppoe ? "pppoe-in1" : "hotspot1")
            .Add(RadiusAttr.ServiceType, session.IsPppoe ? 2u : 1u)
            .Add(RadiusAttr.FramedIpAddress, IPAddress.Parse(session.FramedIp ?? session.Device.Ip))
            .Add(RadiusAttr.EventTimestamp, (uint)new DateTimeOffset(now).ToUnixTimeSeconds());

        if (session.Class != null)
        {
            request.Add(RadiusAttr.Class, session.Class);
        }

        if (statusType != 1)
        {
            request.Add(RadiusAttr.AcctSessionTime, (uint)session.UptimeSeconds(now))
                .Add(RadiusAttr.AcctInputOctets, (uint)(session.BytesIn & 0xffffffff))
                .Add(RadiusAttr.AcctOutputOctets, (uint)(session.BytesOut & 0xffffffff))
                .Add(RadiusAttr.AcctInputGigawords, (uint)(session.BytesIn >> 32))
                .Add(RadiusAttr.AcctOutputGigawords, (uint)(session.BytesOut >> 32));
        }

        if (cause != null)
        {
            request.Add(RadiusAttr.AcctTerminateCause, TerminateCause(cause));
        }

        try
        {
            await radius.AccountAsync(request, ct);
            RadiusReachable = true;
        }
        catch (RadiusTimeoutException ex)
        {
            RadiusReachable = false;
            LastRadiusError = ex.Message;
            logger.LogWarning("Accounting for {User} lost: {Error}", session.User, ex.Message);
        }
    }

    private static uint TerminateCause(string cause) => cause switch
    {
        "User-Request" => 1,
        "Idle-Timeout" => 4,
        "Session-Timeout" => 5,
        "Admin-Reset" => 6,
        "NAS-Reboot" => 11,
        _ => 6
    };

    public static (long UpBps, long DownBps) ParseRate(string rate)
    {
        if (string.IsNullOrWhiteSpace(rate))
        {
            return (2_000_000, 5_000_000);
        }

        var parts = rate.Split(' ')[0].Split('/');
        return (ParseBits(parts[0]), ParseBits(parts.Length > 1 ? parts[1] : parts[0]));
    }

    private static long ParseBits(string value)
    {
        value = value.Trim().ToLowerInvariant();
        if (value.Length == 0 || value == "0")
        {
            return 10_000_000;
        }

        var multiplier = value[^1] switch { 'k' => 1_000, 'm' => 1_000_000, 'g' => 1_000_000_000, _ => 1 };
        var number = multiplier == 1 ? value : value[..^1];
        return long.TryParse(number, out var parsed) ? parsed * multiplier : 1_000_000;
    }

    public static string Md5Hex(byte chapId, string password, byte[] challenge) =>
        Convert.ToHexStringLower(MD5.HashData([chapId, .. Encoding.UTF8.GetBytes(password), .. challenge]));
}

public class RouterTicker(SimulatedRouter router, ILogger<RouterTicker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        await router.SendAccountingOnAsync(stoppingToken);
        var last = DateTime.UtcNow;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var now = DateTime.UtcNow;
            try
            {
                await router.TickAsync(now - last, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Simulator tick failed");
            }

            last = now;
        }
    }
}
