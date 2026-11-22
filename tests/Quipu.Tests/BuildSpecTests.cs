using Quipu.Services;

namespace Quipu.Tests;

public class BuildSpecTests
{
    private static InstalledBuild B(string id, string tag, string backend, string repo = "ggml-org/llama.cpp") =>
        new(id, tag, backend, null, Path.Combine("builds", id), DateTimeOffset.Now, DateTimeOffset.Now, false, repo);

    private static readonly InstalledBuild[] Builds =
    [
        B("b12617-cuda13.5", "b12617", "cuda"),
        B("b12617-vulkan", "b12617", "vulkan"),
        B("unslothai-b12588-mix-c0c3de8-cuda13", "b12588-mix-c0c3de8", "cuda", "unslothai/llama.cpp"),
    ];

    [Theory]
    [InlineData("unslothai/llama.cpp@b12588-mix-c0c3de8", "unslothai-b12588-mix-c0c3de8-cuda13")]
    [InlineData("b12617-vulkan", "b12617-vulkan")]
    [InlineData("b12617", "b12617-cuda13.5")] // a bare tag picks the build for the current backend
    public void Match_finds_a_build_by_spec_id_or_tag(string spec, string expectedId) =>
        Assert.Equal(expectedId, BuildSpec.Match(Builds, spec, "cuda")?.Id);

    [Fact]
    public void Match_is_null_when_not_installed() => Assert.Null(BuildSpec.Match(Builds, "other/llama.cpp@b1", "cuda"));

    [Fact]
    public void A_fork_pinned_by_its_spec_resolves_to_that_build() =>
        Assert.Equal(Builds[2].Id, BuildSpec.Match(Builds, Builds[2].Spec, "cuda")?.Id);

    [Theory]
    [InlineData("unslothai/llama.cpp@b12588-mix-c0c3de8", "unslothai/llama.cpp", "b12588-mix-c0c3de8")]
    [InlineData("b12617-cuda13.5", null, "b12617")]
    [InlineData("b12617", null, "b12617")]
    public void ToInstall_keeps_the_fork_repo_and_full_tag(string spec, string? repo, string tag) =>
        Assert.Equal((repo, tag), BuildSpec.ToInstall(spec));
}
