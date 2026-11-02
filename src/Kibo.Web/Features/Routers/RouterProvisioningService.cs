using System.Text.Json;
using System.Text.Json.Nodes;
using Kibo.Web.Data;
using Kibo.Web.Data.Entities;
using Kibo.Web.Features.Platform;
using Kibo.Web.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Kibo.Web.Features.Routers;

public record PostureFinding(string Severity, string Check, string Detail, string Fix);

public record PlannedChange(string Path, string Action, string? Id, Dictionary<string, string> Values,
    Dictionary<string, string>? Previous, string Description);

public class RouterProvisioningService(
    KiboDb kiboDb,
    CurrentUser currentUser,
    RouterOsClient client,
    RouterHealthChecker healthChecker,
    SecretProtector secrets,
    AuditLogger audit,
    IOptions<OnboardingOptions> onboarding,
    TimeProvider clock)
{
    public const string ManagedComment = "kibo-managed";

    public static string ClaimNote(Router router) => $"kibo-claim:{router.ClaimToken}";

    public async Task<OperationResult<Router>> VerifyOwnershipAsync(Guid routerId, CancellationToken ct = default)
    {
        Demand(Permissions.ManageRouters);
        await using var db = kiboDb.ForCurrentUser();
        var router = await db.Routers.FirstOrDefaultAsync(r => r.Id == routerId, ct);
        if (router == null)
        {
            return OperationResult<Router>.Fail("Router not found.");
        }

        if (router.ApiKind == RouterApiKind.Simulator)
        {
            return OperationResult<Router>.Fail("Ownership proof is only available on real RouterOS.");
        }

        var connection = healthChecker.ConnectionFor(router);
        string note;
        string hardwareId;
        try
        {
            note = (await client.ReadAsync(connection, "system/note", ct))["note"]?.ToString() ?? "";
            var license = await client.ReadAsync(connection, "system/license", ct);
            hardwareId = "swid:" + (license["software-id"]?.ToString() ?? license["system-id"]?.ToString() ?? "");
        }
        catch (RouterOsException ex)
        {
            return OperationResult<Router>.Fail($"Could not read the router: {ex.Message}");
        }

        if (!note.Contains(ClaimNote(router), StringComparison.Ordinal))
        {
            return OperationResult<Router>.Fail($"The router's system note does not contain {ClaimNote(router)}. Run the onboarding script first.");
        }

        if (hardwareId == "swid:")
        {
            return OperationResult<Router>.Fail("The router did not report a software ID.");
        }

        var taken = await db.Routers.IgnoreQueryFilters().AnyAsync(r => r.HardwareId == hardwareId && r.Id != router.Id, ct);
        if (taken)
        {
            await audit.WriteAsync("router.ownership_conflict", "Router", router.Id.ToString(), hardwareId);
            return OperationResult<Router>.Fail("This router is already registered to another account. Contact support.");
        }

        var now = clock.GetUtcNow().UtcDateTime;
        router.HardwareId = hardwareId;
        router.OwnershipVerifiedAt = now;
        if (!await db.RadiusNas.IgnoreQueryFilters().AnyAsync(n => n.RouterId == router.Id, ct))
        {
            db.RadiusNas.Add(new RadiusNas
            {
                TenantId = router.TenantId, NasName = router.NasIpAddress, ShortName = router.Name, Secret = router.RadiusSecret,
                Description = $"Kibo router {router.Id}", RouterId = router.Id
            });
        }

        db.RouterEvents.Add(new RouterEvent
        {
            TenantId = router.TenantId, RouterId = router.Id, Kind = RouterEventKind.CapabilitiesDetected,
            Message = $"Ownership proven with claim token; hardware {hardwareId}. RADIUS client enabled.", OccurredAt = now
        });
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("router.ownership_verified", "Router", router.Id.ToString(), hardwareId);
        return OperationResult<Router>.Ok(router);
    }

    public async Task<List<PostureFinding>> AuditPostureAsync(Guid routerId, CancellationToken ct = default)
    {
        Demand(Permissions.ViewRouters);
        await using var db = kiboDb.ForCurrentUser();
        var router = await db.Routers.FirstAsync(r => r.Id == routerId, ct);
        var findings = await ReadPostureAsync(router, ct);
        router.PostureJson = JsonSerializer.Serialize(findings);
        router.PostureCheckedAt = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        return findings;
    }

    private async Task<List<PostureFinding>> ReadPostureAsync(Router router, CancellationToken ct)
    {
        var findings = new List<PostureFinding>();
        var connection = healthChecker.ConnectionFor(router);
        var radiusServer = onboarding.Value.RadiusServerAddress;

        async Task<List<JsonObject>> List(string path)
        {
            try
            {
                return await client.ListAsync(connection, path, ct);
            }
            catch (RouterOsException ex) when (ex.Kind == RouterFailureKind.ApiError)
            {
                return [];
            }
        }

        static string V(JsonObject o, string key) => o[key]?.ToString() ?? "";
        static bool Enabled(JsonObject o) => V(o, "disabled") != "true";

        var usedProfiles = (await List("ip/hotspot")).Where(Enabled).Select(h => V(h, "profile")).ToHashSet();
        foreach (var profile in (await List("ip/hotspot/profile")).Where(p => usedProfiles.Contains(V(p, "name"))))
        {
            var name = V(profile, "name");
            var loginBy = V(profile, "login-by");
            if (V(profile, "use-radius") != "true")
            {
                findings.Add(new("Critical", "Hotspot profile", $"Profile {name} does not use RADIUS, so logins are not checked against billing.",
                    $"/ip hotspot profile set {name} use-radius=yes"));
            }

            if (loginBy.Contains("mac"))
            {
                findings.Add(new("High", "MAC login", $"Profile {name} allows login by MAC address, which can be spoofed.",
                    $"Remove mac from login-by on profile {name}."));
            }

            if (loginBy.Contains("trial"))
            {
                findings.Add(new("High", "Trial login", $"Profile {name} gives free trial access without payment.",
                    $"Remove trial from login-by on profile {name}."));
            }

            if (V(profile, "use-radius") == "true" && RouterOsClient.ParseDuration(V(profile, "radius-interim-update")) == 0)
            {
                findings.Add(new("Medium", "Accounting interval", $"Profile {name} has no interim accounting interval; usage would only be known at logout.",
                    $"/ip hotspot profile set {name} radius-interim-update=1m"));
            }
        }

        foreach (var user in (await List("ip/hotspot/user")).Where(u => V(u, "name") != "default-trial" && Enabled(u)))
        {
            findings.Add(new("High", "Local hotspot user", $"Local user {V(user, "name")} logs in without RADIUS or billing.",
                "Remove local hotspot users; manage access in Kibo."));
        }

        foreach (var binding in (await List("ip/hotspot/ip-binding")).Where(b => V(b, "type") == "bypassed" && Enabled(b)))
        {
            findings.Add(new("High", "Bypass binding", $"{V(binding, "mac-address")} {V(binding, "address")} bypasses the hotspot entirely.",
                "Remove the bypassed IP binding unless it is infrastructure (for example an access point)."));
        }

        foreach (var garden in (await List("ip/hotspot/walled-garden")).Where(g => V(g, "dynamic") != "true"))
        {
            var host = V(garden, "dst-host");
            var unrestricted = host is "*" or "*.*" ||
                               (host == "" && V(garden, "dst-address") == "" && V(garden, "path") == "" && V(garden, "dst-port") == "");
            if (Enabled(garden) && V(garden, "action") != "deny" && unrestricted)
            {
                findings.Add(new("Critical", "Walled garden", "A walled-garden rule allows every host before login.",
                    "Restrict walled-garden entries to the portal and payment hosts."));
            }
        }

        foreach (var garden in await List("ip/hotspot/walled-garden/ip"))
        {
            var destination = V(garden, "dst-address");
            var anyDestination = (destination == "" || destination == "0.0.0.0/0") && V(garden, "dst-host") == "";
            if (Enabled(garden) && V(garden, "dynamic") != "true" && V(garden, "action") == "accept" && anyDestination)
            {
                findings.Add(new("Critical", "Walled garden", "An IP walled-garden rule accepts traffic to any address before login.",
                    "Restrict IP walled-garden rules to specific addresses."));
            }
        }

        foreach (var radius in (await List("radius")).Where(Enabled))
        {
            if (V(radius, "service").Contains("hotspot") && V(radius, "address") != radiusServer)
            {
                findings.Add(new("High", "Extra RADIUS server", $"Hotspot also authenticates against {V(radius, "address")}.",
                    "Remove RADIUS servers that are not Kibo so no other server can grant access."));
            }
        }

        try
        {
            var incoming = await client.ReadAsync(connection, "radius/incoming", ct);
            if (V(incoming, "accept") != "true")
            {
                findings.Add(new("Medium", "CoA disabled", "The router ignores CoA and Disconnect requests, so Kibo cannot cut off or slow sessions remotely.",
                    $"/radius incoming set accept=yes port={router.CoaPort}"));
            }
        }
        catch (RouterOsException)
        {
        }

        foreach (var rule in (await List("ip/firewall/filter")).Where(r => V(r, "action") == "fasttrack-connection" && Enabled(r)))
        {
            findings.Add(new("High", "FastTrack", "A FastTrack rule is active. Fast-tracked connections skip queues, so hotspot speed limits may not apply to them.",
                "Exclude hotspot traffic from FastTrack or disable the rule; verify per-user limits afterwards."));
        }

        var hotspotInterfaces = (await List("ip/hotspot")).Select(h => V(h, "interface")).ToHashSet();
        foreach (var address in (await List("ipv6/address")).Where(a => hotspotInterfaces.Contains(V(a, "interface")) && Enabled(a)))
        {
            if (!V(address, "address").StartsWith("fe80", StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(new("High", "IPv6 bypass", $"IPv6 {V(address, "address")} on hotspot interface {V(address, "interface")}. The hotspot only controls IPv4, so clients may reach the internet over IPv6 without logging in.",
                    "Remove IPv6 addresses and router advertisements from the hotspot interface, or block forwarded IPv6 from it."));
            }
        }

        var services = (await List("ip/service"))
            .Where(s => Enabled(s) && V(s, "dynamic") != "true")
            .GroupBy(s => V(s, "name"))
            .Select(g => g.First());
        foreach (var service in services)
        {
            var name = V(service, "name");
            var allowed = service.ContainsKey("available-from") ? V(service, "available-from") : V(service, "address");
            if (name is "telnet" or "ftp")
            {
                findings.Add(new("High", "Insecure service", $"{name} is enabled and sends passwords in clear text.", $"/ip service disable {name}"));
            }
            else if (allowed == "" && name is "www" or "www-ssl" or "api" or "api-ssl" or "winbox" or "ssh")
            {
                findings.Add(new("Medium", "Management exposure", $"{name} accepts connections from any address.",
                    $"/ip service set {name} address=<management network>"));
            }
        }

        var pppAaa = await TryReadAsync(connection, "ppp/aaa", ct);
        var secretsList = (await List("ppp/secret")).Where(Enabled).ToList();
        if (secretsList.Count > 0)
        {
            findings.Add(new("High", "Local PPP secrets", $"{secretsList.Count} local PPPoE secret(s) log in without RADIUS or billing.",
                "Move PPPoE customers to Kibo and remove local secrets."));
        }

        if (pppAaa != null && V(pppAaa, "use-radius") != "true" && (await List("interface/pppoe-server/server")).Any(Enabled))
        {
            findings.Add(new("High", "PPPoE without RADIUS", "A PPPoE server is running but PPP does not use RADIUS.",
                "/ppp aaa set use-radius=yes accounting=yes interim-update=1m"));
        }

        var resource = await TryReadAsync(connection, "system/resource", ct);
        var version = resource == null ? "" : V(resource, "version");
        if (Version.TryParse(version.Split(' ')[0], out var parsed) && parsed < new Version(7, 10))
        {
            findings.Add(new("Medium", "RouterOS version", $"RouterOS {version} is older than 7.10, which Kibo has not been tested with.",
                "Upgrade to a current long-term or stable v7 release."));
        }

        return findings;
    }

    public async Task<OperationResult<ProvisioningOperation>> PlanAsync(Guid routerId, CancellationToken ct = default)
    {
        Demand(Permissions.ProvisionRouters);
        await using var db = kiboDb.ForCurrentUser();
        var router = await db.Routers.Include(r => r.Site).FirstOrDefaultAsync(r => r.Id == routerId, ct);
        if (router == null)
        {
            return OperationResult<ProvisioningOperation>.Fail("Router not found.");
        }

        if (router.OwnershipVerifiedAt == null || router.ApiKind == RouterApiKind.Simulator)
        {
            return OperationResult<ProvisioningOperation>.Fail("Prove ownership of a real RouterOS router before provisioning.");
        }

        var tenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == router.TenantId, ct);
        List<PlannedChange> changes;
        try
        {
            changes = await ComputeChangesAsync(router, tenant.PppoeEnabled, ct);
        }
        catch (RouterOsException ex)
        {
            return OperationResult<ProvisioningOperation>.Fail($"Could not read the router: {ex.Message}");
        }

        var operation = new ProvisioningOperation
        {
            TenantId = router.TenantId, RouterId = router.Id, Status = ProvisioningStatus.Planned,
            PlanJson = JsonSerializer.Serialize(changes), RequestedBy = currentUser.Email,
            Detail = changes.Count == 0 ? "Router already matches the Kibo configuration." : $"{changes.Count} change(s) proposed.",
            CreatedAt = clock.GetUtcNow().UtcDateTime
        };
        db.ProvisioningOperations.Add(operation);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("router.provisioning_planned", "Router", router.Id.ToString(), operation.Detail);
        return OperationResult<ProvisioningOperation>.Ok(operation);
    }

    public async Task<List<PlannedChange>> ComputeChangesAsync(Router router, bool pppoe, CancellationToken ct)
    {
        var connection = healthChecker.ConnectionFor(router);
        var settings = onboarding.Value;
        var changes = new List<PlannedChange>();

        var radius = (await client.ListAsync(connection, "radius", ct)).FirstOrDefault(r => r["comment"]?.ToString() == ManagedComment);
        var desiredRadius = new Dictionary<string, string>
        {
            ["address"] = settings.RadiusServerAddress, ["service"] = pppoe ? "ppp,hotspot" : "hotspot",
            ["src-address"] = router.NasIpAddress, ["timeout"] = "3s", ["comment"] = ManagedComment
        };
        if (radius == null)
        {
            changes.Add(new("radius", "add", null, new(desiredRadius) { ["secret"] = router.RadiusSecret }, null,
                $"Add RADIUS client for {settings.RadiusServerAddress}"));
        }
        else
        {
            var diff = Diff(radius, desiredRadius);
            if (diff.Count > 0)
            {
                changes.Add(new("radius", "set", radius[".id"]?.ToString(), diff, Previous(radius, diff), "Correct the Kibo RADIUS client"));
            }
        }

        var incoming = await client.ReadAsync(connection, "radius/incoming", ct);
        var desiredIncoming = new Dictionary<string, string> { ["accept"] = "true", ["port"] = router.CoaPort.ToString() };
        var incomingDiff = Diff(incoming, desiredIncoming);
        if (incomingDiff.Count > 0)
        {
            changes.Add(new("radius/incoming", "set", null, incomingDiff, Previous(incoming, incomingDiff),
                $"Accept CoA and Disconnect on UDP {router.CoaPort}"));
        }

        var servers = await client.ListAsync(connection, "ip/hotspot", ct);
        var server = servers.FirstOrDefault(s => s["interface"]?.ToString() == router.HotspotInterface) ?? servers.FirstOrDefault();
        if (server != null)
        {
            var profileName = server["profile"]?.ToString() ?? "";
            var profile = (await client.ListAsync(connection, "ip/hotspot/profile", ct)).FirstOrDefault(p => p["name"]?.ToString() == profileName);
            if (profile != null)
            {
                var loginBy = (profile["login-by"]?.ToString() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Where(m => m is not ("mac" or "trial" or "mac-cookie")).ToHashSet();
                loginBy.Add("http-chap");
                var desiredProfile = new Dictionary<string, string>
                {
                    ["use-radius"] = "true", ["radius-accounting"] = "true", ["radius-interim-update"] = "1m",
                    ["login-by"] = string.Join(",", loginBy.Order())
                };
                var profileDiff = Diff(profile, desiredProfile);
                if (profileDiff.Count > 0)
                {
                    changes.Add(new("ip/hotspot/profile", "set", profile[".id"]?.ToString(), profileDiff, Previous(profile, profileDiff),
                        $"Use RADIUS with 1-minute accounting on hotspot profile {profileName}"));
                }
            }
        }

        var portalUri = new Uri(settings.PortalBaseUrl);
        var portalHost = portalUri.Host;
        var ipGardens = await client.ListAsync(connection, "ip/hotspot/walled-garden/ip", ct);
        if (!ipGardens.Any(g => g["dst-host"]?.ToString() == portalHost && g["dst-port"]?.ToString() == portalUri.Port.ToString() &&
                                g["action"]?.ToString() == "accept"))
        {
            changes.Add(new("ip/hotspot/walled-garden/ip", "add", null,
                new()
                {
                    ["action"] = "accept", ["dst-host"] = portalHost, ["protocol"] = "tcp",
                    ["dst-port"] = portalUri.Port.ToString(), ["comment"] = ManagedComment
                }, null, $"Let customers reach the portal on TCP {portalUri.Port} before login"));
        }

        var gardens = await client.ListAsync(connection, "ip/hotspot/walled-garden", ct);
        if (!gardens.Any(g => g["dst-host"]?.ToString() == portalHost))
        {
            changes.Add(new("ip/hotspot/walled-garden", "add", null,
                new() { ["dst-host"] = portalHost, ["action"] = "allow", ["comment"] = ManagedComment }, null,
                $"Allow the Kibo portal {portalHost} before login"));
        }

        if (server != null && router.Site != null)
        {
            var profileName = server["profile"]?.ToString() ?? "";
            var profile = (await client.ListAsync(connection, "ip/hotspot/profile", ct)).FirstOrDefault(p => p["name"]?.ToString() == profileName);
            var directory = profile?["html-directory"]?.ToString();
            if (!string.IsNullOrEmpty(directory))
            {
                var fileName = $"{directory}/login.html";
                var desiredPage = RouterScripts.LoginPage(router.Site, settings.PortalBaseUrl);
                var existing = (await client.ListAsync(connection, "file", ct)).FirstOrDefault(f => f["name"]?.ToString() == fileName);
                var current = existing?["contents"]?.ToString();
                if (existing == null)
                {
                    changes.Add(new("file", "add", null, new() { ["name"] = fileName, ["contents"] = desiredPage }, null,
                        $"Install the Kibo login page as {fileName}"));
                }
                else if (Normalize(current) != Normalize(desiredPage))
                {
                    changes.Add(new("file", "set", existing[".id"]?.ToString(), new() { ["contents"] = desiredPage },
                        new() { ["contents"] = current ?? "" }, $"Replace {fileName} with the Kibo login page (the old page is kept for rollback)"));
                }
            }
        }

        var note = (await client.ReadAsync(connection, "system/note", ct))["note"]?.ToString() ?? "";
        if (!note.Contains(ClaimNote(router)))
        {
            changes.Add(new("system/note", "set", null, new() { ["note"] = (note + "\n" + ClaimNote(router)).Trim() },
                new() { ["note"] = note }, "Keep the Kibo ownership claim in the system note"));
        }

        if (pppoe)
        {
            var aaa = await client.ReadAsync(connection, "ppp/aaa", ct);
            var desiredAaa = new Dictionary<string, string> { ["use-radius"] = "true", ["accounting"] = "true", ["interim-update"] = "1m" };
            var aaaDiff = Diff(aaa, desiredAaa);
            if (aaaDiff.Count > 0)
            {
                changes.Add(new("ppp/aaa", "set", null, aaaDiff, Previous(aaa, aaaDiff), "Authenticate PPPoE through RADIUS"));
            }
        }

        return changes;
    }

    public async Task<OperationResult<ProvisioningOperation>> ApplyAsync(long operationId, CancellationToken ct = default)
    {
        Demand(Permissions.ProvisionRouters);
        await using var db = kiboDb.ForCurrentUser();
        var operation = await db.ProvisioningOperations.FirstOrDefaultAsync(o => o.Id == operationId, ct);
        if (operation == null || operation.Status != ProvisioningStatus.Planned)
        {
            return OperationResult<ProvisioningOperation>.Fail("Only a planned operation can be applied.");
        }

        if (clock.GetUtcNow().UtcDateTime - operation.CreatedAt > TimeSpan.FromMinutes(15))
        {
            return OperationResult<ProvisioningOperation>.Fail("This plan is older than 15 minutes. Create a new plan so it reflects the router as it is now.");
        }

        var router = await db.Routers.FirstAsync(r => r.Id == operation.RouterId, ct);
        var connection = healthChecker.ConnectionFor(router);
        var changes = JsonSerializer.Deserialize<List<PlannedChange>>(operation.PlanJson) ?? [];
        var now = clock.GetUtcNow().UtcDateTime;

        try
        {
            var export = await client.ExportConfigurationAsync(connection, ct);
            db.RouterBackups.Add(new RouterBackup
            {
                TenantId = router.TenantId, RouterId = router.Id, Reason = $"Before provisioning operation {operation.Id}",
                ExportProtected = secrets.Protect(export), ExportLength = export.Length, CreatedAt = now
            });
            await db.SaveChangesAsync(ct);
        }
        catch (RouterOsException ex)
        {
            operation.Status = ProvisioningStatus.Failed;
            operation.Detail = $"Backup failed, nothing was changed: {ex.Message}";
            operation.CompletedAt = now;
            await db.SaveChangesAsync(ct);
            return OperationResult<ProvisioningOperation>.Fail(operation.Detail);
        }

        var undo = new List<Func<Task>>();
        string? failure = null;
        foreach (var change in changes)
        {
            try
            {
                var body = ToJson(change.Values);
                switch (change.Action)
                {
                    case "add":
                        var id = await client.CreateAsync(connection, change.Path, body, ct);
                        undo.Add(() => client.DeleteAsync(connection, change.Path, id, CancellationToken.None));
                        break;
                    case "set" when change.Id != null:
                        await client.UpdateAsync(connection, change.Path, change.Id, body, ct);
                        undo.Add(() => client.UpdateAsync(connection, change.Path, change.Id, ToJson(change.Previous ?? []), CancellationToken.None));
                        break;
                    case "set":
                        await client.CommandAsync(connection, change.Path + "/set", body, ct);
                        undo.Add(async () => await client.CommandAsync(connection, change.Path + "/set", ToJson(change.Previous ?? []), CancellationToken.None));
                        break;
                }
            }
            catch (RouterOsException ex)
            {
                failure = $"{change.Description}: {ex.Message}";
                break;
            }
        }

        if (failure == null)
        {
            var tenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == router.TenantId, ct);
            var remaining = await ComputeChangesAsync(router, tenant.PppoeEnabled, ct);
            if (remaining.Count > 0)
            {
                failure = "Verification failed; still different: " + string.Join("; ", remaining.Select(r => r.Description));
            }
        }

        if (failure != null)
        {
            var rollbackErrors = new List<string>();
            foreach (var step in Enumerable.Reverse(undo))
            {
                try
                {
                    await step();
                }
                catch (RouterOsException ex)
                {
                    rollbackErrors.Add(ex.Message);
                }
            }

            operation.Status = rollbackErrors.Count == 0 ? ProvisioningStatus.RolledBack : ProvisioningStatus.Failed;
            operation.Detail = rollbackErrors.Count == 0
                ? $"Rolled back: {failure}"
                : $"Rollback incomplete ({string.Join("; ", rollbackErrors)}). Restore from the saved backup. Cause: {failure}";
            db.RouterEvents.Add(new RouterEvent
            {
                TenantId = router.TenantId, RouterId = router.Id, Kind = RouterEventKind.ProvisioningFailed,
                Message = operation.Detail, OccurredAt = now
            });
        }
        else
        {
            operation.Status = ProvisioningStatus.Applied;
            operation.Detail = $"Applied and verified {changes.Count} change(s).";
            router.DriftDetectedAt = null;
            db.RouterEvents.Add(new RouterEvent
            {
                TenantId = router.TenantId, RouterId = router.Id, Kind = RouterEventKind.ConfigurationChanged,
                Message = operation.Detail, OccurredAt = now
            });
        }

        operation.CompletedAt = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("router.provisioning_" + operation.Status.ToString().ToLowerInvariant(), "Router", router.Id.ToString(), operation.Detail);
        return operation.Status == ProvisioningStatus.Applied
            ? OperationResult<ProvisioningOperation>.Ok(operation)
            : OperationResult<ProvisioningOperation>.Fail(operation.Detail);
    }

    public async Task<List<ProvisioningOperation>> HistoryAsync(Guid routerId)
    {
        await using var db = kiboDb.ForCurrentUser();
        return await db.ProvisioningOperations.AsNoTracking().Where(o => o.RouterId == routerId)
            .OrderByDescending(o => o.Id).Take(10).ToListAsync();
    }

    private async Task<JsonObject?> TryReadAsync(RouterConnection connection, string path, CancellationToken ct)
    {
        try
        {
            return await client.ReadAsync(connection, path, ct);
        }
        catch (RouterOsException)
        {
            return null;
        }
    }

    private static Dictionary<string, string> Diff(JsonObject current, Dictionary<string, string> desired)
    {
        var diff = new Dictionary<string, string>();
        foreach (var (key, value) in desired)
        {
            var actual = current[key]?.ToString() ?? "";
            if (!SameValue(key, actual, value))
            {
                diff[key] = value;
            }
        }

        return diff;
    }

    private static bool SameValue(string key, string actual, string desired)
    {
        if (key is "timeout" or "radius-interim-update" or "interim-update")
        {
            return RouterOsClient.ParseDuration(actual) == RouterOsClient.ParseDuration(desired) ||
                   (TimeSpan.TryParse(actual, out var span) && (long)span.TotalSeconds == RouterOsClient.ParseDuration(desired));
        }

        if (key is "service" or "login-by")
        {
            return actual.Split(',').Order().SequenceEqual(desired.Split(',').Order());
        }

        if (desired is "true" or "false")
        {
            return string.Equals(actual, desired, StringComparison.OrdinalIgnoreCase) ||
                   (desired == "true" && actual == "yes") || (desired == "false" && actual == "no");
        }

        return actual == desired;
    }

    private static string Normalize(string? text) => (text ?? "").Replace("\r\n", "\n").Trim();

    private static Dictionary<string, string> Previous(JsonObject current, Dictionary<string, string> diff) =>
        diff.Keys.ToDictionary(k => k, k => current[k]?.ToString() ?? "");

    private static JsonObject ToJson(Dictionary<string, string> values)
    {
        var json = new JsonObject();
        foreach (var (key, value) in values)
        {
            json[key] = value;
        }

        return json;
    }

    private void Demand(string permission)
    {
        if (!currentUser.Can(permission))
        {
            throw new UnauthorizedAccessException($"Missing permission {permission}.");
        }
    }
}
