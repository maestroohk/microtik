using System.Diagnostics;
using Kibo.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Kibo.Tests;

[Collection("database")]
public class AccountingTests(TestDatabase database)
{
    private const long Gigaword = 4294967296;

    private record Packet(string Status, string SessionId, string User, string Nas, long SessionTime = 0, long InOctets = 0,
        long InGigawords = 0, long OutOctets = 0, long OutGigawords = 0, string Class = "", long EventEpoch = 0,
        string Cause = "", string Calling = "AA:BB:CC:00:00:01");

    private async Task SendAsync(Packet p)
    {
        await using var db = database.System();
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT radius.account(@s, @sid, @uid, @u, @nas, 'port', @st, @io, @ig, @oo, @og, 'hotspot1', @calling, @cause, '', @class, @ev)",
            connection);
        command.Parameters.AddWithValue("s", p.Status);
        command.Parameters.AddWithValue("sid", p.SessionId);
        command.Parameters.AddWithValue("uid", $"{p.Nas}-{p.SessionId}-{p.User}");
        command.Parameters.AddWithValue("u", p.User);
        command.Parameters.AddWithValue("nas", p.Nas);
        command.Parameters.AddWithValue("st", p.SessionTime);
        command.Parameters.AddWithValue("io", p.InOctets);
        command.Parameters.AddWithValue("ig", p.InGigawords);
        command.Parameters.AddWithValue("oo", p.OutOctets);
        command.Parameters.AddWithValue("og", p.OutGigawords);
        command.Parameters.AddWithValue("calling", p.Calling);
        command.Parameters.AddWithValue("cause", p.Cause);
        command.Parameters.AddWithValue("class", p.Class);
        command.Parameters.AddWithValue("ev", p.EventEpoch);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<List<RadiusAccounting>> SessionsAsync(string user)
    {
        await using var db = database.System();
        return await db.RadiusAccounting.AsNoTracking().Where(a => a.UserName == user).OrderBy(a => a.RadAcctId).ToListAsync();
    }

    private static long Bytes(RadiusAccounting a) => (a.AcctInputOctets ?? 0) + (a.AcctOutputOctets ?? 0);

    [Fact]
    public async Task Duplicate_and_reordered_interim_updates_never_double_count_or_reduce_usage()
    {
        await database.CreateTenantAsync("AcctA", "10.98.0.1");
        await SendAsync(new Packet("Start", "s1", "dup", "10.98.0.1"));
        await SendAsync(new Packet("Interim-Update", "s1", "dup", "10.98.0.1", 60, 1000, 0, 5000));
        await SendAsync(new Packet("Interim-Update", "s1", "dup", "10.98.0.1", 120, 2000, 0, 9000));
        await SendAsync(new Packet("Interim-Update", "s1", "dup", "10.98.0.1", 120, 2000, 0, 9000));
        await SendAsync(new Packet("Interim-Update", "s1", "dup", "10.98.0.1", 60, 1000, 0, 5000));
        await SendAsync(new Packet("Start", "s1", "dup", "10.98.0.1"));

        var sessions = await SessionsAsync("dup");
        Assert.Single(sessions);
        Assert.Equal(11000, Bytes(sessions[0]));
        Assert.Equal(2000, sessions[0].AcctInputOctets);
        Assert.Equal(9000, sessions[0].AcctOutputOctets);
    }

    [Fact]
    public async Task A_delayed_old_interim_is_not_mistaken_for_a_new_session()
    {
        await database.CreateTenantAsync("AcctM", "10.98.0.14");
        var routerClock = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await SendAsync(new Packet("Interim-Update", "late1", "late", "10.98.0.14", 600, 5000, 0, 9000, EventEpoch: routerClock));
        await SendAsync(new Packet("Interim-Update", "late1", "late", "10.98.0.14", 60, 100, 0, 200, EventEpoch: routerClock - 540));
        await SendAsync(new Packet("Interim-Update", "late1", "late", "10.98.0.14", 60, 100, 0, 200));

        var session = (await SessionsAsync("late")).Single();
        Assert.Equal(14000, Bytes(session));
    }

    [Fact]
    public async Task Gigawords_are_combined_into_64_bit_totals()
    {
        await database.CreateTenantAsync("AcctB", "10.98.0.2");
        await SendAsync(new Packet("Start", "g1", "giga", "10.98.0.2"));
        await SendAsync(new Packet("Interim-Update", "g1", "giga", "10.98.0.2", 600, 7, 1, 100, 3));

        var session = (await SessionsAsync("giga")).Single();
        Assert.Equal(Gigaword + 7, session.AcctInputOctets);
        Assert.Equal(3 * Gigaword + 100, session.AcctOutputOctets);
    }

    [Fact]
    public async Task Counter_wrap_without_gigawords_is_detected()
    {
        await database.CreateTenantAsync("AcctC", "10.98.0.3");
        await SendAsync(new Packet("Interim-Update", "w1", "wrap", "10.98.0.3", 600, 4_000_000_000, 0, 0));
        await SendAsync(new Packet("Interim-Update", "w1", "wrap", "10.98.0.3", 660, 50_000_000, 0, 0));

        var session = (await SessionsAsync("wrap")).Single();
        Assert.Equal(Gigaword + 50_000_000, session.AcctInputOctets);
    }

    [Fact]
    public async Task Missing_start_creates_the_session_from_the_first_interim()
    {
        await database.CreateTenantAsync("AcctD", "10.98.0.4");
        await SendAsync(new Packet("Interim-Update", "m1", "nostart", "10.98.0.4", 300, 10, 0, 20));
        await SendAsync(new Packet("Stop", "m1", "nostart", "10.98.0.4", 360, 15, 0, 30, Cause: "User-Request"));

        var session = (await SessionsAsync("nostart")).Single();
        Assert.Equal(45, Bytes(session));
        Assert.NotNull(session.AcctStopTime);
        Assert.InRange((DateTime.UtcNow - session.AcctStartTime!.Value).TotalSeconds, 290, 320);
    }

    [Fact]
    public async Task Router_clock_is_not_trusted_for_session_times()
    {
        await database.CreateTenantAsync("AcctE", "10.98.0.5");
        var tenYearsAgo = DateTimeOffset.UtcNow.AddYears(-10).ToUnixTimeSeconds();
        await SendAsync(new Packet("Start", "c1", "clock", "10.98.0.5", EventEpoch: tenYearsAgo));

        var session = (await SessionsAsync("clock")).Single();
        Assert.True((DateTime.UtcNow - session.AcctStartTime!.Value).TotalMinutes < 1);
        Assert.True(session.NasEventTime < DateTime.UtcNow.AddYears(-9));
    }

    [Fact]
    public async Task Reused_session_id_after_reboot_creates_a_new_session_instead_of_merging()
    {
        await database.CreateTenantAsync("AcctF", "10.98.0.6");
        var routerClock = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await SendAsync(new Packet("Start", "81a00001", "reuse", "10.98.0.6", EventEpoch: routerClock - 3600));
        await SendAsync(new Packet("Interim-Update", "81a00001", "reuse", "10.98.0.6", 3600, 1_000_000, 0, 9_000_000, EventEpoch: routerClock - 60));
        await SendAsync(new Packet("Interim-Update", "81a00001", "reuse", "10.98.0.6", 30, 500, 0, 700, EventEpoch: routerClock));

        var sessions = await SessionsAsync("reuse");
        Assert.Equal(2, sessions.Count);
        Assert.Equal("Session-Id-Reused", sessions[0].AcctTerminateCause);
        Assert.Equal(10_000_000, Bytes(sessions[0]));
        Assert.Equal(1200, Bytes(sessions[1]));
        Assert.Null(sessions[1].AcctStopTime);
    }

    [Fact]
    public async Task Accounting_on_closes_open_sessions_and_a_stale_session_reopens_when_updates_resume()
    {
        await database.CreateTenantAsync("AcctG", "10.98.0.7");
        await SendAsync(new Packet("Start", "st1", "stale", "10.98.0.7"));
        await using (var db = database.System())
        {
            await db.RadiusAccounting.Where(a => a.UserName == "stale")
                .ExecuteUpdateAsync(x => x.SetProperty(a => a.AcctStopTime, DateTime.UtcNow).SetProperty(a => a.AcctTerminateCause, "Stale-Session"));
        }

        await SendAsync(new Packet("Interim-Update", "st1", "stale", "10.98.0.7", 400, 10, 0, 10));
        var resumed = (await SessionsAsync("stale")).Single();
        Assert.Null(resumed.AcctStopTime);

        await SendAsync(new Packet("Accounting-On", "", "", "10.98.0.7"));
        Assert.Equal("NAS-Reboot", (await SessionsAsync("stale")).Single().AcctTerminateCause);
    }

    [Fact]
    public async Task Class_from_another_tenant_is_ignored_and_the_owning_subscription_is_used()
    {
        var (a, _, _) = await database.CreateTenantAsync("AcctH", "10.98.0.8");
        var (b, _, _) = await database.CreateTenantAsync("AcctI", "10.98.0.9");
        var foreignSubscription = await CreateSubscriptionAsync(b.Id, "victim");
        var ownSubscription = await CreateSubscriptionAsync(a.Id, "attacker");

        await SendAsync(new Packet("Start", "x1", "attacker", "10.98.0.8", Class: foreignSubscription.ToString()));

        var session = (await SessionsAsync("attacker")).Single();
        Assert.Equal(a.Id, session.TenantId);
        Assert.Equal(ownSubscription, session.SubscriptionId);
    }

    [Fact]
    public async Task Unknown_nas_is_recorded_as_evidence_but_creates_no_session()
    {
        await SendAsync(new Packet("Start", "u1", "ghost", "10.250.250.1"));

        Assert.Empty(await SessionsAsync("ghost"));
        await using var db = database.System();
        Assert.Contains(await db.RadiusAccountingEvents.Where(e => e.UserName == "ghost").Select(e => e.Outcome).ToListAsync(),
            o => o.Contains("unknown NAS"));
    }

    [Fact]
    public async Task Parallel_retransmissions_of_the_same_packets_produce_one_session()
    {
        await database.CreateTenantAsync("AcctJ", "10.98.0.10");
        var packets = new List<Packet> { new("Start", "p1", "parallel", "10.98.0.10") };
        for (var i = 1; i <= 20; i++)
        {
            packets.Add(new Packet("Interim-Update", "p1", "parallel", "10.98.0.10", i * 10, i * 100, 0, i * 1000));
        }

        await Task.WhenAll(packets.Concat(packets).Select(SendAsync));

        var session = (await SessionsAsync("parallel")).Single();
        Assert.Equal(2000 + 20000, Bytes(session));
    }

    [Fact]
    public async Task Concurrent_sessions_share_the_remaining_hard_cap()
    {
        var (t, _, _) = await database.CreateTenantAsync("AcctK", "10.98.0.11");
        var subscription = await CreateSubscriptionAsync(t.Id, "family", dataLimit: 10_000_000, devices: 3);

        var first = await ReplyAsync("family", "10.98.0.11");
        Assert.Equal("10000000", first["Mikrotik-Total-Limit"]);
        Assert.Equal("60", first["Acct-Interim-Interval"]);

        await SendAsync(new Packet("Start", "f1", "family", "10.98.0.11", Class: subscription.ToString()));
        await SendAsync(new Packet("Interim-Update", "f1", "family", "10.98.0.11", 1, 1_000_000, 0, 1_000_000,
            Class: subscription.ToString()));

        var second = await ReplyAsync("family", "10.98.0.11");
        var granted = long.Parse(second["Mikrotik-Total-Limit"]);
        Assert.InRange(granted, 1, 4_000_000);
    }

    [Fact]
    public async Task Accepted_login_without_accounting_counts_toward_the_device_limit()
    {
        await database.CreateTenantAsync("AcctL", "10.98.0.12");
        await using (var db = database.System())
        {
            db.RadiusPostAuth.Add(new RadiusPostAuth
            {
                UserName = "racer", Reply = "Access-Accept", NasIpAddress = "10.98.0.12", CallingStationId = "AA", AuthDate = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        Assert.Equal(1, await ScalarAsync("SELECT radius.simultaneous_sessions('racer', '10.98.0.12')"));
    }

    [Fact]
    public async Task Ten_thousand_interim_updates_for_two_hundred_sessions_stay_exact()
    {
        await database.CreateTenantAsync("AcctLoad", "10.98.0.13");
        const int sessions = 200;
        const int updates = 50;
        var stopwatch = Stopwatch.StartNew();
        await Parallel.ForEachAsync(Enumerable.Range(0, sessions), new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (s, _) =>
        {
            for (var u = 1; u <= updates; u++)
            {
                await SendAsync(new Packet("Interim-Update", $"load{s}", $"load{s}", "10.98.0.13", u * 60, u * 1000L, 0, u * 3000L));
            }
        });
        stopwatch.Stop();

        await using var db = database.System();
        var total = await db.RadiusAccounting.Where(a => a.NasIpAddress == "10.98.0.13")
            .SumAsync(a => (a.AcctInputOctets ?? 0) + (a.AcctOutputOctets ?? 0));
        Assert.Equal(sessions * (updates * 1000L + updates * 3000L), total);
        Assert.Equal(sessions, await db.RadiusAccounting.CountAsync(a => a.NasIpAddress == "10.98.0.13"));
        Console.WriteLine($"{sessions * updates} accounting updates in {stopwatch.ElapsedMilliseconds} ms " +
                          $"({sessions * updates * 1000.0 / stopwatch.ElapsedMilliseconds:0} per second, 4 writers)");
    }

    private async Task<Guid> CreateSubscriptionAsync(Guid tenantId, string username, long? dataLimit = null, int devices = 1)
    {
        await using var db = database.System();
        var package = new Package { TenantId = tenantId, Name = "P", ValidityAmount = 1, ValidityUnit = ValidityUnit.Days, CreatedAt = DateTime.UtcNow };
        var account = new AccessAccount { TenantId = tenantId, Username = username, Password = username, CreatedAt = DateTime.UtcNow };
        var subscription = new Subscription
        {
            TenantId = tenantId, AccessAccountId = account.Id, PackageId = package.Id, PackageName = "P",
            Status = SubscriptionStatus.Active, MaxDevices = devices, DataQuotaBytes = dataLimit, PurchasedAt = DateTime.UtcNow,
            ActivatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddDays(1), CountingSince = DateTime.UtcNow.AddMinutes(-1),
            ActivateBy = DateTime.UtcNow.AddDays(1)
        };
        db.AddRange(package, account, subscription);
        db.RadiusAccess.Add(new RadiusAccess
        {
            TenantId = tenantId, Username = username, AccessAccountId = account.Id, SubscriptionId = subscription.Id,
            Password = username, Enabled = true, ExpiresAt = DateTime.UtcNow.AddDays(1), DataLimitBytes = dataLimit,
            CountingSince = DateTime.UtcNow.AddMinutes(-1), RateLimit = "1000k/2000k", SimultaneousUse = devices,
            AccountingIntervalSeconds = 0,
            TimeZone = "Africa/Kampala", UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        return subscription.Id;
    }

    private async Task<Dictionary<string, string>> ReplyAsync(string username, string nas)
    {
        await using var db = database.System();
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT attribute, value FROM radius.reply_items(@u, @n, 'Login-User')", connection);
        command.Parameters.AddWithValue("u", username);
        command.Parameters.AddWithValue("n", nas);
        var result = new Dictionary<string, string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result[reader.GetString(0)] = reader.GetString(1);
        }

        return result;
    }

    private async Task<int> ScalarAsync(string sql)
    {
        await using var db = database.System();
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }
}
