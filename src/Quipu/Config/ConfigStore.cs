using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Quipu.Config;

/// <summary>
/// Owns quipu.yaml: loads it, validates it, writes changes made through the API back to it,
/// and hot-reloads when you edit the file by hand.
/// </summary>
public sealed partial class ConfigStore : IDisposable
{
    private const string Header =
        "# Quipu configuration (config as code).\n" +
        "# Edit by hand (Quipu reloads it on save) or change it through the dashboard / API.\n" +
        "# Changes made through the API rewrite this file (comments other than this header are not kept).\n" +
        "# Reference: README.md in the Quipu repository.\n\n";

    private static readonly string[] Backends = ["auto", "cuda", "vulkan", "cpu", "rocm", "sycl", "openvino"];

    private ILogger _log;
    private readonly object _gate = new();
    private readonly ISerializer _serializer;
    private readonly IDeserializer _deserializer;
    private FileSystemWatcher? _watcher;
    private string _lastWrittenHash = "";
    private Timer? _debounce;

    public string FilePath { get; }

    /// <summary>Swap in the real logger once logging is configured (the store is created before it).</summary>
    public void UseLogger(ILogger log) => _log = log;
    public QuipuConfig Current { get; private set; }
    public string? LastError { get; private set; }
    public DateTimeOffset LoadedAt { get; private set; }

    /// <summary>Raised after a successful load/save (old, new).</summary>
    public event Action<QuipuConfig, QuipuConfig>? Changed;

    public ConfigStore(string filePath, ILogger log)
    {
        _log = log;
        FilePath = Path.GetFullPath(filePath);
        _serializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull | DefaultValuesHandling.OmitEmptyCollections)
            .DisableAliases()
            .Build();
        _deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        if (File.Exists(FilePath))
        {
            Current = Parse(File.ReadAllText(FilePath));
        }
        else
        {
            Current = CreateDefault();
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            WriteFile(Current);
            _log.LogInformation("Created default config at {Path}", FilePath);
        }
        EnsureDirectories(Current);
        LoadedAt = DateTimeOffset.Now;
    }

    // ---------------------------------------------------------------- location

    /// <summary>--config arg, QUIPU_CONFIG env, an existing quipu.yaml beside the exe, else ~\Quipu\quipu.yaml.</summary>
    public static string Locate(string[] args) => Locate(args,
        Environment.GetEnvironmentVariable("QUIPU_CONFIG"), AppContext.BaseDirectory, DefaultRoot(), File.Exists);

    internal static string Locate(string[] args, string? env, string exeDir, string defaultRoot, Func<string, bool> exists)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] is "--config" or "-c") return args[i + 1];

        if (!string.IsNullOrWhiteSpace(env)) return env;

        var besideExe = Path.Combine(exeDir, "quipu.yaml");
        return exists(besideExe) ? besideExe : Path.Combine(defaultRoot, "quipu.yaml");
    }

    /// <summary>
    /// Where a new config (and by default models, builds and logs) goes: %USERPROFILE%\Quipu.
    /// Point paths.models somewhere else, e.g. another drive, to keep big model files off C:.
    /// </summary>
    public static string DefaultRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Quipu");

    private QuipuConfig CreateDefault()
    {
        var root = Path.GetDirectoryName(FilePath)!;
        return new QuipuConfig
        {
            Paths = new PathsConfig
            {
                Models = Path.Combine(root, "models"),
                Installs = Path.Combine(root, "builds"),
                Logs = Path.Combine(root, "logs"),
            },
        };
    }

    // ---------------------------------------------------------------- read / write

    public QuipuConfig Parse(string yaml)
    {
        var cfg = string.IsNullOrWhiteSpace(yaml) ? new QuipuConfig() : _deserializer.Deserialize<QuipuConfig>(yaml) ?? new QuipuConfig();
        Normalize(cfg);
        Validate(cfg);
        return cfg;
    }

    public string Serialize(QuipuConfig cfg) => Header + _serializer.Serialize(cfg);

    public string ReadRaw() => File.Exists(FilePath) ? File.ReadAllText(FilePath) : Serialize(Current);

    /// <summary>Apply a mutation, validate, persist, publish.</summary>
    public QuipuConfig Update(Action<QuipuConfig> mutate)
    {
        QuipuConfig old, next;
        lock (_gate)
        {
            // writing now would replace the user's (broken) hand edit with the last good config
            if (LastError is not null)
                throw new InvalidOperationException($"quipu.yaml has an error, fix it first (or replace it in the Config editor): {LastError}");
            old = Current;
            next = Parse(_serializer.Serialize(Current));
            mutate(next);
            Normalize(next);
            Validate(next);
            WriteFile(next);
            Current = next;
            LastError = null;
            LoadedAt = DateTimeOffset.Now;
        }
        EnsureDirectories(next);
        Changed?.Invoke(old, next);
        return next;
    }

    /// <summary>Replace the whole file with new YAML text (validated first).</summary>
    public QuipuConfig ReplaceRaw(string yaml)
    {
        var parsed = Parse(yaml); // throws on invalid
        QuipuConfig old;
        lock (_gate)
        {
            old = Current;
            WriteText(yaml.TrimStart().StartsWith('#') ? yaml : Header + yaml);
            Current = parsed;
            LastError = null;
            LoadedAt = DateTimeOffset.Now;
        }
        EnsureDirectories(parsed);
        Changed?.Invoke(old, parsed);
        return parsed;
    }

    public bool Reload()
    {
        try
        {
            var text = File.ReadAllText(FilePath);
            if (Hash(text) == _lastWrittenHash)
            {
                // our own write, or the file was reverted to what's already loaded
                LastError = null;
                return false;
            }
            var parsed = Parse(text);
            QuipuConfig old;
            lock (_gate)
            {
                old = Current;
                Current = parsed;
                LastError = null;
                LoadedAt = DateTimeOffset.Now;
                _lastWrittenHash = Hash(text);
            }
            EnsureDirectories(parsed);
            _log.LogInformation("Reloaded config from {Path}", FilePath);
            Changed?.Invoke(old, parsed);
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _log.LogWarning("Config reload failed, keeping previous config: {Error}", ex.Message);
            return false;
        }
    }

    private void WriteFile(QuipuConfig cfg) => WriteText(Serialize(cfg));

    /// <summary>Temp file + move, so a crash mid-write never leaves a cut-off quipu.yaml.</summary>
    private void WriteText(string text)
    {
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, FilePath, overwrite: true);
        _lastWrittenHash = Hash(text);
    }

    public void StartWatching()
    {
        _watcher = new FileSystemWatcher(Path.GetDirectoryName(FilePath)!, Path.GetFileName(FilePath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };
        FileSystemEventHandler onChange = (_, _) =>
        {
            _debounce?.Dispose();
            _debounce = new Timer(_ => Reload(), null, 400, Timeout.Infinite);
        };
        _watcher.Changed += onChange;
        _watcher.Created += onChange;
        _watcher.Renamed += (s, e) => onChange(s, e);
    }

    // ---------------------------------------------------------------- rules

    private static void Normalize(QuipuConfig cfg)
    {
        cfg.Paths ??= new PathsConfig();
        cfg.LlamaCpp ??= new LlamaCppConfig();
        cfg.Server ??= new ServerConfig();
        cfg.HuggingFace ??= new HuggingFaceConfig();
        cfg.Defaults ??= new ModelParams();
        cfg.Models = new Dictionary<string, ModelProfile>(cfg.Models ?? new(), StringComparer.OrdinalIgnoreCase);
        cfg.LlamaCpp.Backend = (cfg.LlamaCpp.Backend ?? "auto").Trim().ToLowerInvariant();
        cfg.LlamaCpp.Version = string.IsNullOrWhiteSpace(cfg.LlamaCpp.Version) ? "latest" : cfg.LlamaCpp.Version.Trim();
        cfg.LlamaCpp.CudaVersion = string.IsNullOrWhiteSpace(cfg.LlamaCpp.CudaVersion) ? "auto" : cfg.LlamaCpp.CudaVersion.Trim();
        if (string.IsNullOrWhiteSpace(cfg.Server.ApiKey)) cfg.Server.ApiKey = null;
        if (string.IsNullOrWhiteSpace(cfg.HuggingFace.ActiveToken)) cfg.HuggingFace.ActiveToken = null;
    }

    public static void Validate(QuipuConfig cfg)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(cfg.Paths.Models)) errors.Add("paths.models is required");
        if (string.IsNullOrWhiteSpace(cfg.Paths.Installs)) errors.Add("paths.installs is required");
        if (!Backends.Contains(cfg.LlamaCpp.Backend)) errors.Add($"llamaCpp.backend must be one of: {string.Join(", ", Backends)}");
        if (!Uri.TryCreate(cfg.Server.Listen, UriKind.Absolute, out _)) errors.Add("server.listen must be a URL like http://127.0.0.1:5250");
        if (cfg.Server.MaxActiveModels < 0) errors.Add("server.maxActiveModels must be 0 (no limit) or more");
        foreach (var n in cfg.Server.AllowedNetworks ?? [])
            if (!System.Net.IPNetwork.TryParse(n, out _)) errors.Add($"server.allowedNetworks: '{n}' is not a network like 10.0.0.0/24");
        if (cfg.Defaults?.IdleUnloadMinutes < 0) errors.Add("defaults.idleUnloadMinutes must be 0 (never) or more");
        var ports = new Dictionary<int, string>();
        foreach (var (name, m) in cfg.Models)
        {
            if (!ValidName().IsMatch(name)) errors.Add($"model name '{name}' may only contain letters, digits, '.', '_' and '-'");
            if (m is null) { errors.Add($"model '{name}' is empty"); continue; }
            if (string.IsNullOrWhiteSpace(m.File) && m.Hf is null) errors.Add($"model '{name}' needs 'file' or 'hf'");
            if (m.Port is { } p)
            {
                if (p is < 1 or > 65535) errors.Add($"model '{name}': port {p} out of range");
                else if (ports.TryGetValue(p, out var other)) errors.Add($"models '{other}' and '{name}' both use port {p}");
                else ports[p] = name;
            }
            if (m.Hf is not null && (string.IsNullOrWhiteSpace(m.Hf.Repo) || string.IsNullOrWhiteSpace(m.Hf.File)))
                errors.Add($"model '{name}': hf needs repo and file");
        }
        if (errors.Count > 0) throw new ConfigValidationException(errors);
    }

    public static bool IsValidModelName(string name) => ValidName().IsMatch(name);

    /// <summary>JSON merge-patch (keys case-insensitive, null removes). "env" is replaced as a whole, so a
    /// variable left out of the patch is removed instead of lingering.</summary>
    public static void MergePatch(System.Text.Json.Nodes.JsonObject target, System.Text.Json.Nodes.JsonObject patch)
    {
        foreach (var (key, value) in patch)
        {
            var existingKey = target.Select(kv => kv.Key).FirstOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)) ?? key;
            if (value is null) target.Remove(existingKey);
            else if (value is System.Text.Json.Nodes.JsonObject po && target[existingKey] is System.Text.Json.Nodes.JsonObject to
                     && !existingKey.Equals("env", StringComparison.OrdinalIgnoreCase)) MergePatch(to, po);
            else target[existingKey] = value.DeepClone();
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex ValidName();

    private static void EnsureDirectories(QuipuConfig cfg)
    {
        foreach (var dir in new[] { cfg.Paths.Models, cfg.Paths.Installs, cfg.Paths.Logs })
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try { Directory.CreateDirectory(dir); } catch { /* surfaced later by the operation that needs it */ }
        }
    }

    private static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce?.Dispose();
    }
}

public sealed class ConfigValidationException(IReadOnlyList<string> errors)
    : Exception("Invalid configuration: " + string.Join("; ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
