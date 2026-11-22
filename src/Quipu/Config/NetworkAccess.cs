using System.Net;

namespace Quipu.Config;

/// <summary>
/// Who may reach the dashboard, API and MCP. Localhost always. Other machines only when the config says so:
/// either their network is in server.allowedNetworks, or an API key is set (which every request must then send).
/// Listening on 0.0.0.0 alone does not open Quipu to the network.
/// </summary>
public static class NetworkAccess
{
    public const string DeniedNoRule =
        "Remote access is off: set server.allowedNetworks (e.g. 10.0.0.0/24) or server.apiKey in quipu.yaml";
    public const string DeniedNetwork = "Your network is not in server.allowedNetworks";
    public const string DeniedHostName = "Open Quipu by IP address or localhost, not by another host name";

    /// <summary>
    /// Stops DNS rebinding: a web page pointing its own domain at 127.0.0.1 would otherwise pass the origin check.
    /// IP addresses, localhost and this PC's name pass; any other name is refused.
    /// </summary>
    public static bool IsAllowedHostName(string host) =>
        Uri.CheckHostName(host) != UriHostNameType.Dns
        || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase);

    /// <summary>null = allowed, otherwise the reason it is refused.</summary>
    public static string? Check(IPAddress? remote, ServerConfig server)
    {
        if (remote is null) return null; // in-process / test server
        if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
        if (IPAddress.IsLoopback(remote)) return null;

        var networks = server.AllowedNetworks ?? [];
        if (networks.Count > 0)
            return networks.Any(n => IPNetwork.TryParse(n, out var net) && net.Contains(remote)) ? null : DeniedNetwork;
        return string.IsNullOrEmpty(server.ApiKey) ? DeniedNoRule : null;
    }

    public static bool ListensBeyondLocalhost(ServerConfig server) =>
        Uri.TryCreate(server.Listen, UriKind.Absolute, out var u) && !u.IsLoopback && u.Host != "localhost";

    public static bool IsLoopbackHost(string? host) =>
        string.IsNullOrWhiteSpace(host) || host == "localhost" || IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip);

    /// <summary>Plain-language warnings about what is reachable from the network, shown in the dashboard.</summary>
    public static List<string> Warnings(QuipuConfig cfg)
    {
        var warnings = new List<string>();
        var s = cfg.Server;
        if (ListensBeyondLocalhost(s))
        {
            if ((s.AllowedNetworks ?? []).Count == 0 && string.IsNullOrEmpty(s.ApiKey))
                warnings.Add("Quipu listens on the network, but other machines are refused until you set server.allowedNetworks or server.apiKey.");
            else if (string.IsNullOrEmpty(s.ApiKey))
                warnings.Add($"Anyone on {string.Join(", ", s.AllowedNetworks!)} can control Quipu (no server.apiKey set).");
        }

        var exposed = cfg.Models
            .Where(kv => kv.Value is not null)
            .Where(kv => !IsLoopbackHost(kv.Value.Host ?? cfg.Defaults.Host))
            .Where(kv => !(cfg.Defaults.Args ?? []).Concat(kv.Value.Args ?? []).Contains("--api-key"))
            .Select(kv => kv.Key).ToList();
        if (exposed.Count > 0)
            warnings.Add($"Model servers ({string.Join(", ", exposed)}) listen on the network without --api-key. " +
                         "llama-server can't filter clients, so limit the ports with a firewall rule (see README).");
        return warnings;
    }
}
