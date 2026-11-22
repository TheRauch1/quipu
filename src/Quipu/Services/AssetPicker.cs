using System.Text.RegularExpressions;

namespace Quipu.Services;

public sealed record GhAsset(string Name, long Size, string BrowserDownloadUrl);

public readonly record struct PickedAssets(GhAsset Main, GhAsset? Runtime, string? CudaVersion);

/// <summary>Picks the Windows x64 zip (and CUDA runtime zip, if separate) for a backend from a release's asset list.</summary>
public static class AssetPicker
{
    /// <param name="cudaVersion">"auto" = highest CUDA build offered, or e.g. "12.4" / "13".</param>
    public static PickedAssets? Pick(string tag, IReadOnlyList<GhAsset> assets, string backend, string cudaVersion) =>
        PickGgml(tag, assets, backend, cudaVersion) ?? PickUnsloth(tag, assets, backend, cudaVersion);

    /// <summary>ggml-org names assets "llama-{tag}-bin-win-{cuda-13.1|vulkan|cpu|…}-x64.zip" plus "cudart-llama-bin-win-cuda-X-x64.zip".</summary>
    public static PickedAssets? PickGgml(string tag, IReadOnlyList<GhAsset> assets, string backend, string cudaVersion)
    {
        var t = Regex.Escape(tag);
        if (backend == "cuda")
        {
            var cudaAssets = assets
                .Select(a => (Asset: a, M: Regex.Match(a.Name, $@"^llama-{t}-bin-win-cuda-(?<v>[\d.]+)-x64\.zip$", RegexOptions.IgnoreCase)))
                .Where(x => x.M.Success)
                .Select(x => (x.Asset, Ver: x.M.Groups["v"].Value))
                .OrderByDescending(x => Version.TryParse(x.Ver, out var v) ? v : new Version(0, 0))
                .ToList();
            var chosen = cudaVersion == "auto"
                ? cudaAssets.FirstOrDefault()
                : cudaAssets.FirstOrDefault(x => x.Ver == cudaVersion)
                  is { Asset: not null } exact ? exact : cudaAssets.FirstOrDefault(x => x.Ver.Split('.')[0] == cudaVersion);
            if (chosen.Asset is null) return null;
            var runtime = assets.FirstOrDefault(a =>
                Regex.IsMatch(a.Name, $@"^cudart-llama-(?:{t}-)?bin-win-cuda-{Regex.Escape(chosen.Ver)}-x64\.zip$", RegexOptions.IgnoreCase));
            return new PickedAssets(chosen.Asset, runtime, chosen.Ver);
        }

        var pattern = backend switch
        {
            "vulkan" => "vulkan",
            "cpu" => "cpu",
            "rocm" => @"(?:rocm-[\d.]+|hip-radeon)",
            "sycl" => "sycl",
            "openvino" => @"openvino-[\d.]+",
            _ => null,
        };
        if (pattern is null) return null;
        var main = assets.FirstOrDefault(a => Regex.IsMatch(a.Name, $@"^llama-{t}-bin-win-{pattern}-x64\.zip$", RegexOptions.IgnoreCase));
        return main is null ? null : new PickedAssets(main, null, null);
    }

    /// <summary>Unsloth's fork names assets "app-{tag}-windows-x64-{cuda13-portable|cuda13-newer|vulkan|cpu|…}.zip".
    /// "portable" bundles the CUDA runtime and all GPU archs, so it is preferred.</summary>
    public static PickedAssets? PickUnsloth(string tag, IReadOnlyList<GhAsset> assets, string backend, string cudaVersion)
    {
        var t = Regex.Escape(tag);
        var flavored = assets
            .Select(a => (Asset: a, M: Regex.Match(a.Name, $@"^app-{t}-windows-x64-(?<flavor>[a-z0-9-]+)\.zip$", RegexOptions.IgnoreCase)))
            .Where(x => x.M.Success).Select(x => (x.Asset, Flavor: x.M.Groups["flavor"].Value.ToLowerInvariant())).ToList();
        if (backend == "cuda")
        {
            var cuda = flavored
                .Select(x => (x.Asset, x.Flavor, M: Regex.Match(x.Flavor, @"^cuda(?<v>\d+)-(?<kind>portable|newer|older)$")))
                .Where(x => x.M.Success)
                .Select(x => (x.Asset, Ver: x.M.Groups["v"].Value, Kind: x.M.Groups["kind"].Value))
                .Where(x => cudaVersion == "auto" || cudaVersion.Split('.')[0] == x.Ver)
                .OrderByDescending(x => int.Parse(x.Ver)).ThenBy(x => x.Kind switch { "portable" => 0, "newer" => 1, _ => 2 })
                .FirstOrDefault();
            return cuda.Asset is null ? null : new PickedAssets(cuda.Asset, null, cuda.Ver);
        }
        var match = backend switch
        {
            "vulkan" => flavored.FirstOrDefault(x => x.Flavor == "vulkan"),
            "cpu" => flavored.FirstOrDefault(x => x.Flavor == "cpu"),
            "rocm" => flavored.FirstOrDefault(x => x.Flavor.StartsWith("rocm", StringComparison.Ordinal)),
            _ => default,
        };
        return match.Asset is null ? null : new PickedAssets(match.Asset, null, null);
    }
}
