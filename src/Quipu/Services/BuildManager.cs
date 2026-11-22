using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using Quipu.Config;

namespace Quipu.Services;

/// <summary>
/// Finds llama.cpp Windows releases on GitHub, installs them side by side
/// ({installs}\b12617-cuda13.5\...) and decides which build is active.
/// </summary>
public sealed partial class BuildManager(
    IHttpClientFactory httpFactory, ConfigStore config, JobService jobs, GpuService gpu, ILogger<BuildManager> log)
{
    public const string GitHubClient = "github";
    public const string DownloadClient = "download";
    private const string ManifestName = "quipu-build.json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly SemaphoreSlim _releaseLock = new(1, 1);
    private readonly Dictionary<string, (DateTime At, List<GhRelease> Releases)> _releaseCache = new(StringComparer.OrdinalIgnoreCase);

    private string DefaultRepo => config.Current.LlamaCpp.Repo;
    private bool IsDefaultRepo(string? repo) => repo is null || repo.Equals(DefaultRepo, StringComparison.OrdinalIgnoreCase);

    public string EffectiveBackend(string? requested = null)
    {
        var b = (requested ?? config.Current.LlamaCpp.Backend).ToLowerInvariant();
        return b == "auto" ? gpu.AutoBackend : b;
    }

    // ---------------------------------------------------------------- remote releases

    public async Task<IReadOnlyList<ReleaseInfo>> ListReleasesAsync(string? backend, bool refresh, CancellationToken ct, string? repo = null)
    {
        repo ??= DefaultRepo;
        var effective = EffectiveBackend(backend);
        var releases = await FetchReleasesAsync(repo, refresh, ct);
        var installed = ListInstalled();
        var result = new List<ReleaseInfo>();
        foreach (var r in releases)
        {
            var pick = PickAssets(r, effective);
            if (pick is null) continue;
            result.Add(new ReleaseInfo(r.TagName, r.Name, r.PublishedAt, r.Prerelease, r.HtmlUrl, effective,
                pick.Value.CudaVersion, pick.Value.Main.Name, pick.Value.Main.Size + (pick.Value.Runtime?.Size ?? 0),
                installed.Any(b => b.Tag == r.TagName && b.Backend == effective && b.CudaVersion == pick.Value.CudaVersion
                                   && string.Equals(b.Repo, repo, StringComparison.OrdinalIgnoreCase)),
                repo));
        }
        return result;
    }

    public async Task<ReleaseInfo?> LatestReleaseAsync(string? backend, bool refresh, CancellationToken ct) =>
        (await ListReleasesAsync(backend, refresh, ct)).FirstOrDefault();

    private async Task<List<GhRelease>> FetchReleasesAsync(string repo, bool refresh, CancellationToken ct)
    {
        await _releaseLock.WaitAsync(ct);
        try
        {
            if (!refresh && _releaseCache.TryGetValue(repo, out var c) && DateTime.UtcNow - c.At < TimeSpan.FromMinutes(10))
                return c.Releases;
            var http = httpFactory.CreateClient(GitHubClient);
            // llama.cpp publishes its Windows binaries on the per-build (bNNNNN) releases, which GitHub marks as
            // pre-releases, so /releases/latest is not enough — list recent releases and pick the newest that has a Windows asset.
            using var resp = await http.GetAsync($"https://api.github.com/repos/{repo}/releases?per_page=40", ct);
            if ((int)resp.StatusCode == 403 && resp.Headers.Contains("X-RateLimit-Remaining"))
                throw new InvalidOperationException("GitHub API rate limit reached. Try again later or set a GITHUB_TOKEN environment variable.");
            resp.EnsureSuccessStatusCode();
            var releases = JsonSerializer.Deserialize<List<GhRelease>>(await resp.Content.ReadAsStringAsync(ct), GhJson) ?? [];
            releases = releases.Where(r => !r.Draft).OrderByDescending(r => r.PublishedAt).ToList();
            _releaseCache[repo] = (DateTime.UtcNow, releases);
            return releases;
        }
        finally
        {
            _releaseLock.Release();
        }
    }

    private PickedAssets? PickAssets(GhRelease r, string backend) =>
        AssetPicker.Pick(r.TagName, r.Assets, backend, config.Current.LlamaCpp.CudaVersion);

    // ---------------------------------------------------------------- installed builds

    public IReadOnlyList<InstalledBuild> ListInstalled()
    {
        var root = config.Current.Paths.Installs;
        if (!Directory.Exists(root)) return [];
        var list = new List<InstalledBuild>();
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var manifest = Path.Combine(dir, ManifestName);
            if (!File.Exists(manifest) || !File.Exists(Path.Combine(dir, "llama-server.exe"))) continue;
            try
            {
                var m = JsonSerializer.Deserialize<BuildManifest>(File.ReadAllText(manifest), Json)!;
                list.Add(new InstalledBuild(Path.GetFileName(dir), m.Tag, m.Backend, m.CudaVersion, dir, m.PublishedAt, m.InstalledAt, false,
                    m.Repo ?? DefaultRepo));
            }
            catch (Exception ex)
            {
                log.LogWarning("Ignoring unreadable build manifest {Path}: {Error}", manifest, ex.Message);
            }
        }
        var active = ResolveActive(list);
        return list.Select(b => b with { Active = active?.Id == b.Id })
            .OrderByDescending(b => TagNumber(b.Tag)).ThenByDescending(b => b.PublishedAt).ToList();
    }

    public InstalledBuild? Active() => ListInstalled().FirstOrDefault(b => b.Active);

    private InstalledBuild? ResolveActive(List<InstalledBuild> builds)
    {
        if (builds.Count == 0) return null;
        var backend = EffectiveBackend();
        var version = config.Current.LlamaCpp.Version;
        var ordered = builds.OrderByDescending(b => b.Backend == backend).ThenByDescending(b => TagNumber(b.Tag)).ThenByDescending(b => b.PublishedAt).ToList();
        if (!version.Equals("latest", StringComparison.OrdinalIgnoreCase)) return BuildSpec.Match(ordered, version, backend);
        // "latest" only follows the main repo; fork builds (e.g. Unsloth's) are used when pinned or per model
        return ordered.FirstOrDefault(b => IsDefaultRepo(b.Repo));
    }

    /// <summary>Resolves a build spec: "owner/repo@tag", an installed build id, or a tag.</summary>
    public InstalledBuild? Find(string spec) => BuildSpec.Match(ListInstalled(), spec, EffectiveBackend());

    /// <summary>Pins the given build in quipu.yaml (llamaCpp.version + backend). Fork builds are pinned as
    /// owner/repo@tag, so Apply can reinstall them on another machine.</summary>
    public InstalledBuild Activate(string idOrTag)
    {
        var build = ListInstalled().FirstOrDefault(b => b.Id.Equals(idOrTag, StringComparison.OrdinalIgnoreCase))
                    ?? ListInstalled().FirstOrDefault(b => b.Tag.Equals(idOrTag, StringComparison.OrdinalIgnoreCase))
                    ?? throw new KeyNotFoundException($"Build '{idOrTag}' is not installed");
        config.Update(c =>
        {
            c.LlamaCpp.Version = IsDefaultRepo(build.Repo) ? build.Id : build.Spec;
            if (c.LlamaCpp.Backend != "auto" || EffectiveBackend() != build.Backend) c.LlamaCpp.Backend = build.Backend;
        });
        return Active()!;
    }

    public void Delete(string id, IEnumerable<string> inUseDirs)
    {
        var build = ListInstalled().FirstOrDefault(b => b.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                    ?? throw new KeyNotFoundException($"Build '{id}' is not installed");
        if (inUseDirs.Contains(build.Directory, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Build {id} is used by a running model; stop it first");
        if (build.Active && !config.Current.LlamaCpp.Version.Equals("latest", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Build {id} is pinned in quipu.yaml; activate another build first");
        Directory.Delete(build.Directory, recursive: true);
    }

    // ---------------------------------------------------------------- install

    /// <summary>Starts an install job. tag = null installs the newest release with a matching asset.</summary>
    public Job Install(string? tag, string? backend, bool pin, IEnumerable<string>? inUseDirs = null, string? repo = null)
    {
        var effective = EffectiveBackend(backend);
        repo = string.IsNullOrWhiteSpace(repo) ? DefaultRepo : repo.Trim();
        var title = $"Install {(IsDefaultRepo(repo) ? "llama.cpp" : repo)} {tag ?? "latest"} ({effective})";
        return jobs.Start("install", title, async (job, ct) =>
        {
            job.Message = "Querying GitHub releases…";
            var releases = await FetchReleasesAsync(repo, refresh: tag is null, ct);
            var release = tag is null
                ? releases.FirstOrDefault(r => PickAssets(r, effective) is not null)
                : releases.FirstOrDefault(r => r.TagName.Equals(tag, StringComparison.OrdinalIgnoreCase))
                  ?? await FetchReleaseByTagAsync(repo, tag, ct);
            if (release is null) throw new InvalidOperationException($"No release with a Windows {effective} build found in {repo}");
            var pick = PickAssets(release, effective)
                       ?? throw new InvalidOperationException($"Release {release.TagName} of {repo} has no Windows {effective} asset");

            var owner = IsDefaultRepo(repo) ? "" : repo.Split('/')[0] + "-";
            var id = $"{owner}{release.TagName}-{effective}{(pick.CudaVersion is null ? "" : pick.CudaVersion)}";
            var installs = config.Current.Paths.Installs;
            var target = Path.Combine(installs, id);
            if (File.Exists(Path.Combine(target, "llama-server.exe")) && File.Exists(Path.Combine(target, ManifestName)))
            {
                job.Message = $"{id} already installed";
                if (pin) Activate(id);
                return;
            }

            var downloads = Path.Combine(installs, "_downloads");
            var http = httpFactory.CreateClient(DownloadClient);
            var assets = new[] { pick.Main, pick.Runtime }.Where(a => a is not null).Cast<GhAsset>().ToList();
            job.BytesTotal = assets.Sum(a => a.Size);
            var staging = target + ".staging";
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            Directory.CreateDirectory(staging);

            foreach (var asset in assets)
            {
                job.Message = $"Downloading {asset.Name}";
                var zip = Path.Combine(downloads, asset.Name);
                await Downloader.DownloadAsync(http, asset.BrowserDownloadUrl, zip, job, null, ct, asset.Size, totalKnown: true);
                job.Message = $"Extracting {asset.Name}";
                await Task.Run(() => ZipFile.ExtractToDirectory(zip, staging, overwriteFiles: true), ct);
                File.Delete(zip);
            }

            // some archives wrap everything in a single folder — flatten it
            if (!File.Exists(Path.Combine(staging, "llama-server.exe")))
            {
                var nested = Directory.EnumerateFiles(staging, "llama-server.exe", SearchOption.AllDirectories).FirstOrDefault()
                             ?? throw new InvalidOperationException("llama-server.exe not found in the downloaded archive");
                var inner = Path.GetDirectoryName(nested)!;
                foreach (var f in Directory.EnumerateFileSystemEntries(inner))
                {
                    var dest = Path.Combine(staging, Path.GetFileName(f));
                    if (Directory.Exists(f)) Directory.Move(f, dest); else File.Move(f, dest, true);
                }
            }

            // Some CUDA builds (e.g. Unsloth's "portable" bundles) don't ship the CUDA runtime, so ggml-cuda.dll
            // fails to load and llama-server silently falls back to CPU. Add ggml-org's matching-major runtime package.
            if (effective == "cuda" && !Directory.EnumerateFiles(staging, "cudart64_*.dll").Any())
            {
                job.Message = "Build has no CUDA runtime; fetching cudart from ggml-org/llama.cpp…";
                var runtime = await FindCudaRuntimeAsync(pick.CudaVersion, ct)
                              ?? throw new InvalidOperationException($"Build has no CUDA runtime DLLs and no CUDA {pick.CudaVersion} runtime package was found");
                job.BytesTotal += runtime.Size;
                var zip = Path.Combine(downloads, runtime.Name);
                await Downloader.DownloadAsync(http, runtime.BrowserDownloadUrl, zip, job, null, ct, runtime.Size, totalKnown: true);
                await Task.Run(() => ZipFile.ExtractToDirectory(zip, staging, overwriteFiles: false), ct);
                File.Delete(zip);
                assets.Add(runtime);
            }

            var manifest = new BuildManifest(release.TagName, effective, pick.CudaVersion, release.PublishedAt, DateTimeOffset.Now,
                assets.Select(a => a.Name).ToList(), release.HtmlUrl, repo);
            await File.WriteAllTextAsync(Path.Combine(staging, ManifestName), JsonSerializer.Serialize(manifest, Json), ct);
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.Move(staging, target);
            log.LogInformation("Installed llama.cpp {Id} into {Dir}", id, target);

            if (pin) Activate(id);
            Prune(inUseDirs ?? []);
            job.Message = $"Installed {id}";
        }, key: $"install:{repo}:{tag ?? "latest"}:{effective}");
    }

    /// <summary>ggml-org's "cudart-llama-bin-win-cuda-X.Y-x64.zip" with the same CUDA major version (highest minor).</summary>
    private async Task<GhAsset?> FindCudaRuntimeAsync(string? cudaVersion, CancellationToken ct)
    {
        var major = (cudaVersion ?? "").Split('.')[0];
        var releases = await FetchReleasesAsync("ggml-org/llama.cpp", refresh: false, ct);
        foreach (var r in releases)
        {
            var best = r.Assets
                .Select(a => (Asset: a, M: Regex.Match(a.Name, @"^cudart-llama-(?:b\d+-)?bin-win-cuda-(?<v>(?<maj>\d+)\.\d+)-x64\.zip$", RegexOptions.IgnoreCase)))
                .Where(x => x.M.Success && (major.Length == 0 || x.M.Groups["maj"].Value == major))
                .OrderByDescending(x => Version.Parse(x.M.Groups["v"].Value))
                .Select(x => x.Asset).FirstOrDefault();
            if (best is not null) return best;
        }
        return null;
    }

    private async Task<GhRelease?> FetchReleaseByTagAsync(string repo, string tag, CancellationToken ct)
    {
        var http = httpFactory.CreateClient(GitHubClient);
        using var resp = await http.GetAsync($"https://api.github.com/repos/{repo}/releases/tags/{Uri.EscapeDataString(tag)}", ct);
        if (!resp.IsSuccessStatusCode) return null;
        return JsonSerializer.Deserialize<GhRelease>(await resp.Content.ReadAsStringAsync(ct), GhJson);
    }

    /// <summary>Keeps the newest N builds per backend; never removes the active build or one in use.</summary>
    private void Prune(IEnumerable<string> inUseDirs)
    {
        var keep = config.Current.LlamaCpp.KeepBuilds;
        if (keep <= 0) return;
        var inUse = inUseDirs.ToHashSet(StringComparer.OrdinalIgnoreCase);
        // builds pinned by a model profile are never pruned
        var pinnedByModels = config.Current.Models.Values.Where(m => !string.IsNullOrWhiteSpace(m.Build))
            .Select(m => Find(m.Build!)?.Id).Where(id => id is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var group in ListInstalled().GroupBy(b => (b.Repo, b.Backend)))
        foreach (var old in group.Skip(keep).Where(b => !b.Active && !inUse.Contains(b.Directory) && !pinnedByModels.Contains(b.Id)))
        {
            try
            {
                Directory.Delete(old.Directory, true);
                log.LogInformation("Pruned old build {Id}", old.Id);
            }
            catch (Exception ex)
            {
                log.LogWarning("Could not prune {Id}: {Error}", old.Id, ex.Message);
            }
        }
    }

    public static long TagNumber(string tag) =>
        TagNum().Match(tag) is { Success: true } m && long.TryParse(m.Groups[1].Value, out var n) ? n : 0;

    [GeneratedRegex(@"^b(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex TagNum();

    private static readonly JsonSerializerOptions GhJson = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private sealed record GhRelease(string TagName, string? Name, DateTimeOffset PublishedAt, bool Prerelease, bool Draft, string HtmlUrl, List<GhAsset> Assets);
    private sealed record BuildManifest(string Tag, string Backend, string? CudaVersion, DateTimeOffset PublishedAt, DateTimeOffset InstalledAt, List<string> Assets, string? ReleaseUrl, string? Repo = null);
}

public sealed record ReleaseInfo(string Tag, string? Name, DateTimeOffset PublishedAt, bool Prerelease, string Url,
    string Backend, string? CudaVersion, string Asset, long DownloadSize, bool Installed, string Repo);
