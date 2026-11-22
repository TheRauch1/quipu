using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Quipu.Config;

namespace Quipu.Services;

public sealed partial class HuggingFaceService(IHttpClientFactory httpFactory, ConfigStore config, SecretStore secrets)
{
    public const string HttpClientName = "hf";

    private string Endpoint => config.Current.HuggingFace.Endpoint.TrimEnd('/');

    /// <summary>The token in use: the active stored token, else the HF_TOKEN environment variable.</summary>
    public (string? Token, string Source) ActiveToken()
    {
        var name = config.Current.HuggingFace.ActiveToken;
        if (name is not null && secrets.Get(name) is { Length: > 0 } t) return (t, $"stored:{name}");
        var env = Environment.GetEnvironmentVariable("HF_TOKEN");
        if (!string.IsNullOrWhiteSpace(env)) return (env, "env:HF_TOKEN");
        return (null, "none");
    }

    public async Task<WhoAmI> WhoAmIAsync(string token, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{Endpoint}/api/whoami-v2");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var resp = await Http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return new WhoAmI(false, null, null, $"HTTP {(int)resp.StatusCode}");
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var name = root.TryGetProperty("name", out var n) ? n.GetString() : null;
        string? role = null;
        if (root.TryGetProperty("auth", out var auth) && auth.TryGetProperty("accessToken", out var at))
            role = at.TryGetProperty("role", out var r) ? r.GetString() : null;
        return new WhoAmI(true, name, role, null);
    }

    public async Task<IReadOnlyList<HfRepo>> SearchAsync(string query, int limit, CancellationToken ct)
    {
        var url = $"{Endpoint}/api/models?search={Uri.EscapeDataString(query)}&filter=gguf&sort=downloads&direction=-1&limit={Math.Clamp(limit, 1, 100)}" +
                  "&expand[]=downloads&expand[]=likes&expand[]=lastModified&expand[]=gated&expand[]=pipeline_tag";
        using var req = Authorized(HttpMethod.Get, url);
        using var resp = await Http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.EnumerateArray().Select(e => new HfRepo(
            e.GetProperty("id").GetString()!,
            e.TryGetProperty("downloads", out var d) ? d.GetInt64() : 0,
            e.TryGetProperty("likes", out var l) ? l.GetInt64() : 0,
            e.TryGetProperty("lastModified", out var lm) ? lm.GetString() : null,
            e.TryGetProperty("gated", out var g) && g.ValueKind != JsonValueKind.False,
            e.TryGetProperty("pipeline_tag", out var pt) ? pt.GetString() : null)).ToList();
    }

    /// <summary>GGUF files in a repo; split files (-00001-of-00003) are grouped into one entry.</summary>
    public async Task<IReadOnlyList<HfFile>> ListGgufAsync(string repo, string? revision, CancellationToken ct)
    {
        var rev = string.IsNullOrWhiteSpace(revision) ? "main" : revision;
        var url = $"{Endpoint}/api/models/{repo}/tree/{Uri.EscapeDataString(rev)}?recursive=true";
        using var req = Authorized(HttpMethod.Get, url);
        using var resp = await Http.SendAsync(req, ct);
        if (resp.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            throw new InvalidOperationException($"No access to {repo}. Is it private or gated? Set a Hugging Face token.");
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new KeyNotFoundException($"Repository {repo}@{rev} not found");
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var files = doc.RootElement.EnumerateArray()
            .Where(e => e.GetProperty("type").GetString() == "file")
            .Select(e => (Path: e.GetProperty("path").GetString()!, Size: FileSize(e)))
            .Where(f => f.Path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var result = new List<HfFile>();
        foreach (var g in files.GroupBy(f => SplitPart().Replace(f.Path, ".gguf")))
        {
            var parts = g.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
            var first = parts[0].Path;
            var isMmproj = first.Contains("mmproj", StringComparison.OrdinalIgnoreCase);
            result.Add(new HfFile(first, parts.Select(p => new HfPart(p.Path, p.Size)).ToList(), parts.Sum(p => p.Size),
                Quant(first), isMmproj));
        }
        // main model files first (repo root before sub-folders such as MTP/ draft heads), then by size
        return result.OrderBy(f => f.IsMmproj).ThenBy(f => f.File.Contains('/')).ThenBy(f => f.Size).ToList();
    }

    /// <summary>Finds a GGUF (any part of a split file matches) in a repo.</summary>
    public async Task<HfFile> FindAsync(string repo, string file, string? revision, CancellationToken ct)
    {
        var all = await ListGgufAsync(repo, revision, ct);
        return all.FirstOrDefault(f => f.Parts.Any(p => p.Path.Equals(file, StringComparison.OrdinalIgnoreCase)))
               ?? throw new KeyNotFoundException($"{file} not found in {repo}. Available: {string.Join(", ", all.Select(f => f.File).Take(20))}");
    }

    public string DownloadUrl(string repo, string? revision, string path) =>
        $"{Endpoint}/{repo}/resolve/{Uri.EscapeDataString(string.IsNullOrWhiteSpace(revision) ? "main" : revision)}/" +
        string.Join('/', path.Split('/').Select(Uri.EscapeDataString)) + "?download=true";

    private HttpClient Http => httpFactory.CreateClient(HttpClientName);

    private HttpRequestMessage Authorized(HttpMethod method, string url)
    {
        var req = new HttpRequestMessage(method, url);
        if (ActiveToken().Token is { } t) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", t);
        return req;
    }

    private static long FileSize(JsonElement e) =>
        e.TryGetProperty("lfs", out var lfs) && lfs.TryGetProperty("size", out var ls) ? ls.GetInt64()
        : e.TryGetProperty("size", out var s) ? s.GetInt64() : 0;

    public static string? Quant(string fileName)
    {
        var m = QuantRegex().Match(Path.GetFileNameWithoutExtension(fileName));
        return m.Success ? m.Value.ToUpperInvariant() : null;
    }

    [GeneratedRegex(@"-\d{5}-of-\d{5}\.gguf$", RegexOptions.IgnoreCase)]
    private static partial Regex SplitPart();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(UD-)?(I?Q\d(_[0-9A-Z]+)*|BF16|F16|F32|MXFP4(_MOE)?|FP8)(?![A-Za-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex QuantRegex();
}

public sealed record WhoAmI(bool Valid, string? User, string? Role, string? Error);
public sealed record HfRepo(string Id, long Downloads, long Likes, string? LastModified, bool Gated, string? Pipeline);
public sealed record HfFile(string File, IReadOnlyList<HfPart> Parts, long Size, string? Quant, bool IsMmproj);
public sealed record HfPart(string Path, long Size);
