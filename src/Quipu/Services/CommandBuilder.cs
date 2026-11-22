using System.Globalization;
using System.Text;
using Quipu.Config;

namespace Quipu.Services;

/// <summary>Turns a model profile + defaults into a llama-server command line.</summary>
public static class CommandBuilder
{
    /// <summary>Model profile values override defaults; args are concatenated, env merged.</summary>
    public static ModelParams Effective(QuipuConfig cfg, ModelProfile m)
    {
        var d = cfg.Defaults;
        return new ModelParams
        {
            Host = m.Host ?? d.Host ?? "127.0.0.1",
            CtxSize = m.CtxSize ?? d.CtxSize,
            GpuLayers = m.GpuLayers ?? d.GpuLayers,
            FlashAttn = m.FlashAttn ?? d.FlashAttn,
            Fit = m.Fit ?? d.Fit,
            Threads = m.Threads ?? d.Threads,
            Parallel = m.Parallel ?? d.Parallel,
            BatchSize = m.BatchSize ?? d.BatchSize,
            UBatchSize = m.UBatchSize ?? d.UBatchSize,
            CacheTypeK = m.CacheTypeK ?? d.CacheTypeK,
            CacheTypeV = m.CacheTypeV ?? d.CacheTypeV,
            Jinja = m.Jinja ?? d.Jinja,
            Mlock = m.Mlock ?? d.Mlock,
            NoMmap = m.NoMmap ?? d.NoMmap,
            IdleUnloadMinutes = m.IdleUnloadMinutes ?? d.IdleUnloadMinutes,
            Args = [.. d.Args ?? [], .. m.Args ?? []],
            Env = MergeEnv(d.Env, m.Env),
        };
    }

    private static Dictionary<string, string> MergeEnv(Dictionary<string, string>? a, Dictionary<string, string>? b)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var src in new[] { a, b })
            if (src is not null)
                foreach (var (k, v) in src) env[k] = v;
        return env;
    }

    public static List<string> BuildArgs(QuipuConfig cfg, string name, ModelProfile m, int port)
    {
        var p = Effective(cfg, m);
        var args = new List<string> { "-m", ModelPaths.ResolveModel(cfg, m) ?? "" };
        if (ModelPaths.ResolveMmproj(cfg, m) is { } mm) args.AddRange(["--mmproj", mm]);
        if (ModelPaths.ResolveDraft(cfg, m) is { } draft)
            args.AddRange(["--spec-type", string.IsNullOrWhiteSpace(m.SpecType) ? InferSpecType(draft) : m.SpecType, "-md", draft]);
        else if (!string.IsNullOrWhiteSpace(m.SpecType))
            args.AddRange(["--spec-type", m.SpecType]);
        args.AddRange(["--host", p.Host!, "--port", port.ToString(CultureInfo.InvariantCulture)]);
        args.AddRange(["-a", string.IsNullOrWhiteSpace(m.Alias) ? name : m.Alias]);
        Add(args, "-c", p.CtxSize);
        Add(args, "-ngl", p.GpuLayers);
        Add(args, "-fa", p.FlashAttn);
        Add(args, "-fit", p.Fit);
        Add(args, "-t", p.Threads);
        Add(args, "-np", p.Parallel);
        Add(args, "-b", p.BatchSize);
        Add(args, "-ub", p.UBatchSize);
        Add(args, "-ctk", p.CacheTypeK);
        Add(args, "-ctv", p.CacheTypeV);
        if (p.Jinja is { } j) args.Add(j ? "--jinja" : "--no-jinja");
        if (p.Mlock == true) args.Add("--mlock");
        if (p.NoMmap == true) args.Add("--no-mmap");
        if (p.IdleUnloadMinutes is > 0 and var idle) args.AddRange(["--sleep-idle-seconds", (idle * 60).ToString(CultureInfo.InvariantCulture)]);
        args.AddRange(p.Args ?? []);
        return args;
    }

    /// <summary>Speculative decoding type from a draft file name (dspark / mtp / eagle / dflash heads), else a plain draft model.</summary>
    public static string InferSpecType(string draftPath)
    {
        var n = Path.GetFileName(draftPath).ToLowerInvariant();
        if (n.Contains("dspark")) return "draft-dspark";
        if (n.Contains("dflash2")) return "draft-dflash2";
        if (n.Contains("dflash")) return "draft-dflash";
        if (n.Contains("mtp")) return "draft-mtp";
        if (n.Contains("eagle")) return "draft-eagle3";
        return "draft-simple";
    }

    private static void Add(List<string> args, string flag, object? value)
    {
        if (value is null || value is string { Length: 0 }) return;
        args.Add(flag);
        args.Add(Convert.ToString(value, CultureInfo.InvariantCulture)!);
    }

    /// <summary>Human-readable / copy-pasteable command line.</summary>
    public static string Render(string exe, IEnumerable<string> args)
    {
        var sb = new StringBuilder(Quote(exe));
        foreach (var a in args) sb.Append(' ').Append(Quote(a));
        return sb.ToString();
    }

    private static string Quote(string s) =>
        s.Length > 0 && !s.Any(c => char.IsWhiteSpace(c) || c == '"') ? s : "\"" + s.Replace("\"", "\\\"") + "\"";
}

public static class ModelPaths
{
    /// <summary>Local folder for a Hugging Face file: {models}\{owner}\{repo}\{file}.</summary>
    public static string HfRelative(string repo, string file) =>
        Path.Combine([.. repo.Split('/'), .. file.Split('/')]);

    public static string? ResolveModel(QuipuConfig cfg, ModelProfile m)
    {
        if (!string.IsNullOrWhiteSpace(m.File)) return Rooted(cfg, m.File);
        if (m.Hf is { Repo.Length: > 0, File.Length: > 0 }) return Path.Combine(cfg.Paths.Models, HfRelative(m.Hf.Repo, m.Hf.File));
        return null;
    }

    public static string? ResolveMmproj(QuipuConfig cfg, ModelProfile m)
    {
        if (!string.IsNullOrWhiteSpace(m.Mmproj)) return Rooted(cfg, m.Mmproj);
        if (m.Hf is { Mmproj.Length: > 0 }) return Path.Combine(cfg.Paths.Models, HfRelative(m.Hf.Repo, m.Hf.Mmproj));
        return null;
    }

    public static string? ResolveDraft(QuipuConfig cfg, ModelProfile m)
    {
        if (!string.IsNullOrWhiteSpace(m.Draft)) return Rooted(cfg, m.Draft);
        if (m.Hf is { Draft.Length: > 0 }) return Path.Combine(cfg.Paths.Models, HfRelative(m.Hf.Repo, m.Hf.Draft));
        return null;
    }

    /// <summary>Model, mmproj and draft paths a profile refers to.</summary>
    public static IEnumerable<string> AllFiles(QuipuConfig cfg, ModelProfile m) =>
        new[] { ResolveModel(cfg, m), ResolveMmproj(cfg, m), ResolveDraft(cfg, m) }.Where(p => p is not null).Select(p => p!);

    public static string Rooted(QuipuConfig cfg, string path) =>
        Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(cfg.Paths.Models, path));

    /// <summary>Path relative to the models dir if inside it, else absolute.</summary>
    public static string Relativize(QuipuConfig cfg, string fullPath)
    {
        var rel = Path.GetRelativePath(cfg.Paths.Models, fullPath);
        return rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel) ? fullPath : rel;
    }

    /// <summary>All files of a split GGUF (…-00001-of-00003.gguf) or just the file itself.</summary>
    public static IReadOnlyList<string> AllParts(string path)
    {
        var m = System.Text.RegularExpressions.Regex.Match(path, @"^(?<base>.*)-00001-of-(?<n>\d{5})\.gguf$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success) return [path];
        var n = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
        return Enumerable.Range(1, n).Select(i => $"{m.Groups["base"].Value}-{i:00000}-of-{n:00000}.gguf").ToList();
    }
}
