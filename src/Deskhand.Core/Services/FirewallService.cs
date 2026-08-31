using System.Runtime.InteropServices;

namespace Deskhand.Core.Services;

public record FirewallRuleDto(
    string Name, string? Description, string Direction, string Action, string? Protocol,
    string? LocalPorts, string? RemotePorts, bool Enabled, string Profiles, string? Grouping,
    string? ApplicationName, string? LocalAddresses, string? RemoteAddresses, bool Managed);

public record FirewallRulesResultDto(int Total, int Returned, IReadOnlyList<FirewallRuleDto> Rules, string? Error = null);

public record FirewallOpResultDto(
    bool Ok, string? RuleName, int Port, string Protocol, string Direction, string Action,
    int Removed = 0, string? Error = null, string? Hint = null);

/// <summary>
/// Windows Firewall rules via the Firewall COM API (<c>HNetCfg.FwPolicy2</c>, INetFwPolicy2).
///
/// <para><b>Listing</b> is read-only and needs no elevation. <b>Opening/closing</b> ports adds/removes rules and
/// needs Administrator; without it the OS returns access-denied and we surface a crisp hint.</para>
///
/// <para><b>Safety.</b> Every rule Deskhand creates is tagged with a distinctive <see cref="ManagedGroup"/>
/// (the rule's Grouping) and a <see cref="NamePrefix"/> name. Close operations <b>only ever remove rules
/// carrying that tag</b> — Deskhand will never delete a rule it didn't open, so it can't take down your RDP,
/// SSH, or any pre-existing rule. The tag lives in the firewall itself, so it survives restarts.</para>
///
/// <para>The write ops are OFF unless <c>DESKHAND_ENABLE_FIREWALL_ADMIN</c> is set; the host layer additionally
/// requires the kill switch armed and audits every change.</para>
/// </summary>
public static class FirewallService
{
    public const string ManagedGroup = "Deskhand (managed)";
    public const string NamePrefix = "Deskhand";

    private const int DIR_IN = 1, DIR_OUT = 2;
    private const int ACTION_BLOCK = 0, ACTION_ALLOW = 1;
    private const int PROTO_TCP = 6, PROTO_UDP = 17;
    private const int PROFILE_ALL = 0x7fffffff;

    /// <summary>Opt-in for open/close (write) ops. Listing is always allowed.</summary>
    public static bool AdminEnabled
    {
        get
        {
            var v = Environment.GetEnvironmentVariable("DESKHAND_ENABLE_FIREWALL_ADMIN")?.Trim().ToLowerInvariant();
            return v is "1" or "true" or "yes" or "on";
        }
    }

    // ---- read ----

    /// <summary>Enumerate firewall rules, newest OS APIs. Optional filters keep the (often hundreds of) rules
    /// manageable: direction in/out, a specific port, enabled-only, a name/substring, or only Deskhand-managed.</summary>
    public static FirewallRulesResultDto List(string? direction = null, int? port = null, bool? enabledOnly = null,
        string? contains = null, bool managedOnly = false, int max = 200)
    {
        dynamic policy;
        try { policy = FwPolicy(); }
        catch (Exception ex) { return new FirewallRulesResultDto(0, 0, Array.Empty<FirewallRuleDto>(), "Firewall API unavailable: " + ex.Message); }

        int? wantDir = direction?.Trim().ToLowerInvariant() switch { "in" or "inbound" => DIR_IN, "out" or "outbound" => DIR_OUT, _ => null };
        string? needle = string.IsNullOrWhiteSpace(contains) ? null : contains.Trim();

        var all = new List<FirewallRuleDto>();
        int total = 0;
        try
        {
            foreach (var raw in policy.Rules)
            {
                total++;
                var dto = ToDto(raw);
                if (dto is null) continue;
                if (wantDir is int wd && Dir(dto.Direction) != wd) continue;
                if (enabledOnly == true && !dto.Enabled) continue;
                if (managedOnly && !dto.Managed) continue;
                if (needle is not null && !(dto.Name?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false)
                                       && !(dto.Grouping?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false)) continue;
                if (port is int p && !PortMatches(dto.LocalPorts, p)) continue;
                all.Add(dto);
            }
        }
        catch (Exception ex) { return new FirewallRulesResultDto(total, all.Count, all, "Enumeration stopped early: " + ex.Message); }

        var page = all.Count > max ? all.GetRange(0, max) : all;
        return new FirewallRulesResultDto(total, page.Count, page);
    }

    /// <summary>The rules Deskhand itself opened (Grouping == ManagedGroup).</summary>
    public static FirewallRulesResultDto ListManaged(int max = 200) => List(managedOnly: true, max: max);

    // ---- write (Administrator) ----

    /// <summary>Open a port: add an inbound (or outbound) Allow rule for a TCP/UDP port, tagged as Deskhand-managed
    /// so it can be cleanly closed later. remoteAddresses (optional) scopes who may connect (e.g. "LocalSubnet").</summary>
    public static FirewallOpResultDto OpenPort(int port, string? protocol = "tcp", string? direction = "in",
        string? remoteAddresses = null, string? name = null)
    {
        string proto = NormProto(protocol);
        int dir = NormDir(direction);
        string dirStr = dir == DIR_OUT ? "out" : "in";
        if (port is < 1 or > 65535)
            return new FirewallOpResultDto(false, null, port, proto, dirStr, "Allow", Error: "Port must be 1–65535.");
        if (!AdminEnabled)
            return new FirewallOpResultDto(false, null, port, proto, dirStr, "Allow",
                Error: "Firewall admin is disabled. Set DESKHAND_ENABLE_FIREWALL_ADMIN=1.");

        string ruleName = string.IsNullOrWhiteSpace(name)
            ? $"{NamePrefix} {proto.ToUpperInvariant()} {port} ({dirStr})"
            : $"{NamePrefix}: {name.Trim()}";
        try
        {
            dynamic rule = NewRule();
            rule.Name = ruleName;
            rule.Description = $"Opened by Deskhand on {DateTime.Now:yyyy-MM-dd HH:mm}. Remove via Deskhand or by deleting this rule.";
            rule.Protocol = proto == "udp" ? PROTO_UDP : PROTO_TCP;
            rule.LocalPorts = port.ToString();
            rule.Direction = dir;
            rule.Action = ACTION_ALLOW;
            rule.Enabled = true;
            rule.Profiles = PROFILE_ALL;
            rule.Grouping = ManagedGroup;
            if (!string.IsNullOrWhiteSpace(remoteAddresses)) rule.RemoteAddresses = remoteAddresses.Trim();

            dynamic policy = FwPolicy();
            policy.Rules.Add(rule);
            return new FirewallOpResultDto(true, ruleName, port, proto, dirStr, "Allow");
        }
        catch (Exception ex)
        {
            return new FirewallOpResultDto(false, ruleName, port, proto, dirStr, "Allow",
                Error: Describe(ex), Hint: IsAccessDenied(ex) ? "Adding a firewall rule requires running as Administrator." : null);
        }
    }

    /// <summary>Close a port Deskhand opened: remove ONLY Deskhand-managed rules matching this port/protocol
    /// (and direction). Rules Deskhand did not create are never touched. Returns how many were removed.</summary>
    public static FirewallOpResultDto ClosePort(int port, string? protocol = "tcp", string? direction = "in")
    {
        string proto = NormProto(protocol);
        int dir = NormDir(direction);
        string dirStr = dir == DIR_OUT ? "out" : "in";
        if (!AdminEnabled)
            return new FirewallOpResultDto(false, null, port, proto, dirStr, "Allow",
                Error: "Firewall admin is disabled. Set DESKHAND_ENABLE_FIREWALL_ADMIN=1.");

        int wantProto = proto == "udp" ? PROTO_UDP : PROTO_TCP;
        try
        {
            dynamic policy = FwPolicy();
            // Collect the names of OUR rules that match; only these get removed.
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in policy.Rules)
            {
                if (!IsManaged(raw)) continue;                 // never remove a rule we didn't create
                if (I(() => (int)raw.Direction) != dir) continue;
                if (I(() => (int)raw.Protocol) != wantProto) continue;
                if (!PortMatches(S(() => (string)raw.LocalPorts), port)) continue;
                var n = S(() => (string)raw.Name);
                if (n is not null) names.Add(n);
            }
            int removed = 0;
            foreach (var n in names) { try { policy.Rules.Remove(n); removed++; } catch { } }

            return removed > 0
                ? new FirewallOpResultDto(true, string.Join(", ", names), port, proto, dirStr, "Allow", Removed: removed)
                : new FirewallOpResultDto(false, null, port, proto, dirStr, "Allow",
                    Error: $"No Deskhand-managed {proto.ToUpperInvariant()} {dirStr} rule for port {port} was found (Deskhand only closes ports it opened).");
        }
        catch (Exception ex)
        {
            return new FirewallOpResultDto(false, null, port, proto, dirStr, "Allow",
                Error: Describe(ex), Hint: IsAccessDenied(ex) ? "Removing a firewall rule requires running as Administrator." : null);
        }
    }

    /// <summary>Remove every rule Deskhand opened. Useful cleanup. Never touches non-managed rules.</summary>
    public static FirewallOpResultDto CloseAllManaged()
    {
        if (!AdminEnabled)
            return new FirewallOpResultDto(false, null, 0, "any", "any", "Allow",
                Error: "Firewall admin is disabled. Set DESKHAND_ENABLE_FIREWALL_ADMIN=1.");
        try
        {
            dynamic policy = FwPolicy();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in policy.Rules)
                if (IsManaged(raw)) { var n = S(() => (string)raw.Name); if (n is not null) names.Add(n); }
            int removed = 0;
            foreach (var n in names) { try { policy.Rules.Remove(n); removed++; } catch { } }
            return new FirewallOpResultDto(true, string.Join(", ", names), 0, "any", "any", "Allow", Removed: removed);
        }
        catch (Exception ex)
        {
            return new FirewallOpResultDto(false, null, 0, "any", "any", "Allow",
                Error: Describe(ex), Hint: IsAccessDenied(ex) ? "Removing firewall rules requires running as Administrator." : null);
        }
    }

    // ---- helpers ----

    private static dynamic FwPolicy()
    {
        var t = Type.GetTypeFromProgID("HNetCfg.FwPolicy2")
            ?? throw new PlatformNotSupportedException("HNetCfg.FwPolicy2 not registered.");
        return Activator.CreateInstance(t)!;
    }
    private static dynamic NewRule()
    {
        var t = Type.GetTypeFromProgID("HNetCfg.FWRule")
            ?? throw new PlatformNotSupportedException("HNetCfg.FWRule not registered.");
        return Activator.CreateInstance(t)!;
    }

    private static FirewallRuleDto? ToDto(dynamic raw)
    {
        try
        {
            string? grouping = S(() => (string)raw.Grouping);
            bool managed = string.Equals(grouping, ManagedGroup, StringComparison.OrdinalIgnoreCase);
            int protoNum = I(() => (int)raw.Protocol) ?? -1;
            return new FirewallRuleDto(
                Name: S(() => (string)raw.Name) ?? "(unnamed)",
                Description: S(() => (string)raw.Description),
                Direction: (I(() => (int)raw.Direction) == DIR_OUT) ? "out" : "in",
                Action: (I(() => (int)raw.Action) == ACTION_BLOCK) ? "Block" : "Allow",
                Protocol: ProtoName(protoNum),
                LocalPorts: protoNum is PROTO_TCP or PROTO_UDP ? S(() => (string)raw.LocalPorts) : null,
                RemotePorts: protoNum is PROTO_TCP or PROTO_UDP ? S(() => (string)raw.RemotePorts) : null,
                Enabled: B(() => (bool)raw.Enabled) ?? false,
                Profiles: ProfileNames(I(() => (int)raw.Profiles) ?? 0),
                Grouping: grouping,
                ApplicationName: S(() => (string)raw.ApplicationName),
                LocalAddresses: S(() => (string)raw.LocalAddresses),
                RemoteAddresses: S(() => (string)raw.RemoteAddresses),
                Managed: managed);
        }
        catch { return null; }
    }

    private static bool IsManaged(dynamic raw) =>
        string.Equals(S(() => (string)raw.Grouping), ManagedGroup, StringComparison.OrdinalIgnoreCase);

    private static int Dir(string s) => s == "out" ? DIR_OUT : DIR_IN;

    private static bool PortMatches(string? localPorts, int port)
    {
        if (string.IsNullOrWhiteSpace(localPorts)) return false;
        foreach (var part in localPorts.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Equals(port.ToString(), StringComparison.Ordinal)) return true;
            var dash = part.IndexOf('-');
            if (dash > 0 && int.TryParse(part[..dash], out var lo) && int.TryParse(part[(dash + 1)..], out var hi)
                && port >= lo && port <= hi) return true;
        }
        return false;
    }

    private static string NormProto(string? p) => (p ?? "tcp").Trim().ToLowerInvariant() switch { "udp" or "17" => "udp", _ => "tcp" };
    private static int NormDir(string? d) => (d ?? "in").Trim().ToLowerInvariant() is "out" or "outbound" ? DIR_OUT : DIR_IN;
    private static string? ProtoName(int n) => n switch { PROTO_TCP => "TCP", PROTO_UDP => "UDP", 1 => "ICMPv4", 58 => "ICMPv6", 256 => "Any", -1 => null, _ => n.ToString() };
    private static string ProfileNames(int mask)
    {
        if (mask == PROFILE_ALL || mask == 0) return "All";
        var parts = new List<string>();
        if ((mask & 1) != 0) parts.Add("Domain");
        if ((mask & 2) != 0) parts.Add("Private");
        if ((mask & 4) != 0) parts.Add("Public");
        return parts.Count > 0 ? string.Join(",", parts) : "All";
    }

    // Safe COM property accessors (a rule can throw on properties not valid for its protocol).
    private static string? S(Func<string> f) { try { var v = f(); return string.IsNullOrEmpty(v) ? null : v; } catch { return null; } }
    private static int? I(Func<int> f) { try { return f(); } catch { return null; } }
    private static bool? B(Func<bool> f) { try { return f(); } catch { return null; } }

    private static bool IsAccessDenied(Exception ex) =>
        ex is UnauthorizedAccessException || (ex is COMException c && (uint)c.HResult == 0x80070005) || (uint)ex.HResult == 0x80070005;
    private static string Describe(Exception ex) =>
        IsAccessDenied(ex) ? "Access denied (E_ACCESSDENIED)." : $"{ex.GetType().Name}: {ex.Message}";
}
