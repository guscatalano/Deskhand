using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Deskhand.Core.Services;

public record NetAddressDto(string Address, string Family, int? PrefixLength);
public record NetAdapterDto(
    string Name, string Description, string Type, string Status, bool IsUp, string? Mac, long? SpeedMbps,
    IReadOnlyList<NetAddressDto> Addresses, IReadOnlyList<string> Gateways, IReadOnlyList<string> DnsServers, string? DhcpServer);
public record PingResultDto(string Host, string? Address, bool Success, string Status, long? RoundtripMs, int? Ttl);

/// <summary>Read-only network inventory and reachability: list the machine's network adapters with their
/// IPs / gateways / DNS, and ping a host. Complements the read-only connection list (netstat-like).</summary>
public static class NetworkService
{
    public static IReadOnlyList<NetAdapterDto> Adapters()
    {
        var list = new List<NetAdapterDto>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                var p = ni.GetIPProperties();
                var addrs = p.UnicastAddresses.Select(u => new NetAddressDto(
                    u.Address.ToString(),
                    u.Address.AddressFamily == AddressFamily.InterNetwork ? "IPv4"
                        : u.Address.AddressFamily == AddressFamily.InterNetworkV6 ? "IPv6" : u.Address.AddressFamily.ToString(),
                    Prefix(u))).ToList();

                string hex = ni.GetPhysicalAddress()?.ToString() ?? "";
                string? mac = hex.Length >= 12
                    ? string.Join(":", Enumerable.Range(0, hex.Length / 2).Select(i => hex.Substring(i * 2, 2))) : null;

                list.Add(new NetAdapterDto(
                    ni.Name, ni.Description, ni.NetworkInterfaceType.ToString(), ni.OperationalStatus.ToString(),
                    ni.OperationalStatus == OperationalStatus.Up,
                    mac, ni.Speed > 0 ? ni.Speed / 1_000_000 : null,
                    addrs,
                    p.GatewayAddresses.Select(g => g.Address.ToString()).ToList(),
                    p.DnsAddresses.Select(d => d.ToString()).ToList(),
                    SafeFirst(() => p.DhcpServerAddresses.FirstOrDefault()?.ToString())));
            }
            catch { }
        }
        return list;
    }

    public static PingResultDto Ping(string? host, int timeoutMs = 3000)
    {
        host = (host ?? "").Trim();
        if (host.Length == 0) return new PingResultDto("", null, false, "no host given", null, null);
        try
        {
            using var ping = new System.Net.NetworkInformation.Ping();
            var r = ping.Send(host, Math.Clamp(timeoutMs, 100, 20000));
            return new PingResultDto(host, r.Address?.ToString(), r.Status == IPStatus.Success, r.Status.ToString(),
                r.Status == IPStatus.Success ? r.RoundtripTime : null, SafeTtl(r));
        }
        catch (Exception ex) { return new PingResultDto(host, null, false, (ex.InnerException ?? ex).Message, null, null); }
    }

    private static int? Prefix(UnicastIPAddressInformation u) { try { return u.PrefixLength; } catch { return null; } }
    private static int? SafeTtl(PingReply r) { try { return r.Options?.Ttl; } catch { return null; } }
    private static string? SafeFirst(Func<string?> f) { try { return f(); } catch { return null; } }
}
