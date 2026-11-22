using Quipu.Config;
using Quipu.Services;

namespace Quipu.Tests;

public class CommandBuilderTests
{
    private static QuipuConfig Config(Action<QuipuConfig>? tweak = null)
    {
        var cfg = new QuipuConfig { Paths = new PathsConfig { Models = Path.Combine(Path.GetTempPath(), "models"), Installs = "builds" } };
        tweak?.Invoke(cfg);
        return cfg;
    }

    private static string? ValueOf(List<string> args, string flag)
    {
        var i = args.LastIndexOf(flag);
        return i >= 0 && i + 1 < args.Count ? args[i + 1] : null;
    }

    [Fact]
    public void Model_values_override_defaults_and_args_are_appended()
    {
        var cfg = Config(c => c.Defaults = new ModelParams { Host = "127.0.0.1", CtxSize = 8192, FlashAttn = "auto", Args = ["--metrics"] });
        var m = new ModelProfile { File = "a.gguf", CtxSize = 65536, Args = ["--temp", "0.6"] };

        var args = CommandBuilder.BuildArgs(cfg, "a", m, 8080);

        Assert.Equal("65536", ValueOf(args, "-c"));
        Assert.Equal("auto", ValueOf(args, "-fa"));
        Assert.Equal("8080", ValueOf(args, "--port"));
        Assert.Equal(new[] { "--metrics", "--temp", "0.6" }, args.Skip(args.IndexOf("--metrics")).ToList());
    }

    [Fact]
    public void Relative_model_file_resolves_against_models_dir()
    {
        var cfg = Config();
        var args = CommandBuilder.BuildArgs(cfg, "a", new ModelProfile { File = Path.Combine("owner", "repo", "a.gguf") }, 8080);
        Assert.Equal(Path.Combine(cfg.Paths.Models, "owner", "repo", "a.gguf"), ValueOf(args, "-m"));
    }

    [Fact]
    public void Hf_source_without_file_uses_owner_repo_layout()
    {
        var cfg = Config();
        var m = new ModelProfile { Hf = new HfSource { Repo = "unsloth/Qwen3-8B-GGUF", File = "Qwen3-8B-Q4_K_M.gguf" } };
        var args = CommandBuilder.BuildArgs(cfg, "q", m, 8080);
        Assert.Equal(Path.Combine(cfg.Paths.Models, "unsloth", "Qwen3-8B-GGUF", "Qwen3-8B-Q4_K_M.gguf"), ValueOf(args, "-m"));
    }

    [Fact]
    public void Alias_defaults_to_profile_name()
    {
        var cfg = Config();
        Assert.Equal("my-model", ValueOf(CommandBuilder.BuildArgs(cfg, "my-model", new ModelProfile { File = "a.gguf" }, 1), "-a"));
        Assert.Equal("gemma", ValueOf(CommandBuilder.BuildArgs(cfg, "my-model", new ModelProfile { File = "a.gguf", Alias = "gemma" }, 1), "-a"));
    }

    [Theory]
    [InlineData(15, "900")]
    [InlineData(1, "60")]
    public void Idle_minutes_become_sleep_seconds(int minutes, string seconds)
    {
        var args = CommandBuilder.BuildArgs(Config(), "a", new ModelProfile { File = "a.gguf", IdleUnloadMinutes = minutes }, 1);
        Assert.Equal(seconds, ValueOf(args, "--sleep-idle-seconds"));
    }

    [Fact]
    public void Idle_zero_means_never()
    {
        var cfg = Config(c => c.Defaults.IdleUnloadMinutes = 15);
        var args = CommandBuilder.BuildArgs(cfg, "a", new ModelProfile { File = "a.gguf", IdleUnloadMinutes = 0 }, 1);
        Assert.DoesNotContain("--sleep-idle-seconds", args);
    }

    [Fact]
    public void Jinja_false_is_explicit()
    {
        Assert.Contains("--no-jinja", CommandBuilder.BuildArgs(Config(), "a", new ModelProfile { File = "a.gguf", Jinja = false }, 1));
        Assert.Contains("--jinja", CommandBuilder.BuildArgs(Config(), "a", new ModelProfile { File = "a.gguf", Jinja = true }, 1));
    }

    [Fact]
    public void Draft_model_adds_md_and_inferred_spec_type()
    {
        var m = new ModelProfile { File = "a.gguf", Draft = Path.Combine("dspark", "dspark-Model-BF16.gguf") };
        var args = CommandBuilder.BuildArgs(Config(), "a", m, 1);
        Assert.Equal("draft-dspark", ValueOf(args, "--spec-type"));
        Assert.EndsWith("dspark-Model-BF16.gguf", ValueOf(args, "-md"));
    }

    [Theory]
    [InlineData("dspark-x.gguf", "draft-dspark")]
    [InlineData("x-dflash2.gguf", "draft-dflash2")]
    [InlineData("x-dflash.gguf", "draft-dflash")]
    [InlineData("x-MTP.gguf", "draft-mtp")]
    [InlineData("eagle3-x.gguf", "draft-eagle3")]
    [InlineData("Qwen3-0.6B-Q8_0.gguf", "draft-simple")]
    public void Spec_type_is_inferred_from_draft_name(string file, string expected) =>
        Assert.Equal(expected, CommandBuilder.InferSpecType(file));

    [Fact]
    public void Render_quotes_only_what_needs_quoting()
    {
        var line = CommandBuilder.Render(@"C:\llama\llama-server.exe", ["-m", @"D:\my models\a.gguf", "-c", "4096"]);
        Assert.Equal(@"C:\llama\llama-server.exe -m ""D:\my models\a.gguf"" -c 4096", line);
    }
}
