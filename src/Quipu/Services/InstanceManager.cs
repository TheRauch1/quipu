using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Serialization;
using Quipu.Config;

namespace Quipu.Services;

public enum InstanceState { Stopped, Starting, Ready, Stopping, Failed }

/// <summary>A running (or recently stopped) llama-server process for one model profile.</summary>
public sealed class ModelInstance
{
    private readonly object _logGate = new();
    private readonly LinkedList<LogLine> _log = new();
    private long _seq;

    public required string Name { get; init; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public InstanceState State { get; internal set; }
    public int? Pid { get; internal set; }
    public int Port { get; internal set; }
    public string Host { get; internal set; } = "127.0.0.1";
    public string? BuildId { get; internal set; }
    [JsonIgnore] public string? BuildDirectory { get; internal set; }
    public string? CommandLine { get; internal set; }
    public DateTimeOffset? StartedAt { get; internal set; }
    public DateTimeOffset? ReadyAt { get; internal set; }
    public DateTimeOffset? StoppedAt { get; internal set; }
    public int? ExitCode { get; internal set; }
    public string? Error { get; internal set; }
    public string? LogFile { get; internal set; }
    /// <summary>Idle unload (minutes) this instance was started with; null/0 = never.</summary>
    public int? IdleUnloadMinutes { get; internal set; }
    /// <summary>True while llama-server has unloaded the model after being idle. The next request reloads it.</summary>
    public bool Asleep { get; internal set; }
    public DateTimeOffset? AsleepSince { get; internal set; }
    /// <summary>Last time the server processed a request (or became ready / woke up).</summary>
    public DateTimeOffset? LastActivityAt { get; internal set; }
    /// <summary>When the model will be unloaded if no request arrives before then.</summary>
    public DateTimeOffset? UnloadAt =>
        State == InstanceState.Ready && !Asleep && IdleUnloadMinutes is > 0 && LastActivityAt is { } t ? t.AddMinutes(IdleUnloadMinutes.Value) : null;
    public string Url => $"http://{(Host is "0.0.0.0" or "::" ? "127.0.0.1" : Host)}:{Port}";
    [JsonIgnore] internal Process? Process { get; set; }
    [JsonIgnore] internal string? Fingerprint { get; set; }
    [JsonIgnore] internal bool StopRequested { get; set; }
    [JsonIgnore] internal StreamWriter? LogWriter { get; set; }

    /// <summary>Raised on sleep (true) / wake (false) transitions.</summary>
    [JsonIgnore] internal Action<ModelInstance, bool>? SleepChanged { get; set; }

    internal void Append(string line)
    {
        TrackActivity(line);
        lock (_logGate)
        {
            _log.AddLast(new LogLine(++_seq, DateTimeOffset.Now, line));
            while (_log.Count > 4000) _log.RemoveFirst();
            try { LogWriter?.WriteLine(line); } catch { /* disk issues shouldn't kill the server */ }
        }
    }

    private void TrackActivity(string line)
    {
        if (line.Contains("server is entering sleeping state", StringComparison.Ordinal))
        {
            Asleep = true;
            AsleepSince = DateTimeOffset.Now;
            SleepChanged?.Invoke(this, true);
        }
        else if (line.Contains("server is exiting sleeping state", StringComparison.Ordinal))
        {
            Asleep = false;
            AsleepSince = null;
            LastActivityAt = DateTimeOffset.Now;
            SleepChanged?.Invoke(this, false);
        }
        else if (line.Contains(" slot ", StringComparison.Ordinal))
        {
            LastActivityAt = DateTimeOffset.Now;
        }
    }

    public IReadOnlyList<LogLine> Logs(long since, int tail)
    {
        lock (_logGate)
        {
            var q = _log.Where(l => l.Seq > since);
            var list = q.ToList();
            return tail > 0 && list.Count > tail ? list.GetRange(list.Count - tail, tail) : list;
        }
    }
}

public sealed record LogLine(long Seq, DateTimeOffset Time, string Text);

public sealed class InstanceManager(
    ConfigStore config, BuildManager builds, HuggingFaceService hf, IHttpClientFactory httpFactory, ILogger<InstanceManager> log)
    : IDisposable
{
    private readonly ConcurrentDictionary<string, ModelInstance> _instances = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _startLock = new(1, 1);

    /// <summary>A model was unloaded for being idle (true) or reloaded by a request (false).</summary>
    public event Action<ModelInstance, bool>? SleepChanged;

    public IReadOnlyCollection<ModelInstance> All => _instances.Values.ToList();
    public ModelInstance? Get(string name) => _instances.GetValueOrDefault(name);

    /// <summary>The model's own build (profile "build") or else the active build.</summary>
    public InstalledBuild? BuildFor(ModelProfile m) => string.IsNullOrWhiteSpace(m.Build) ? builds.Active() : builds.Find(m.Build);

    public IEnumerable<string> BuildDirsInUse =>
        _instances.Values.Where(i => i.State is InstanceState.Starting or InstanceState.Ready && i.BuildDirectory is not null)
            .Select(i => i.BuildDirectory!);

    public bool IsRunning(string name) => Get(name) is { State: InstanceState.Starting or InstanceState.Ready };

    /// <summary>True when the profile/defaults/build changed since the instance was started.</summary>
    public bool NeedsRestart(string name)
    {
        var inst = Get(name);
        if (inst is not { State: InstanceState.Starting or InstanceState.Ready }) return false;
        if (!config.Current.Models.TryGetValue(name, out var m)) return true;
        return Fingerprint(config.Current, name, m, inst.Port, BuildFor(m)) != inst.Fingerprint;
    }

    /// <summary>The command a start would run right now (for previews).</summary>
    public (string Exe, List<string> Args) PreviewCommand(string name)
    {
        var cfg = config.Current;
        if (!cfg.Models.TryGetValue(name, out var m)) throw new KeyNotFoundException($"Model '{name}' is not configured");
        var build = BuildFor(m);
        var port = Get(name) is { State: InstanceState.Starting or InstanceState.Ready } i ? i.Port : (m.Port ?? 0);
        return (build?.ServerExe ?? "llama-server.exe", CommandBuilder.BuildArgs(cfg, name, m, port == 0 ? NextPortGuess(cfg) : port));
    }

    public async Task<ModelInstance> StartAsync(string name, CancellationToken ct = default)
    {
        await _startLock.WaitAsync(ct);
        try
        {
            if (Get(name) is { State: InstanceState.Starting or InstanceState.Ready } running) return running;

            var cfg = config.Current;
            if (!cfg.Models.TryGetValue(name, out var m)) throw new KeyNotFoundException($"Model '{name}' is not configured");
            var build = BuildFor(m) ?? throw new InvalidOperationException(string.IsNullOrWhiteSpace(m.Build)
                ? "No llama.cpp build installed yet. Install one first (POST /api/llamacpp/install)."
                : $"Build '{m.Build}' for model '{name}' is not installed. Run Apply, or POST /api/llamacpp/install with repo and tag.");
            var modelPath = ModelPaths.ResolveModel(cfg, m) ?? throw new InvalidOperationException($"Model '{name}' has no file");
            var missing = ModelPaths.AllParts(modelPath).Where(p => !File.Exists(p)).ToList();
            if (missing.Count > 0)
                throw new FileNotFoundException($"Model file missing: {missing[0]}." + (m.Hf is not null ? " Download it first (POST /api/models/" + name + "/download)." : ""));
            if (ModelPaths.ResolveMmproj(cfg, m) is { } mm && !File.Exists(mm))
                throw new FileNotFoundException($"mmproj file missing: {mm}");
            if (ModelPaths.ResolveDraft(cfg, m) is { } dr && ModelPaths.AllParts(dr).Any(p => !File.Exists(p)))
                throw new FileNotFoundException($"Draft model file missing: {dr}." + (m.Hf?.Draft is not null ? $" Download it first (POST /api/models/{name}/download)." : ""));

            await MakeRoomAsync(cfg, name, ct);

            var port = m.Port ?? FindFreePort(cfg);
            if (!IsPortFree(port)) throw new InvalidOperationException($"Port {port} is already in use by another program");

            var args = CommandBuilder.BuildArgs(cfg, name, m, port);
            var effective = CommandBuilder.Effective(cfg, m);
            var inst = new ModelInstance
            {
                Name = name,
                State = InstanceState.Starting,
                Port = port,
                Host = effective.Host ?? "127.0.0.1",
                BuildId = build.Id,
                BuildDirectory = build.Directory,
                CommandLine = CommandBuilder.Render(build.ServerExe, args),
                StartedAt = DateTimeOffset.Now,
                Fingerprint = Fingerprint(cfg, name, m, port, build),
                IdleUnloadMinutes = effective.IdleUnloadMinutes is > 0 ? effective.IdleUnloadMinutes : null,
                SleepChanged = (i, asleep) =>
                {
                    log.LogInformation(asleep ? "{Name} idle: model unloaded from VRAM (sleeping)" : "{Name} woke up: model reloaded", i.Name);
                    SleepChanged?.Invoke(i, asleep);
                },
            };

            var psi = new ProcessStartInfo(build.ServerExe)
            {
                WorkingDirectory = build.Directory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            // let llama.cpp's own -hf downloads land in the models dir and use the active token
            psi.Environment["LLAMA_CACHE"] = cfg.Paths.Models;
            if (hf.ActiveToken().Token is { } token) psi.Environment["HF_TOKEN"] = token;
            foreach (var (k, v) in effective.Env ?? []) psi.Environment[k] = v;

            var logDir = string.IsNullOrWhiteSpace(cfg.Paths.Logs) ? Path.Combine(cfg.Paths.Installs, "..", "logs") : cfg.Paths.Logs;
            Directory.CreateDirectory(logDir);
            inst.LogFile = Path.GetFullPath(Path.Combine(logDir, $"{name}.log"));
            inst.LogWriter = new StreamWriter(new FileStream(inst.LogFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
            inst.Append($"[quipu] {inst.CommandLine}");

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.OutputDataReceived += (_, e) => { if (e.Data is not null) inst.Append(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) inst.Append(e.Data); };
            proc.Exited += (_, _) => OnExited(inst);

            if (_instances.TryGetValue(name, out var previous)) previous.LogWriter?.Dispose();
            _instances[name] = inst;
            try
            {
                proc.Start();
            }
            catch (Exception ex)
            {
                inst.State = InstanceState.Failed;
                inst.Error = ex.Message;
                inst.LogWriter?.Dispose();
                throw;
            }
            JobObject.Assign(proc);
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            inst.Process = proc;
            inst.Pid = proc.Id;
            log.LogInformation("Started {Name} (pid {Pid}) on port {Port}", name, proc.Id, port);

            _ = Task.Run(() => WaitForReadyAsync(inst));
            return inst;
        }
        finally
        {
            _startLock.Release();
        }
    }

    /// <summary>Enforces server.maxActiveModels: stops the least recently used models so the new one fits.</summary>
    private async Task MakeRoomAsync(QuipuConfig cfg, string starting, CancellationToken ct)
    {
        var max = cfg.Server.MaxActiveModels;
        if (max <= 0) return;
        var others = _instances.Values
            .Where(i => i.State is InstanceState.Starting or InstanceState.Ready && !i.Name.Equals(starting, StringComparison.OrdinalIgnoreCase))
            .OrderBy(i => i.LastActivityAt ?? i.StartedAt)
            .ToList();
        foreach (var victim in others.Take(Math.Max(0, others.Count - (max - 1))))
        {
            log.LogInformation("Stopping {Victim} to start {Name} (server.maxActiveModels = {Max})", victim.Name, starting, max);
            victim.Append($"[quipu] stopped to make room for {starting} (server.maxActiveModels = {max})");
            ModelEvicted?.Invoke(victim, starting);
            await StopAsync(victim.Name, ct);
        }
    }

    /// <summary>A model was stopped to make room for another (victim, started model).</summary>
    public event Action<ModelInstance, string>? ModelEvicted;

    private async Task WaitForReadyAsync(ModelInstance inst)
    {
        var http = httpFactory.CreateClient("local");
        var deadline = DateTime.UtcNow.AddMinutes(30);
        while (inst.State == InstanceState.Starting && DateTime.UtcNow < deadline)
        {
            try
            {
                using var resp = await http.GetAsync($"{inst.Url}/health");
                if (resp.IsSuccessStatusCode)
                {
                    inst.State = InstanceState.Ready;
                    inst.ReadyAt = DateTimeOffset.Now;
                    inst.LastActivityAt = inst.ReadyAt;
                    inst.Append($"[quipu] ready after {(inst.ReadyAt - inst.StartedAt)?.TotalSeconds:F1}s at {inst.Url}");
                    return;
                }
            }
            catch
            {
                // not listening yet
            }
            await Task.Delay(500);
        }
    }

    private void OnExited(ModelInstance inst)
    {
        int? code = null;
        try { code = inst.Process?.ExitCode; } catch { }
        inst.ExitCode = code;
        inst.StoppedAt = DateTimeOffset.Now;
        inst.Asleep = false;
        inst.AsleepSince = null;
        if (inst.StopRequested)
        {
            inst.State = InstanceState.Stopped;
        }
        else
        {
            inst.State = InstanceState.Failed;
            inst.Error = $"llama-server exited unexpectedly (code {code}). See logs.";
        }
        inst.Append($"[quipu] process exited with code {code}");
        // give the async stdout readers a moment to flush before closing the file
        Task.Delay(1000).ContinueWith(_ => inst.LogWriter?.Dispose());
        log.LogInformation("{Name} exited with code {Code}", inst.Name, code);
    }

    public async Task<ModelInstance?> StopAsync(string name, CancellationToken ct = default)
    {
        var inst = Get(name);
        if (inst?.Process is null || inst.State is InstanceState.Stopped or InstanceState.Failed) return inst;
        inst.StopRequested = true;
        inst.State = InstanceState.Stopping;
        try
        {
            if (!inst.Process.HasExited) inst.Process.Kill(entireProcessTree: true);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await inst.Process.WaitForExitAsync(timeout.Token);
        }
        catch (InvalidOperationException)
        {
            // already gone
        }
        inst.State = InstanceState.Stopped;
        return inst;
    }

    public async Task<ModelInstance> RestartAsync(string name, CancellationToken ct = default)
    {
        await StopAsync(name, ct);
        return await StartAsync(name, ct);
    }

    public async Task StopAllAsync()
    {
        await Task.WhenAll(_instances.Keys.Select(n => StopAsync(n)));
    }

    /// <summary>Forget a stopped instance (e.g. after its profile was deleted).</summary>
    public void Forget(string name)
    {
        if (_instances.TryGetValue(name, out var i) && i.State is InstanceState.Stopped or InstanceState.Failed)
            _instances.TryRemove(name, out _);
    }

    // ---------------------------------------------------------------- ports

    private int FindFreePort(QuipuConfig cfg)
    {
        var reserved = cfg.Models.Values.Where(m => m.Port is not null).Select(m => m.Port!.Value)
            .Concat(_instances.Values.Where(i => i.State is InstanceState.Starting or InstanceState.Ready).Select(i => i.Port))
            .ToHashSet();
        for (var p = cfg.Server.ModelPortStart; p < cfg.Server.ModelPortStart + 1000; p++)
            if (!reserved.Contains(p) && IsPortFree(p)) return p;
        throw new InvalidOperationException("No free port found");
    }

    private int NextPortGuess(QuipuConfig cfg)
    {
        try { return FindFreePort(cfg); } catch { return cfg.Server.ModelPortStart; }
    }

    private static bool IsPortFree(int port)
    {
        try
        {
            using var l = new TcpListener(IPAddress.Loopback, port);
            l.Start();
            l.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static string Fingerprint(QuipuConfig cfg, string name, ModelProfile m, int port, InstalledBuild? build) =>
        string.Join('\u001f', CommandBuilder.BuildArgs(cfg, name, m, m.Port ?? port))
        + '\u001e' + string.Join(';', (CommandBuilder.Effective(cfg, m).Env ?? []).OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"))
        + '\u001e' + build?.Id;

    public void Dispose()
    {
        foreach (var i in _instances.Values)
        {
            try { if (i.Process is { HasExited: false } p) p.Kill(true); } catch { }
        }
    }
}
