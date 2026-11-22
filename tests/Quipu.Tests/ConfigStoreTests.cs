using Quipu.Config;
using Microsoft.Extensions.Logging.Abstractions;

namespace Quipu.Tests;

public sealed class ConfigStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("quipu-tests-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private ConfigStore Store(string? yaml = null)
    {
        var path = Path.Combine(_dir, "quipu.yaml");
        if (yaml is not null) File.WriteAllText(path, yaml);
        return new ConfigStore(path, NullLogger.Instance);
    }

    [Fact]
    public void New_config_is_created_next_to_its_file_with_safe_defaults()
    {
        using var store = Store();
        var cfg = store.Current;

        Assert.True(File.Exists(store.FilePath));
        Assert.Equal(Path.Combine(_dir, "models"), cfg.Paths.Models);
        Assert.Equal("http://127.0.0.1:5250", cfg.Server.Listen);
        Assert.Equal("127.0.0.1", cfg.Defaults.Host);
        Assert.Equal(1, cfg.Server.MaxActiveModels);
        Assert.Equal(15, cfg.Defaults.IdleUnloadMinutes);
        Assert.Empty(NetworkAccess.Warnings(cfg));
    }

    [Fact]
    public void Yaml_round_trips()
    {
        using var store = Store("""
            paths: { models: M, installs: B }
            server:
              listen: http://0.0.0.0:5250
              allowedNetworks: [10.0.0.0/24]
            models:
              Qwen:
                hf: { repo: unsloth/Qwen3-8B-GGUF, file: Qwen3-8B-Q4_K_M.gguf }
                ctxSize: 65536
                args: [--spec-draft-n-max, "2"]
            """);
        var again = store.Parse(store.Serialize(store.Current));

        Assert.Equal(new[] { "10.0.0.0/24" }, again.Server.AllowedNetworks);
        Assert.True(again.Models.ContainsKey("qwen"), "model names are case-insensitive");
        Assert.Equal(65536, again.Models["Qwen"].CtxSize);
        Assert.Equal(new[] { "--spec-draft-n-max", "2" }, again.Models["Qwen"].Args);
    }

    [Theory]
    [InlineData("server: { allowedNetworks: [10.0.0.0/33] }", "allowedNetworks")]
    [InlineData("server: { maxActiveModels: -1 }", "maxActiveModels")]
    [InlineData("llamaCpp: { backend: metal }", "llamaCpp.backend")]
    [InlineData("models: { 'bad name': { file: a.gguf } }", "may only contain")]
    [InlineData("models: { a: { port: 8081, file: a.gguf }, b: { port: 8081, file: b.gguf } }", "both use port 8081")]
    [InlineData("models: { a: { ctxSize: 4096 } }", "needs 'file' or 'hf'")]
    public void Invalid_config_is_rejected_with_a_clear_message(string yaml, string expected)
    {
        using var store = Store();
        var ex = Assert.Throws<ConfigValidationException>(() => store.Parse("paths: { models: M, installs: B }\n" + yaml));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void Locate_prefers_argument_then_env_then_existing_files_then_profile_folder()
    {
        var exe = Path.Combine(_dir, "app");
        var home = Path.Combine(_dir, "home");
        var none = (string _) => false;

        Assert.Equal("x.yaml", ConfigStore.Locate(["--config", "x.yaml"], "env.yaml", exe, home, none));
        Assert.Equal("env.yaml", ConfigStore.Locate([], "env.yaml", exe, home, none));
        Assert.Equal(Path.Combine(exe, "quipu.yaml"), ConfigStore.Locate([], null, exe, home, _ => true));
        Assert.Equal(Path.Combine(home, "quipu.yaml"), ConfigStore.Locate([], null, exe, home, none));
    }

    [Fact]
    public void Api_save_refuses_to_overwrite_a_broken_hand_edit()
    {
        using var store = Store("paths: { models: M, installs: B }\n");
        const string broken = "paths: { models: M, installs: B }\nllamaCpp: { backend: metal }\n";
        File.WriteAllText(store.FilePath, broken);
        Assert.False(store.Reload());
        Assert.NotNull(store.LastError);

        Assert.Throws<InvalidOperationException>(() => store.Update(c => c.Server.MaxActiveModels = 2));
        Assert.Equal(broken, File.ReadAllText(store.FilePath));

        store.ReplaceRaw("paths: { models: M, installs: B }\n"); // fixing it through the editor works
        store.Update(c => c.Server.MaxActiveModels = 2);
        Assert.Equal(2, store.Current.Server.MaxActiveModels);
    }

    [Fact]
    public void Merge_patch_replaces_env_as_a_whole_and_merges_other_objects()
    {
        var target = System.Text.Json.Nodes.JsonNode.Parse("""{"defaults":{"ctxSize":4096,"env":{"A":"1","B":"2"}},"server":{"listen":"x"}}""")!.AsObject();
        var patch = System.Text.Json.Nodes.JsonNode.Parse("""{"Defaults":{"env":{"A":"1"},"ctxSize":null}}""")!.AsObject();

        ConfigStore.MergePatch(target, patch);

        Assert.Equal("""{"defaults":{"env":{"A":"1"}},"server":{"listen":"x"}}""", target.ToJsonString());
    }
}
