namespace Quipu.Config;

/// <summary>Root of quipu.yaml — the whole desired state of the app, as code.</summary>
public sealed class QuipuConfig
{
    public PathsConfig Paths { get; set; } = new();
    public LlamaCppConfig LlamaCpp { get; set; } = new();
    public ServerConfig Server { get; set; } = new();
    public HuggingFaceConfig HuggingFace { get; set; } = new();

    /// <summary>Start parameters applied to every model unless the model overrides them.</summary>
    public ModelParams Defaults { get; set; } = new() { Host = "127.0.0.1", FlashAttn = "auto", IdleUnloadMinutes = 15 };

    public Dictionary<string, ModelProfile> Models { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class PathsConfig
{
    /// <summary>Where GGUF models live. Relative model paths resolve against this.</summary>
    public string Models { get; set; } = "";
    /// <summary>Where llama.cpp builds get extracted (one folder per build).</summary>
    public string Installs { get; set; } = "";
    /// <summary>Server log files.</summary>
    public string Logs { get; set; } = "";
}

public sealed class LlamaCppConfig
{
    /// <summary>GitHub repo releases are pulled from.</summary>
    public string Repo { get; set; } = "ggml-org/llama.cpp";
    /// <summary>auto | cuda | vulkan | cpu | rocm | sycl | openvino</summary>
    public string Backend { get; set; } = "auto";
    /// <summary>auto (highest available) or a version such as "13.5" / "12.4".</summary>
    public string CudaVersion { get; set; } = "auto";
    /// <summary>"latest" = newest installed build, or a pin: a tag like "b12617", a build id, or "owner/repo@tag" for a fork.</summary>
    public string Version { get; set; } = "latest";
    /// <summary>Install the newest release automatically at startup.</summary>
    public bool AutoUpdate { get; set; }
    /// <summary>How many old builds to keep after installing a new one (0 = keep all).</summary>
    public int KeepBuilds { get; set; } = 3;
}

public sealed class ServerConfig
{
    public string Listen { get; set; } = "http://127.0.0.1:5250";
    /// <summary>If set, every /api and /mcp call must send it (X-Api-Key header or Bearer token).</summary>
    public string? ApiKey { get; set; }
    /// <summary>
    /// Client networks (CIDR, e.g. 10.0.0.0/24) allowed to reach the dashboard, API and MCP from other machines.
    /// Localhost is always allowed. Empty = other machines only when server.apiKey is set.
    /// </summary>
    public List<string> AllowedNetworks { get; set; } = [];
    public bool OpenBrowserOnStart { get; set; } = true;
    /// <summary>First port handed out to models that don't pin one.</summary>
    public int ModelPortStart { get; set; } = 8080;
    /// <summary>How many models may run at once. Starting another one stops the least recently used. 0 = no limit.</summary>
    public int MaxActiveModels { get; set; } = 1;
}

public sealed class HuggingFaceConfig
{
    public string Endpoint { get; set; } = "https://huggingface.co";
    /// <summary>Name of the stored token to use (tokens themselves are kept encrypted outside this file).</summary>
    public string? ActiveToken { get; set; }
}

/// <summary>Typed llama-server start parameters. null = not set (inherit / let llama.cpp decide).</summary>
public class ModelParams
{
    public string? Host { get; set; }
    /// <summary>-c. 0 = take from model.</summary>
    public int? CtxSize { get; set; }
    /// <summary>-ngl: number, "auto" or "all".</summary>
    public string? GpuLayers { get; set; }
    /// <summary>-fa: on | off | auto</summary>
    public string? FlashAttn { get; set; }
    /// <summary>-fit: on | off (auto-fit unset args to VRAM).</summary>
    public string? Fit { get; set; }
    public int? Threads { get; set; }
    public int? Parallel { get; set; }
    public int? BatchSize { get; set; }
    public int? UBatchSize { get; set; }
    /// <summary>-ctk e.g. f16, q8_0, q4_0</summary>
    public string? CacheTypeK { get; set; }
    public string? CacheTypeV { get; set; }
    public bool? Jinja { get; set; }
    public bool? Mlock { get; set; }
    public bool? NoMmap { get; set; }
    /// <summary>Unload the model from VRAM after this many minutes without requests (llama-server --sleep-idle-seconds).
    /// The server keeps its port and reloads the model automatically on the next request. 0 = never.</summary>
    public int? IdleUnloadMinutes { get; set; }
    /// <summary>Extra raw command-line arguments, appended verbatim.</summary>
    public List<string>? Args { get; set; }
    /// <summary>Extra environment variables for the server process.</summary>
    public Dictionary<string, string>? Env { get; set; }
}

public sealed class ModelProfile : ModelParams
{
    public string? Description { get; set; }
    /// <summary>GGUF path; relative paths resolve against paths.models.</summary>
    public string? File { get; set; }
    /// <summary>Multimodal projector GGUF (relative or absolute).</summary>
    public string? Mmproj { get; set; }
    /// <summary>Draft model for speculative decoding (-md), e.g. a DSpark / MTP / EAGLE3 head or a small model.</summary>
    public string? Draft { get; set; }
    /// <summary>--spec-type, e.g. draft-dspark, draft-mtp, draft-eagle3, draft-simple, ngram-mod.
    /// If a draft is set and this is empty, it is inferred from the draft's file name.</summary>
    public string? SpecType { get; set; }
    /// <summary>Where to download the model from if it's missing.</summary>
    public HfSource? Hf { get; set; }
    /// <summary>Fixed port; if null a free one is assigned at start.</summary>
    public int? Port { get; set; }
    /// <summary>llama.cpp build for this model instead of the active one: an installed build id, or
    /// "owner/repo@tag" (e.g. unslothai/llama.cpp@b12588-mix-c0c3de8), which Apply installs if missing.</summary>
    public string? Build { get; set; }
    /// <summary>Model name exposed on the OpenAI API (-a). Defaults to the profile name.</summary>
    public string? Alias { get; set; }
    /// <summary>Start automatically when Quipu starts / on apply.</summary>
    public bool Autostart { get; set; }
}

public sealed class HfSource
{
    public string Repo { get; set; } = "";
    public string File { get; set; } = "";
    public string? Revision { get; set; }
    public string? Mmproj { get; set; }
    /// <summary>Draft model file in the same repo (e.g. dspark/dspark-Model-BF16.gguf).</summary>
    public string? Draft { get; set; }
}
