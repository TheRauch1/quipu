using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Quipu.Api;
using Quipu.Config;
using Quipu.Services;
using ModelContextProtocol.Server;

namespace Quipu.Mcp;

/// <summary>MCP tools (served at /mcp) so an AI assistant can manage llama.cpp for you.</summary>
[McpServerToolType]
public sealed class QuipuTools(
    ConfigStore config, BuildManager builds, ModelService models, InstanceManager instances, HuggingFaceService hf,
    SecretStore secrets, JobService jobs, GpuService gpu, ReconcileService reconcile, PiExportService piExport)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private static string J(object? o) => JsonSerializer.Serialize(o, Json);

    // ------------------------------------------------------------ overview

    [McpServerTool(Name = "get_status", ReadOnly = true), Description("Overview: active llama.cpp build, backend, GPU/VRAM, disk space, running models, config file path.")]
    public string GetStatus() => J(new
    {
        configFile = config.FilePath,
        configError = config.LastError,
        backend = builds.EffectiveBackend(),
        llamaCpp = builds.Active(),
        paths = config.Current.Paths,
        disk = models.Disk(),
        gpu = gpu.Query(),
        running = instances.All.Where(i => i.State is InstanceState.Ready or InstanceState.Starting)
            .Select(i => new { i.Name, i.State, i.Url, i.BuildId, unloadedWhileIdle = i.Asleep, i.LastActivityAt, i.UnloadAt }),
        huggingFaceToken = hf.ActiveToken().Source,
    });

    // ------------------------------------------------------------ models

    [McpServerTool(Name = "list_models", ReadOnly = true), Description("List model profiles with their parameters, whether the file is downloaded, run state, URL and VRAM use.")]
    public string ListModels() => J(models.List());

    [McpServerTool(Name = "list_model_files", ReadOnly = true), Description("List every GGUF file on disk in the models directory and which profiles use it.")]
    public string ListModelFiles() => J(new { disk = models.Disk(), files = models.ScanDisk() });

    [McpServerTool(Name = "start_model"), Description("Start llama-server for a model profile. Returns the URL of its OpenAI-compatible API. Loading continues in the background; poll get_model until state is Ready. " +
        "If server.maxActiveModels is reached (default 1), the least recently used running model is stopped first.")]
    public async Task<string> StartModel([Description("Model profile name")] string name) => J(await instances.StartAsync(name));

    [McpServerTool(Name = "stop_model", Destructive = false), Description("Stop a running model's llama-server.")]
    public async Task<string> StopModel(string name) => J(await instances.StopAsync(name));

    [McpServerTool(Name = "restart_model"), Description("Restart a model so changed parameters take effect.")]
    public async Task<string> RestartModel(string name) => J(await instances.RestartAsync(name));

    [McpServerTool(Name = "get_model", ReadOnly = true), Description("Details for one model profile, including state and the exact llama-server command line.")]
    public string GetModel(string name)
    {
        var view = models.Get(name);
        var (exe, args) = instances.PreviewCommand(name);
        return J(new { model = view, command = CommandBuilder.Render(exe, args) });
    }

    [McpServerTool(Name = "get_model_logs", ReadOnly = true), Description("Last lines of a model's llama-server output (useful to diagnose load failures, VRAM issues, speed).")]
    public string GetModelLogs(string name, [Description("Number of lines (default 80)")] int tail = 80) =>
        J(instances.Get(name)?.Logs(0, tail).Select(l => l.Text) ?? []);

    [McpServerTool(Name = "export_pi_config", ReadOnly = true), Description(
        "models.json for the pi coding agent (~/.pi/agent/models.json) pointing at a model's llama-server: provider 'quipu', " +
        "context window, max tokens, vision and reasoning support (read from the running server). Merge the model entry into an existing file.")]
    public async Task<string> ExportPiConfig(string name,
        [Description("Address pi should connect to (default 127.0.0.1; a LAN IP when pi runs on another machine)")] string? host = null) =>
        J(await piExport.ExportAsync(name, host, null, CancellationToken.None));

    [McpServerTool(Name = "update_model"), Description(
        "Create or merge-update a model profile from a JSON object. Fields: file (path relative to models dir), mmproj, port, alias, autostart, description, " +
        "ctxSize, gpuLayers (number|'auto'|'all'), flashAttn (on|off|auto), fit (on|off), threads, parallel, batchSize, ubatchSize, cacheTypeK, cacheTypeV, " +
        "jinja, mlock, noMmap, idleUnloadMinutes (unload from VRAM after N idle minutes, reloads on next request; 0 = never; default from defaults), " +
        "args (array of extra CLI args), env (object), hf {repo,file,revision,mmproj,draft}, " +
        "draft (speculative-decoding draft GGUF, -md), specType (--spec-type, inferred from draft name if empty), " +
        "build (llama.cpp build for this model: installed id or 'owner/repo@tag', e.g. a fork a model card requires). A null value removes a field. " +
        "Running models need restart_model afterwards.")]
    public string UpdateModel(string name, [Description("JSON object with the fields to set")] string json)
    {
        var patch = JsonNode.Parse(json) as JsonObject ?? throw new ArgumentException("json must be an object");
        if (config.Current.Models.ContainsKey(name)) return J(models.Patch(name, patch));
        return J(models.Upsert(name, patch.Deserialize<ModelProfile>(ModelService.Json)!));
    }

    [McpServerTool(Name = "delete_model", Destructive = true), Description("Remove a model profile. deleteFiles=true also deletes its GGUF files from disk (cannot be undone).")]
    public async Task<string> DeleteModel(string name, bool deleteFiles = false) =>
        J(new { deleted = name, files = await models.DeleteAsync(name, deleteFiles) });

    [McpServerTool(Name = "set_default_params"), Description("Merge-update the default start parameters that apply to all models (same fields as update_model minus file/port/hf).")]
    public string SetDefaultParams([Description("JSON object")] string json)
    {
        var patch = JsonNode.Parse(json) as JsonObject ?? throw new ArgumentException("json must be an object");
        var cfg = config.Update(c =>
        {
            var node = JsonSerializer.SerializeToNode(c.Defaults, ModelService.Json)!.AsObject();
            ConfigStore.MergePatch(node, patch);
            c.Defaults = node.Deserialize<ModelParams>(ModelService.Json)!;
        });
        return J(cfg.Defaults);
    }

    // ------------------------------------------------------------ hugging face

    [McpServerTool(Name = "search_huggingface", ReadOnly = true), Description("Search Hugging Face for GGUF model repositories, sorted by downloads.")]
    public async Task<string> SearchHuggingFace(string query, int limit = 15) => J(await hf.SearchAsync(query, limit, CancellationToken.None));

    [McpServerTool(Name = "list_huggingface_files", ReadOnly = true), Description("List GGUF files (quantizations) in a Hugging Face repo with sizes. Pick one that fits in VRAM (RTX-class GPUs: size < VRAM minus a few GB for context).")]
    public async Task<string> ListHuggingFaceFiles([Description("e.g. unsloth/Qwen3-8B-GGUF")] string repo, string? revision = null) =>
        J(await hf.ListGgufAsync(repo, revision, CancellationToken.None));

    [McpServerTool(Name = "add_model_from_huggingface"), Description("Create a model profile for a Hugging Face GGUF and start downloading it to the models directory. Returns a job id to poll with list_jobs.")]
    public async Task<string> AddModelFromHuggingFace(
        [Description("Repo id, e.g. unsloth/Qwen3-8B-GGUF")] string repo,
        [Description("GGUF file path in the repo (first part for split files)")] string file,
        [Description("Profile name (default derived from file name)")] string? name = null,
        [Description("Optional mmproj file for vision models")] string? mmproj = null,
        [Description("Optional JSON object with start parameters (see update_model)")] string? paramsJson = null,
        [Description("Optional draft model file in the same repo for speculative decoding (e.g. a DSpark/MTP head)")] string? draft = null)
    {
        var profile = string.IsNullOrWhiteSpace(paramsJson) ? null : JsonSerializer.Deserialize<ModelProfile>(paramsJson, ModelService.Json);
        var (model, job) = await models.AddFromHuggingFaceAsync(repo, file, name, null, mmproj, profile, CancellationToken.None, draft);
        return J(new { model, jobId = job.Id });
    }

    [McpServerTool(Name = "download_model"), Description("(Re)download a profile's Hugging Face source. Partial downloads resume.")]
    public string DownloadModel(string name) => J(models.StartDownload(name));

    [McpServerTool(Name = "hf_token_status", ReadOnly = true), Description("Which Hugging Face token is in use and the stored tokens (masked). Tokens are added by the user in the dashboard.")]
    public string HfTokenStatus() => J(new { active = config.Current.HuggingFace.ActiveToken, source = hf.ActiveToken().Source, tokens = secrets.List() });

    // ------------------------------------------------------------ llama.cpp

    [McpServerTool(Name = "list_llamacpp_builds", ReadOnly = true), Description("Installed llama.cpp builds and which one is active.")]
    public string ListBuilds() => J(builds.ListInstalled());

    [McpServerTool(Name = "list_llamacpp_releases", ReadOnly = true), Description("Recent llama.cpp GitHub releases that have a Windows build for the backend (default: configured backend and repo).")]
    public async Task<string> ListReleases(string? backend = null, int limit = 10,
        [Description("Other GitHub repo, e.g. unslothai/llama.cpp")] string? repo = null) =>
        J((await builds.ListReleasesAsync(backend, true, CancellationToken.None, repo)).Take(limit));

    [McpServerTool(Name = "install_llamacpp"), Description("Download and install a llama.cpp release from GitHub (default newest). Set repo to install a fork build (e.g. unslothai/llama.cpp) that a model needs; reference it from a model with build: 'owner/repo@tag'. Returns a job.")]
    public string InstallLlamaCpp(string? tag = null, [Description("cuda|vulkan|cpu|rocm|sycl|openvino (default: configured)")] string? backend = null,
        [Description("Pin this build in quipu.yaml")] bool pin = false,
        [Description("GitHub repo (default: llamaCpp.repo)")] string? repo = null) =>
        J(builds.Install(tag, backend, pin, instances.BuildDirsInUse, repo));

    [McpServerTool(Name = "activate_llamacpp_build"), Description("Pin an installed build (by id like b12617-cuda13.5) as the one used to start models.")]
    public string ActivateBuild(string id) => J(builds.Activate(id));

    // ------------------------------------------------------------ jobs & config

    [McpServerTool(Name = "list_jobs", ReadOnly = true), Description("Downloads/installs with progress.")]
    public string ListJobs() => J(jobs.List());

    [McpServerTool(Name = "cancel_job"), Description("Cancel a running download/install.")]
    public string CancelJob(string id) => J(new { canceled = jobs.Cancel(id) });

    [McpServerTool(Name = "get_config_yaml", ReadOnly = true), Description("The full quipu.yaml (config as code).")]
    public string GetConfigYaml() => config.ReadRaw();

    [McpServerTool(Name = "set_config_yaml", Destructive = true), Description("Replace quipu.yaml with new content. It is validated first; invalid YAML is rejected. Call apply_config afterwards to act on it.")]
    public string SetConfigYaml(string yaml)
    {
        config.ReplaceRaw(yaml);
        return J(new { ok = true, models = config.Current.Models.Count });
    }

    [McpServerTool(Name = "apply_config"), Description("Make the machine match quipu.yaml: install the wanted llama.cpp build, download missing models, start autostart models, optionally restart changed ones.")]
    public async Task<string> ApplyConfig(bool updateLlamaCpp = false, bool downloadModels = true, bool startAutostart = true, bool restartChanged = false) =>
        J(await reconcile.ApplyAsync(new ApplyOptions
        {
            UpdateLlamaCpp = updateLlamaCpp, DownloadModels = downloadModels, StartAutostart = startAutostart, RestartChanged = restartChanged,
        }, CancellationToken.None));
}
