using Quipu.Services;

namespace Quipu.Tests;

public class AssetPickerTests
{
    private static List<GhAsset> Assets(params string[] names) =>
        names.Select(n => new GhAsset(n, 1000, $"https://example.invalid/{n}")).ToList();

    // asset names as ggml-org/llama.cpp publishes them
    private static readonly List<GhAsset> Ggml = Assets(
        "llama-b12617-bin-win-cpu-x64.zip",
        "llama-b12617-bin-win-cuda-12.4-x64.zip",
        "llama-b12617-bin-win-cuda-13.1-x64.zip",
        "llama-b12617-bin-win-vulkan-x64.zip",
        "llama-b12617-bin-win-hip-radeon-x64.zip",
        "llama-b12617-bin-win-sycl-x64.zip",
        "llama-b12617-bin-win-cpu-arm64.zip",
        "cudart-llama-bin-win-cuda-12.4-x64.zip",
        "cudart-llama-bin-win-cuda-13.1-x64.zip",
        "llama-b12617-bin-ubuntu-x64.zip");

    [Fact]
    public void Cuda_auto_picks_highest_version_with_matching_runtime()
    {
        var p = AssetPicker.Pick("b12617", Ggml, "cuda", "auto")!.Value;
        Assert.Equal("llama-b12617-bin-win-cuda-13.1-x64.zip", p.Main.Name);
        Assert.Equal("cudart-llama-bin-win-cuda-13.1-x64.zip", p.Runtime?.Name);
        Assert.Equal("13.1", p.CudaVersion);
    }

    [Theory]
    [InlineData("12.4", "llama-b12617-bin-win-cuda-12.4-x64.zip")]
    [InlineData("12", "llama-b12617-bin-win-cuda-12.4-x64.zip")]
    [InlineData("13", "llama-b12617-bin-win-cuda-13.1-x64.zip")]
    public void Cuda_version_can_be_pinned_exactly_or_by_major(string wanted, string expected) =>
        Assert.Equal(expected, AssetPicker.Pick("b12617", Ggml, "cuda", wanted)!.Value.Main.Name);

    [Fact]
    public void Unknown_cuda_version_finds_nothing() =>
        Assert.Null(AssetPicker.Pick("b12617", Ggml, "cuda", "11.7"));

    [Theory]
    [InlineData("cpu", "llama-b12617-bin-win-cpu-x64.zip")]
    [InlineData("vulkan", "llama-b12617-bin-win-vulkan-x64.zip")]
    [InlineData("rocm", "llama-b12617-bin-win-hip-radeon-x64.zip")]
    [InlineData("sycl", "llama-b12617-bin-win-sycl-x64.zip")]
    public void Other_backends_pick_their_x64_zip(string backend, string expected)
    {
        var p = AssetPicker.Pick("b12617", Ggml, backend, "auto")!.Value;
        Assert.Equal(expected, p.Main.Name);
        Assert.Null(p.Runtime);
    }

    [Fact]
    public void Release_without_windows_assets_is_skipped() =>
        Assert.Null(AssetPicker.Pick("b1", Assets("llama-b1-bin-ubuntu-x64.zip", "llama-b1-bin-macos-arm64.zip"), "cuda", "auto"));

    [Fact]
    public void Assets_of_another_tag_are_ignored() =>
        Assert.Null(AssetPicker.Pick("b2", Assets("llama-b1-bin-win-cpu-x64.zip"), "cpu", "auto"));

    // asset names as unslothai/llama.cpp publishes them
    private static readonly List<GhAsset> Unsloth = Assets(
        "app-b12588-mix-c0c3de8-windows-x64-cuda12-newer.zip",
        "app-b12588-mix-c0c3de8-windows-x64-cuda13-newer.zip",
        "app-b12588-mix-c0c3de8-windows-x64-cuda13-portable.zip",
        "app-b12588-mix-c0c3de8-windows-x64-vulkan.zip",
        "app-b12588-mix-c0c3de8-windows-x64-cpu.zip");

    [Fact]
    public void Unsloth_cuda_prefers_highest_major_and_portable()
    {
        var p = AssetPicker.Pick("b12588-mix-c0c3de8", Unsloth, "cuda", "auto")!.Value;
        Assert.Equal("app-b12588-mix-c0c3de8-windows-x64-cuda13-portable.zip", p.Main.Name);
        Assert.Equal("13", p.CudaVersion);
    }

    [Fact]
    public void Unsloth_cuda_major_pin() =>
        Assert.Equal("app-b12588-mix-c0c3de8-windows-x64-cuda12-newer.zip",
            AssetPicker.Pick("b12588-mix-c0c3de8", Unsloth, "cuda", "12.8")!.Value.Main.Name);

    [Fact]
    public void Unsloth_vulkan() =>
        Assert.Equal("app-b12588-mix-c0c3de8-windows-x64-vulkan.zip",
            AssetPicker.Pick("b12588-mix-c0c3de8", Unsloth, "vulkan", "auto")!.Value.Main.Name);
}
