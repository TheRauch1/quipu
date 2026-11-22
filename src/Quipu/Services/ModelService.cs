using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Quipu.Config;

namespace Quipu.Services;

/// <summary>Model profiles (from config) joined with what's on disk, what's running and what's downloading.</summary>
public sealed partial class ModelService(
    ConfigStore config, InstanceManager instances, HuggingFaceService hf, JobService jobs, GpuService gpu,
    IHttpClientFactory httpFactory, ILogger<ModelService> log)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public IReadOnlyList<ModelView> List()
    {
        var cfg = config.Current;
        var snapshot = gpu.Query();
        return cfg.Models.OrderBy(kv => kv.Key).Select(kv => View(cfg, kv.Key, kv.Value, snapshot)).ToList();
    }

    public ModelView Get(string name)
    {
        var cfg = config.Current;
        if (!cfg.Models.TryGetValue(name, out var m)) throw new KeyNotFoundException($"Model '{name}' is not configured");
        return View(cfg, name, m, gpu.Query());
    }

    private ModelView View(QuipuConfig cfg, string name, ModelProfile m, GpuSnapshot snapshot)
    {
        var path = ModelPaths.ResolveModel(cfg, m);
        var parts = path is null ? [] : ModelPaths.AllParts(path);
        var exists = parts.Count > 0 && parts.All(File.Exists)
                     && ModelPaths.AllFiles(cfg, m).SelectMany(ModelPaths.AllParts).All(File.Exists);
        long? size = exists ? parts.Sum(p => new FileInfo(p).Length) : null;
        var inst = instances.Get(name);
        long? vram = inst?.Pid is { } pid && snapshot.VramByPid.TryGetValue(pid, out var v) ? v : null;
        var job = jobs.FindActive($"download:{name}");
        return new ModelView(
            name, m, path, exists, size, HuggingFaceService.Quant(path ?? m.Hf?.File ?? ""),
            inst?.State.ToString() ?? nameof(InstanceState.Stopped),
            inst is { State: InstanceState.Starting or InstanceState.Ready } ? inst.Port : m.Port,
            inst is { State: InstanceState.Starting or InstanceState.Ready } ? inst.Url : null,
            inst?.Pid, vram, instances.NeedsRestart(name), job?.Id, inst?.Error, inst?.BuildId, inst?.StartedAt, inst?.ReadyAt,
            inst is { State: InstanceState.Ready, Asleep: true }, inst?.AsleepSince, inst?.LastActivityAt, inst?.UnloadAt,
            CommandBuilder.Effective(cfg, m).IdleUnloadMinutes is > 0 and var idle ? idle : null);
    }

    // ---------------------------------------------------------------- profiles

    public ModelView Upsert(string name, ModelProfile profile)
    {
        if (!ConfigStore.IsValidModelName(name)) throw new ArgumentException("Name may only contain letters, digits, '.', '_' and '-'");
        config.Update(c => c.Models[name] = profile);
        return Get(name);
    }

    /// <summary>JSON merge-patch onto an existing profile (null removes a field).</summary>
    public ModelView Patch(string name, JsonObject patch)
    {
        config.Update(c =>
        {
            if (!c.Models.TryGetValue(name, out var existing)) throw new KeyNotFoundException($"Model '{name}' is not configured");
            var node = JsonSerializer.SerializeToNode(existing, Json)!.AsObject();
            ConfigStore.MergePatch(node, patch);
            c.Models[name] = node.Deserialize<ModelProfile>(Json)!;
        });
        return Get(name);
    }

    public ModelView Rename(string name, string newName)
    {
        if (!ConfigStore.IsValidModelName(newName)) throw new ArgumentException("Invalid name");
        if (instances.IsRunning(name)) throw new InvalidOperationException("Stop the model before renaming it");
        config.Update(c =>
        {
            if (!c.Models.Remove(name, out var p)) throw new KeyNotFoundException($"Model '{name}' is not configured");
            if (c.Models.ContainsKey(newName)) throw new InvalidOperationException($"'{newName}' already exists");
            c.Models[newName] = p;
        });
        instances.Forget(name);
        return Get(newName);
    }

    public async Task<IReadOnlyList<string>> DeleteAsync(string name, bool deleteFiles)
    {
        var cfg = config.Current;
        if (!cfg.Models.TryGetValue(name, out var m)) throw new KeyNotFoundException($"Model '{name}' is not configured");
        await instances.StopAsync(name);
        var deleted = new List<string>();
        if (deleteFiles)
        {
            var others = cfg.Models.Where(kv => !kv.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
                .SelectMany(kv => ModelPaths.AllFiles(cfg, kv.Value))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var files = ModelPaths.AllFiles(cfg, m).Where(p => !others.Contains(p)).SelectMany(ModelPaths.AllParts);
            foreach (var f in files.Where(File.Exists))
            {
                File.Delete(f);
                deleted.Add(f);
            }
        }
        config.Update(c => c.Models.Remove(name));
        instances.Forget(name);
        return deleted;
    }

    // ---------------------------------------------------------------- downloads

    /// <summary>Creates (or updates) a profile pointing at a Hugging Face GGUF and downloads it.</summary>
    public async Task<(ModelView Model, Job Job)> AddFromHuggingFaceAsync(string repo, string file, string? name, string? revision,
        string? mmproj, ModelProfile? overrides, CancellationToken ct, string? draft = null)
    {
        var found = await hf.FindAsync(repo, file, revision, ct); // validates the file exists (and access)
        if (draft is not null) await hf.FindAsync(repo, draft, revision, ct);
        name ??= SuggestName(found.File);
        if (!ConfigStore.IsValidModelName(name)) throw new ArgumentException($"Invalid model name '{name}'");
        var profile = overrides ?? new ModelProfile();
        profile.Hf = new HfSource { Repo = repo, File = found.File, Revision = revision, Mmproj = mmproj, Draft = draft };
        profile.File ??= ModelPaths.HfRelative(repo, found.File);
        if (mmproj is not null) profile.Mmproj ??= ModelPaths.HfRelative(repo, mmproj);
        if (draft is not null) profile.Draft ??= ModelPaths.HfRelative(repo, draft);
        config.Update(c => c.Models[name] = profile);
        var job = StartDownload(name);
        return (Get(name), job);
    }

    /// <summary>Downloads the model's Hugging Face source (all split parts + mmproj) into the models directory.</summary>
    public Job StartDownload(string name)
    {
        var cfg = config.Current;
        if (!cfg.Models.TryGetValue(name, out var m)) throw new KeyNotFoundException($"Model '{name}' is not configured");
        if (m.Hf is null) throw new InvalidOperationException($"Model '{name}' has no 'hf' source to download from");
        var src = m.Hf;
        return jobs.Start("download", $"Download {name} ({src.File})", async (job, ct) =>
        {
            job.Message = "Resolving files on Hugging Face…";
            var main = await hf.FindAsync(src.Repo, src.File, src.Revision, ct);
            var items = new List<(HfPart Part, string Dest)>();
            var dest = ModelPaths.ResolveModel(cfg, m)!;
            var destDir = Path.GetDirectoryName(dest)!;
            foreach (var part in main.Parts) items.Add((part, Path.Combine(destDir, Path.GetFileName(part.Path))));
            if (src.Mmproj is not null)
            {
                var mm = await hf.FindAsync(src.Repo, src.Mmproj, src.Revision, ct);
                var mmDest = ModelPaths.ResolveMmproj(cfg, m)!;
                items.AddRange(mm.Parts.Select(p => (p, mmDest)));
            }
            if (src.Draft is not null)
            {
                var dr = await hf.FindAsync(src.Repo, src.Draft, src.Revision, ct);
                var drDir = Path.GetDirectoryName(ModelPaths.ResolveDraft(cfg, m)!)!;
                items.AddRange(dr.Parts.Select(p => (p, Path.Combine(drDir, Path.GetFileName(p.Path)))));
            }

            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(destDir))!);
            var needed = items.Where(i => !File.Exists(i.Dest)).Sum(i => i.Part.Size);
            if (drive.IsReady && drive.AvailableFreeSpace < needed + (1L << 30))
                throw new IOException($"Not enough space on {drive.Name}: need {needed / (1L << 30)} GiB, have {drive.AvailableFreeSpace / (1L << 30)} GiB");

            job.BytesTotal = items.Sum(i => i.Part.Size);
            var http = httpFactory.CreateClient(BuildManager.DownloadClient);
            var token = hf.ActiveToken().Token;
            foreach (var (part, path) in items)
            {
                job.Message = $"Downloading {Path.GetFileName(part.Path)}";
                await Downloader.DownloadAsync(http, hf.DownloadUrl(src.Repo, src.Revision, part.Path), path, job, token, ct,
                    part.Size > 0 ? part.Size : null, totalKnown: true);
            }
            job.Message = $"Downloaded {items.Count} file(s) to {destDir}";
            log.LogInformation("Downloaded {Name} to {Dir}", name, destDir);
        }, key: $"download:{name}");
    }

    // ---------------------------------------------------------------- disk

    /// <summary>Every GGUF under the models directory, with the profiles that use it.</summary>
    public IReadOnlyList<DiskModel> ScanDisk()
    {
        var cfg = config.Current;
        var root = cfg.Paths.Models;
        if (!Directory.Exists(root)) return [];
        var used = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, m) in cfg.Models)
        foreach (var p in ModelPaths.AllFiles(cfg, m))
            {
                var key = Path.GetFullPath(p);
                (used.TryGetValue(key, out var l) ? l : used[key] = []).Add(name);
            }

        var result = new List<DiskModel>();
        foreach (var file in Directory.EnumerateFiles(root, "*.gguf", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
        {
            if (SplitNotFirst().IsMatch(file)) continue;
            var parts = ModelPaths.AllParts(file);
            var size = parts.Where(File.Exists).Sum(p => new FileInfo(p).Length);
            result.Add(new DiskModel(ModelPaths.Relativize(cfg, file), file, size, HuggingFaceService.Quant(file),
                Path.GetFileName(file).Contains("mmproj", StringComparison.OrdinalIgnoreCase),
                parts.Count, parts.All(File.Exists), used.GetValueOrDefault(Path.GetFullPath(file)) ?? [], File.GetLastWriteTime(file)));
        }
        return result.OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public DiskUsage Disk()
    {
        var root = config.Current.Paths.Models;
        try
        {
            var d = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root))!);
            return new DiskUsage(root, d.Name, d.TotalSize, d.AvailableFreeSpace, ScanDisk().Sum(m => m.Size));
        }
        catch
        {
            return new DiskUsage(root, "?", 0, 0, 0);
        }
    }

    /// <summary>"Qwen3-8B-Q4_K_M.gguf" -> "qwen3-8b-q4_k_m".</summary>
    public static string SuggestName(string file)
    {
        var n = Path.GetFileNameWithoutExtension(file.Split('/').Last());
        n = Regex.Replace(n, @"-\d{5}-of-\d{5}$", "");
        n = Regex.Replace(n.ToLowerInvariant(), @"[^a-z0-9._-]+", "-").Trim('-');
        return n.Length > 64 ? n[..64] : n;
    }

    [GeneratedRegex(@"-(?!00001)\d{5}-of-\d{5}\.gguf$", RegexOptions.IgnoreCase)]
    private static partial Regex SplitNotFirst();
}

public sealed record ModelView(
    string Name, ModelProfile Profile, string? Path, bool Downloaded, long? SizeBytes, string? Quant,
    string State, int? Port, string? Url, int? Pid, long? VramMiB, bool NeedsRestart, string? DownloadJobId,
    string? Error, string? BuildId, DateTimeOffset? StartedAt, DateTimeOffset? ReadyAt,
    bool Asleep, DateTimeOffset? AsleepSince, DateTimeOffset? LastActivityAt, DateTimeOffset? UnloadAt, int? IdleUnloadMinutes);

public sealed record DiskModel(string Path, string FullPath, long Size, string? Quant, bool IsMmproj, int Parts,
    bool Complete, IReadOnlyList<string> UsedBy, DateTime Modified);

public sealed record DiskUsage(string ModelsDir, string Drive, long TotalBytes, long FreeBytes, long ModelBytes);
