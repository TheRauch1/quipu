using System.Diagnostics;
using System.Globalization;

namespace Quipu.Services;

/// <summary>GPU telemetry through nvidia-smi (ships with the NVIDIA driver).</summary>
public sealed class GpuService
{
    private readonly object _gate = new();
    private GpuSnapshot? _cached;
    private DateTime _cachedAt;
    private bool? _hasNvidia;

    public bool HasNvidia => _hasNvidia ??= Run("nvidia-smi", "-L", 3000) is { Length: > 0 } s && s.Contains("GPU", StringComparison.Ordinal);

    /// <summary>Backend used when llamaCpp.backend is "auto".</summary>
    public string AutoBackend => HasNvidia ? "cuda" : "vulkan";

    public GpuSnapshot Query()
    {
        lock (_gate)
        {
            if (_cached is not null && DateTime.UtcNow - _cachedAt < TimeSpan.FromSeconds(2)) return _cached;
            _cached = HasNvidia ? QueryNvidia() : new GpuSnapshot([], new Dictionary<int, long>(), "nvidia-smi not found");
            _cachedAt = DateTime.UtcNow;
            return _cached;
        }
    }

    private static GpuSnapshot QueryNvidia()
    {
        var gpus = new List<GpuInfo>();
        var csv = Run("nvidia-smi",
            "--query-gpu=index,name,memory.total,memory.used,utilization.gpu,temperature.gpu,power.draw,driver_version,compute_cap --format=csv,noheader,nounits", 4000);
        foreach (var line in (csv ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var f = line.Split(',').Select(x => x.Trim()).ToArray();
            if (f.Length < 9) continue;
            gpus.Add(new GpuInfo(Int(f[0]) ?? 0, f[1], Int(f[2]) ?? 0, Int(f[3]) ?? 0, Int(f[4]), Int(f[5]), Dbl(f[6]), f[7], f[8]));
        }

        var perPid = new Dictionary<int, long>();
        var apps = Run("nvidia-smi", "--query-compute-apps=pid,used_memory --format=csv,noheader,nounits", 4000);
        foreach (var line in (apps ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var f = line.Split(',').Select(x => x.Trim()).ToArray();
            if (f.Length >= 2 && Int(f[0]) is { } pid && Int(f[1]) is { } mem)
                perPid[pid] = perPid.GetValueOrDefault(pid) + mem;
        }
        return new GpuSnapshot(gpus, perPid, null);
    }

    private static int? Int(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    private static double? Dbl(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static string? Run(string exe, string args, int timeoutMs)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return null; }
            return p.ExitCode == 0 ? output.Result : null;
        }
        catch
        {
            return null;
        }
    }
}

public sealed record GpuInfo(int Index, string Name, int MemoryTotalMiB, int MemoryUsedMiB, int? UtilizationPct,
    int? TemperatureC, double? PowerW, string Driver, string ComputeCap);

/// <summary>GPUs plus VRAM (MiB) used per process id.</summary>
public sealed record GpuSnapshot(IReadOnlyList<GpuInfo> Gpus, IReadOnlyDictionary<int, long> VramByPid, string? Error);
