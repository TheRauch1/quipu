using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Quipu.Config;

namespace Quipu.Services;

/// <summary>
/// Builds a models.json entry for the pi coding agent (~/.pi/agent/models.json) that points at a model's llama-server.
/// Values come from the model's actual command line; when the server is running, /props adds vision and reasoning support.
/// </summary>
public sealed class PiExportService(ConfigStore config, InstanceManager instances, IHttpClientFactory httpFactory)
{
    public const string ProviderId = "quipu";
    private static readonly string[] PiThinkingLevels = ["minimal", "low", "medium", "high", "xhigh", "max"];

    public async Task<PiExport> ExportAsync(string name, string? host, string? requestHost, CancellationToken ct)
    {
        var cfg = config.Current;
        if (!cfg.Models.TryGetValue(name, out var profile)) throw new KeyNotFoundException($"Model '{name}' is not configured");
        var (_, args) = instances.PreviewCommand(name);
        var notes = new List<string>();

        string? Arg(params string[] names)
        {
            string? value = null; // last one wins, like llama-server
            for (var i = 0; i < args.Count - 1; i++)
                if (names.Contains(args[i])) value = args[i + 1];
            return value;
        }
        int? IntArg(params string[] names) =>
            int.TryParse(Arg(names), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

        var bind = Arg("--host") ?? "127.0.0.1";
        var port = IntArg("--port") ?? 8080;
        var id = Arg("-a", "--alias") ?? name;
        var apiKey = Arg("--api-key");

        // hosts a client could use for this server
        var hosts = new List<string>();
        var bindsAll = bind is "0.0.0.0" or "::" or "[::]";
        if (bindsAll || IPAddress.TryParse(bind, out var b) && IPAddress.IsLoopback(b)) hosts.Add("127.0.0.1");
        if (bindsAll) hosts.AddRange(LanAddresses(cfg.Server.AllowedNetworks));
        else if (!hosts.Contains(bind)) hosts.Add(bind);
        if (!bindsAll && hosts.Count == 1 && hosts[0] == "127.0.0.1")
            notes.Add("The server only listens on 127.0.0.1, so pi must run on this PC. Set host 0.0.0.0 (model or defaults) to reach it from the network.");

        var chosen = host is { Length: > 0 } && hosts.Contains(host) ? host
            : requestHost is { Length: > 0 } && hosts.Contains(requestHost) ? requestHost
            : hosts[0];

        // probe the running server for capabilities
        int? ctxFromServer = null;
        bool? vision = null, reasoning = null;
        string[] effortLevels = [];
        var probed = false;
        if (instances.Get(name) is { State: InstanceState.Ready } inst)
        {
            try
            {
                var http = httpFactory.CreateClient("local");
                var props = JsonNode.Parse(await http.GetStringAsync($"{inst.Url}/props", ct));
                vision = props?["modalities"]?["vision"]?.GetValue<bool>();
                var caps = props?["chat_template_caps"];
                reasoning = caps?["supports_reasoning_effort"]?.GetValue<bool>() == true || caps?["supports_preserve_reasoning"]?.GetValue<bool>() == true;
                effortLevels = caps?["reasoning_effort_levels"]?.AsArray().Select(x => x?.GetValue<string>()).OfType<string>().ToArray() ?? [];
                ctxFromServer = props?["default_generation_settings"]?["n_ctx"]?.GetValue<int>();
                probed = true;
            }
            catch (Exception ex) { notes.Add($"Could not read /props from the running server ({ex.Message}); capabilities are guessed from the profile."); }
        }
        else notes.Add("The model is not running, so vision and reasoning support are guessed from the profile. Start it and export again for exact values.");

        var parallel = Math.Max(1, IntArg("-np", "--parallel") ?? 1);
        var ctx = IntArg("-c", "--ctx-size") is > 0 and var c ? c / parallel : ctxFromServer ?? 0;
        if (ctx <= 0) { ctx = 128000; notes.Add("Context size is the model's maximum; contextWindow is set to pi's default 128000. Adjust it to the real value."); }
        var maxTokens = IntArg("-n", "--n-predict", "--predict") is > 0 and var n ? n : 16384;
        maxTokens = Math.Min(maxTokens, ctx);

        vision ??= profile.Mmproj is not null || profile.Hf?.Mmproj is not null;
        reasoning ??= false;

        var model = new JsonObject
        {
            ["id"] = id,
            ["name"] = $"{name} (Quipu)",
            ["baseUrl"] = $"http://{(chosen.Contains(':') ? $"[{chosen}]" : chosen)}:{port}/v1",
            ["reasoning"] = reasoning,
            ["input"] = vision == true ? new JsonArray("text", "image") : new JsonArray("text"),
            ["contextWindow"] = ctx,
            ["maxTokens"] = maxTokens,
            ["cost"] = new JsonObject { ["input"] = 0, ["output"] = 0, ["cacheRead"] = 0, ["cacheWrite"] = 0 },
        };
        if (reasoning == true && effortLevels.Length > 0)
        {
            var map = new JsonObject();
            foreach (var level in PiThinkingLevels)
                map[level] = effortLevels.Contains(level) ? level : null;
            model["thinkingLevelMap"] = map;
        }

        var compat = new JsonObject
        {
            ["supportsDeveloperRole"] = false,
            ["supportsStore"] = false,
            ["maxTokensField"] = "max_tokens",
        };
        if (probed) compat["supportsReasoningEffort"] = reasoning == true && effortLevels.Length > 0;

        var provider = new JsonObject
        {
            ["name"] = "Quipu",
            ["api"] = "openai-completions",
            ["apiKey"] = apiKey ?? "quipu", // pi only lists providers with a key; llama-server ignores it unless --api-key is set
            ["compat"] = compat,
            ["models"] = new JsonArray(model),
        };
        var doc = new JsonObject
        {
            ["$schema"] = "https://pi.dev/schemas/models.schema.json",
            ["providers"] = new JsonObject { [ProviderId] = provider },
        };

        return new PiExport(doc, model, ProviderId, chosen, hosts, probed, notes,
            @"%USERPROFILE%\.pi\agent\models.json  (Linux/macOS: ~/.pi/agent/models.json)");
    }

    /// <summary>This PC's IPv4 addresses on up network interfaces, limited to the allowed networks when set.
    /// Interfaces with a default gateway (the real LAN) come before virtual ones (VMware, WSL, Hyper-V).</summary>
    public static IEnumerable<string> LanAddresses(IReadOnlyList<string>? allowed)
    {
        var nets = (allowed ?? []).Select(n => IPNetwork.TryParse(n, out var net) ? net : (IPNetwork?)null).OfType<IPNetwork>().ToList();
        IEnumerable<IPAddress> addrs;
        try
        {
            addrs = NetworkInterface.GetAllNetworkInterfaces()
                .Where(i => i.OperationalStatus == OperationalStatus.Up && i.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Select(i => (Props: i.GetIPProperties(), Iface: i))
                .OrderBy(x => x.Props.GatewayAddresses.Any(g => !g.Address.Equals(IPAddress.Any)) ? 0 : 1)
                .SelectMany(x => x.Props.UnicastAddresses.Select(a => a.Address))
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !a.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                .ToList();
        }
        catch { yield break; }
        foreach (var a in addrs.Distinct())
            if (nets.Count == 0 || nets.Any(n => n.Contains(a))) yield return a.ToString();
    }
}

public sealed record PiExport(
    JsonObject Config, JsonObject Model, string Provider, string Host, IReadOnlyList<string> Hosts,
    bool Probed, IReadOnlyList<string> Notes, string Path);
