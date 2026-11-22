using System.Text.Json;
using System.Text.Json.Nodes;
using Quipu.Config;
using Quipu.Services;

namespace Quipu.Api;

public static class Endpoints
{
    public static void MapQuipuApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        // ------------------------------------------------------------ overview
        api.MapGet("/status", (ConfigStore config, BuildManager builds, InstanceManager instances, GpuService gpu,
                JobService jobs, HuggingFaceService hf, ModelService models) =>
            {
                var active = builds.Active();
                var tok = hf.ActiveToken();
                return new
                {
                    version = Program.AppVersion,
                    configFile = config.FilePath,
                    configError = config.LastError,
                    configLoadedAt = config.LoadedAt,
                    backend = builds.EffectiveBackend(),
                    backendSetting = config.Current.LlamaCpp.Backend,
                    llamaCpp = active,
                    paths = config.Current.Paths,
                    disk = models.Disk(),
                    gpus = gpu.Query().Gpus,
                    models = new
                    {
                        configured = config.Current.Models.Count,
                        running = instances.All.Count(i => i.State is InstanceState.Ready or InstanceState.Starting),
                    },
                    activeJobs = jobs.List().Count(j => j.State == JobState.Running),
                    huggingFace = new { tokenSource = tok.Source, hasToken = tok.Token is not null },
                    apiKeyRequired = !string.IsNullOrEmpty(config.Current.Server.ApiKey),
                    warnings = NetworkAccess.Warnings(config.Current),
                };
            })
            .WithSummary("Overall status: active llama.cpp build, GPU, disk, counts").WithTags("System");

        api.MapGet("/gpu", (GpuService gpu) => gpu.Query()).WithSummary("GPU telemetry (nvidia-smi)").WithTags("System");

        api.MapGet("/logs", (AppLogProvider logs, int? tail) => logs.Tail(tail ?? 200))
            .WithSummary("Quipu's own log").WithTags("System");

        api.MapPost("/apply", async (ReconcileService r, ApplyOptions? opts, CancellationToken ct) =>
                await r.ApplyAsync(opts ?? new ApplyOptions(), ct))
            .WithSummary("Make the machine match quipu.yaml (install build, download missing models, start autostart models)")
            .WithTags("Config");

        // ------------------------------------------------------------ config as code
        api.MapGet("/config", (ConfigStore config) => config.Current)
            .WithSummary("Current configuration as JSON").WithTags("Config");

        api.MapMethods("/config", ["PATCH"], (ConfigStore config, JsonObject patch) =>
            {
                if (patch.ContainsKey("models")) throw new ArgumentException("Use /api/models to change model profiles");
                return config.Update(c =>
                {
                    var node = JsonSerializer.SerializeToNode(c, ModelService.Json)!.AsObject();
                    ConfigStore.MergePatch(node, patch);
                    var next = node.Deserialize<QuipuConfig>(ModelService.Json)!;
                    c.Paths = next.Paths;
                    c.LlamaCpp = next.LlamaCpp;
                    c.Server = next.Server;
                    c.HuggingFace = next.HuggingFace;
                    c.Defaults = next.Defaults;
                });
            })
            .WithSummary("JSON merge-patch the configuration (paths, llamaCpp, server, huggingFace, defaults)").WithTags("Config");

        api.MapGet("/config/yaml", (ConfigStore config) => Results.Text(config.ReadRaw(), "text/yaml; charset=utf-8"))
            .WithSummary("Raw quipu.yaml").WithTags("Config");

        api.MapPut("/config/yaml", async (ConfigStore config, HttpRequest req) =>
            {
                using var reader = new StreamReader(req.Body);
                config.ReplaceRaw(await reader.ReadToEndAsync());
                return Results.Ok(new { ok = true, models = config.Current.Models.Count });
            })
            .Accepts<string>("text/yaml").WithSummary("Replace quipu.yaml (validated before saving)").WithTags("Config");

        api.MapPost("/config/validate", async (ConfigStore config, HttpRequest req) =>
            {
                using var reader = new StreamReader(req.Body);
                try
                {
                    var cfg = config.Parse(await reader.ReadToEndAsync());
                    return Results.Ok(new { valid = true, models = cfg.Models.Count });
                }
                catch (ConfigValidationException ex) { return Results.Ok(new { valid = false, errors = ex.Errors }); }
                catch (YamlDotNet.Core.YamlException ex) { return Results.Ok(new { valid = false, errors = new[] { $"line {ex.Start.Line}: {ex.InnerException?.Message ?? ex.Message}" } }); }
            })
            .Accepts<string>("text/yaml").WithSummary("Validate YAML without saving").WithTags("Config");

        api.MapPost("/config/reload", (ConfigStore config) => new { reloaded = config.Reload(), error = config.LastError })
            .WithSummary("Re-read quipu.yaml from disk").WithTags("Config");

        // ------------------------------------------------------------ llama.cpp builds
        var ll = api.MapGroup("/llamacpp").WithTags("llama.cpp");
        ll.MapGet("/builds", (BuildManager b) => b.ListInstalled()).WithSummary("Installed llama.cpp builds");
        ll.MapGet("/releases", async (BuildManager b, string? backend, bool? refresh, int? limit, string? repo, CancellationToken ct) =>
                (await b.ListReleasesAsync(backend, refresh ?? false, ct, repo)).Take(limit ?? 15))
            .WithSummary("Recent GitHub releases with a Windows build for the backend (repo= another fork, e.g. unslothai/llama.cpp)");
        ll.MapPost("/install", (BuildManager b, InstanceManager i, InstallRequest? req) =>
                b.Install(req?.Tag, req?.Backend, req?.Pin ?? false, i.BuildDirsInUse, req?.Repo))
            .WithSummary("Install a release (default: newest of llamaCpp.repo; set repo to install from a fork). Returns a job to poll.");
        ll.MapPost("/builds/{id}/activate", (BuildManager b, string id) => b.Activate(id))
            .WithSummary("Pin this build in quipu.yaml");
        ll.MapPost("/unpin", (ConfigStore c, BuildManager b) =>
            {
                c.Update(x => x.LlamaCpp.Version = "latest");
                return b.Active();
            })
            .WithSummary("Follow the newest installed build again (version: latest)");
        ll.MapDelete("/builds/{id}", (BuildManager b, InstanceManager i, string id) =>
            {
                b.Delete(id, i.BuildDirsInUse);
                return Results.NoContent();
            })
            .WithSummary("Delete an installed build");

        // ------------------------------------------------------------ models
        var mg = api.MapGroup("/models").WithTags("Models");
        mg.MapGet("/", (ModelService m) => m.List()).WithSummary("Model profiles with download and run state");
        mg.MapGet("/{name}", (ModelService m, string name) => m.Get(name)).WithSummary("One model profile");
        mg.MapPut("/{name}", (ModelService m, string name, ModelProfile profile) => m.Upsert(name, profile))
            .WithSummary("Create or replace a model profile");
        mg.MapMethods("/{name}", ["PATCH"], (ModelService m, string name, JsonObject patch) => m.Patch(name, patch))
            .WithSummary("Merge-patch a profile, e.g. {\"ctxSize\":32768,\"args\":[\"--jinja\"]}; null removes a field");
        mg.MapDelete("/{name}", async (ModelService m, string name, bool? deleteFiles) =>
                new { deleted = name, files = await m.DeleteAsync(name, deleteFiles ?? false) })
            .WithSummary("Remove a profile (deleteFiles=true also deletes the GGUF files)");
        mg.MapPost("/{name}/rename", (ModelService m, string name, RenameRequest req) => m.Rename(name, req.NewName))
            .WithSummary("Rename a profile");
        mg.MapPost("/{name}/start", async (InstanceManager i, string name, CancellationToken ct) => await i.StartAsync(name, ct))
            .WithSummary("Start llama-server for this model");
        mg.MapPost("/{name}/stop", async (InstanceManager i, string name) => await i.StopAsync(name) is { } inst ? Results.Ok(inst) : Results.NotFound())
            .WithSummary("Stop this model's server");
        mg.MapPost("/{name}/restart", async (InstanceManager i, string name, CancellationToken ct) => await i.RestartAsync(name, ct))
            .WithSummary("Restart (applies changed parameters)");
        mg.MapPost("/{name}/download", (ModelService m, string name) => m.StartDownload(name))
            .WithSummary("Download the model's hf source. Returns a job.");
        mg.MapGet("/{name}/logs", (InstanceManager i, string name, long? since, int? tail) =>
                i.Get(name) is { } inst
                    ? Results.Ok(new { state = inst.State, lines = inst.Logs(since ?? 0, tail ?? 300) })
                    : Results.Ok(new { state = InstanceState.Stopped, lines = Array.Empty<LogLine>() }))
            .WithSummary("Server output (use since=<last seq> to tail)");
        mg.MapGet("/{name}/command", (InstanceManager i, string name) =>
            {
                var (exe, args) = i.PreviewCommand(name);
                return new { exe, args, commandLine = CommandBuilder.Render(exe, args) };
            })
            .WithSummary("The llama-server command line this profile produces");
        mg.MapGet("/{name}/export/pi", async (PiExportService pi, HttpContext http, string name, string? host, CancellationToken ct) =>
                await pi.ExportAsync(name, host, http.Request.Host.Host, ct))
            .WithSummary("models.json for the pi coding agent (~/.pi/agent/models.json) pointing at this model; host= picks the address pi should use");

        api.MapGet("/files", (ModelService m) => new { disk = m.Disk(), files = m.ScanDisk() })
            .WithSummary("All GGUF files in the models directory").WithTags("Models");

        // ------------------------------------------------------------ hugging face
        var hfg = api.MapGroup("/hf").WithTags("Hugging Face");
        hfg.MapGet("/search", async (HuggingFaceService hf, string q, int? limit, CancellationToken ct) =>
                await hf.SearchAsync(q, limit ?? 25, ct))
            .WithSummary("Search GGUF repos on Hugging Face");
        hfg.MapGet("/files", async (HuggingFaceService hf, string repo, string? revision, CancellationToken ct) =>
                await hf.ListGgufAsync(repo, revision, ct))
            .WithSummary("GGUF files in a repo (split files grouped)");
        hfg.MapPost("/add", async (ModelService m, InstanceManager inst, AddModelRequest req, CancellationToken ct) =>
            {
                var (model, job) = await m.AddFromHuggingFaceAsync(req.Repo, req.File, req.Name, req.Revision, req.Mmproj, req.Profile, ct, req.Draft);
                return new { model, job };
            })
            .WithSummary("Create a profile from a Hugging Face GGUF and start downloading it into the models dir");

        hfg.MapGet("/tokens", (SecretStore s, ConfigStore c, HuggingFaceService hf) => new
            {
                active = c.Current.HuggingFace.ActiveToken,
                source = hf.ActiveToken().Source,
                envTokenPresent = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HF_TOKEN")),
                tokens = s.List(),
                storage = s.FilePath,
            })
            .WithSummary("Stored tokens (masked)");
        hfg.MapPut("/tokens/{name}", async (SecretStore s, ConfigStore c, HuggingFaceService hf, string name, TokenRequest req, CancellationToken ct) =>
            {
                if (!ConfigStore.IsValidModelName(name)) throw new ArgumentException("Token name may only contain letters, digits, '.', '_' and '-'");
                if (string.IsNullOrWhiteSpace(req.Token)) throw new ArgumentException("token is required");
                var who = await hf.WhoAmIAsync(req.Token.Trim(), ct);
                if (!who.Valid && !req.SkipValidation) throw new ArgumentException($"Hugging Face rejected the token ({who.Error})");
                s.Set(name, req.Token);
                if (who.Valid) s.MarkValidated(name, who.User, who.Role);
                if (req.Activate) c.Update(x => x.HuggingFace.ActiveToken = name);
                return new { name, who.User, who.Role, active = req.Activate };
            })
            .WithSummary("Store a token (encrypted with Windows DPAPI). Validated against huggingface.co unless skipValidation.");
        hfg.MapDelete("/tokens/{name}", (SecretStore s, ConfigStore c, string name) =>
            {
                if (!s.Remove(name)) return Results.NotFound();
                if (string.Equals(c.Current.HuggingFace.ActiveToken, name, StringComparison.OrdinalIgnoreCase))
                    c.Update(x => x.HuggingFace.ActiveToken = null);
                return Results.NoContent();
            })
            .WithSummary("Delete a stored token");
        hfg.MapPost("/tokens/{name}/activate", (SecretStore s, ConfigStore c, string name) =>
            {
                if (!s.Exists(name)) throw new KeyNotFoundException($"No token named '{name}'");
                c.Update(x => x.HuggingFace.ActiveToken = name);
                return new { active = name };
            })
            .WithSummary("Use this token for downloads and llama-server");
        hfg.MapPost("/tokens/deactivate", (ConfigStore c) =>
            {
                c.Update(x => x.HuggingFace.ActiveToken = null);
                return new { active = (string?)null };
            })
            .WithSummary("Stop using a stored token (falls back to HF_TOKEN env var)");
        hfg.MapPost("/tokens/{name}/validate", async (SecretStore s, HuggingFaceService hf, string name, CancellationToken ct) =>
            {
                var token = s.Get(name) ?? throw new KeyNotFoundException($"No token named '{name}' (or it was encrypted by another Windows user)");
                var who = await hf.WhoAmIAsync(token, ct);
                if (who.Valid) s.MarkValidated(name, who.User, who.Role);
                return who;
            })
            .WithSummary("Check a stored token against huggingface.co");

        // ------------------------------------------------------------ jobs
        var jg = api.MapGroup("/jobs").WithTags("Jobs");
        jg.MapGet("/", (JobService j) => j.List()).WithSummary("Downloads / installs");
        jg.MapGet("/{id}", (JobService j, string id) => j.Get(id) is { } job ? Results.Ok(job) : Results.NotFound())
            .WithSummary("One job");
        jg.MapDelete("/{id}", (JobService j, string id) => j.Cancel(id) ? Results.Accepted() : Results.NotFound())
            .WithSummary("Cancel a running job (partial downloads are resumed next time)");
        jg.MapPost("/clear", (JobService j) => new { removed = j.ClearFinished() }).WithSummary("Remove finished jobs");
    }
}

public sealed record InstallRequest(string? Tag, string? Backend, bool Pin = false, string? Repo = null);
public sealed record RenameRequest(string NewName);
public sealed record TokenRequest(string Token, bool Activate = true, bool SkipValidation = false);
public sealed record AddModelRequest(string Repo, string File, string? Name, string? Revision, string? Mmproj, ModelProfile? Profile, string? Draft = null);
