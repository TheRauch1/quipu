# Quipu

*Quipu* ("KEE-poo", Quechua for "knot") were the knotted cords the Inca kept their records on, including counts of their llama herds. This one keeps the record of your llamas: which llama.cpp build, which models, which settings.

A Windows app for running [llama.cpp](https://github.com/ggml-org/llama.cpp) on your GPU. It:

- **installs the latest llama.cpp build** from GitHub (it picks the right asset for your GPU, e.g. CUDA 13.5 for an RTX 5090, and adds the CUDA runtime DLLs)
- **manages GGUF models**: browse Hugging Face, see which quantizations fit your VRAM, download (resumable, split files supported) to any drive you choose
- keeps **default + per-model start parameters** (context, GPU layers, flash attention, KV cache type, extra args, env vars)
- runs **one model at a time** by default (starting another stops the previous one) and **unloads idle models** from VRAM after 15 minutes. The next request reloads them automatically.
- **starts and stops `llama-server`** instances, with live logs, health checks and auto ports. Windows kills the servers too if Quipu exits.
- uses **config as code**: everything lives in one `quipu.yaml`, which hot-reloads when you edit it and has an **Apply** command
- handles **Hugging Face tokens**: several named tokens, checked against huggingface.co and encrypted with Windows DPAPI (never in the YAML)
- has a **REST API** (OpenAPI docs at `/docs`) and an **MCP server** (`/mcp`), so an AI assistant like Claude can manage it for you
- **exports pi config**: a `models.json` entry for the [pi coding agent](https://github.com/earendil-works/pi), per model
- runs in the tray: start and stop models, open folders, start with Windows

One self-contained `Quipu.exe` (.NET 11). You don't need the .NET runtime, and the build runs in Docker.

## Install

Download `Quipu.exe` from the latest GitHub release and run it. Put it in its own folder, for example `%USERPROFILE%\Quipu`.

The exe isn't code-signed, so Windows SmartScreen shows "Windows protected your PC" the first time you run it. Choose **More info → Run anyway**. Each release also has a `Quipu.exe.sha256` checksum file if you want to check your download.

Then open **http://127.0.0.1:5250**.

On first start Quipu creates `%USERPROFILE%\Quipu\quipu.yaml` and installs the newest llama.cpp build for your GPU:

```
%USERPROFILE%\Quipu\
  quipu.yaml         config as code
  models\<owner>\<repo>\*.gguf
  builds\b12617-cuda13.5\llama-server.exe ...
  logs\quipu.log, logs\<model>.log
```

Models take a lot of space. To keep them off C:, set `paths.models` to another drive: either in the dashboard (Config → Paths) or in `quipu.yaml`, for example `models: D:\llama\models`.

Command line:

| | |
|---|---|
| `Quipu.exe` | tray icon + dashboard |
| `Quipu.exe --headless` | no UI, logs to console (for a scheduled task or service) |
| `Quipu.exe --config X:\path\quipu.yaml` | use a specific config |
| `Quipu.exe --no-browser` | don't open the dashboard |

Config lookup order:

1. `--config`
2. `%QUIPU_CONFIG%`
3. `quipu.yaml` next to the exe, if it exists
4. `%USERPROFILE%\Quipu\quipu.yaml` (created if missing)

## Build from source

Needs Docker Desktop (Linux containers). You don't need a local .NET SDK.

```powershell
.\build.ps1                               # unit tests, then build -> dist\Quipu.exe (+ THIRD-PARTY-NOTICES.txt)
.\build.ps1 -SkipTests                    # build only
.\build.ps1 -Install -Run                 # build, copy to %USERPROFILE%\Quipu and start it
.\build.ps1 -Install -InstallDir D:\llama # install somewhere else
```

Or by hand:

```powershell
docker build --target test .                                    # unit tests
docker build --target export --output type=local,dest=dist .    # dist\Quipu.exe
```

- **CI:** GitHub Actions (`.github/workflows/build.yml`) runs the same two targets on every push and pull request.
- **Releases:** pushing a tag like `v1.1.0` publishes a release with the exe, its SHA-256 and the third-party notices.
- **Running tests without Docker:** with a .NET 11 SDK, run `dotnet test --project tests/Quipu.Tests`.
- **What the tests cover:** they compile the platform-neutral parts of the app (config, command line, release-asset picking, network access rules), so they also run on Linux and macOS.

## Config as code

See [`quipu.example.yaml`](quipu.example.yaml) for every option. In short:

```yaml
paths:
  models: D:\llama\models
llamaCpp:
  backend: auto        # cuda on NVIDIA
  version: latest      # or pin: b12617
defaults:
  flashAttn: auto
models:
  qwen3-8b:
    hf: { repo: unsloth/Qwen3-8B-GGUF, file: Qwen3-8B-Q4_K_M.gguf }
    port: 8081
    autostart: true
    ctxSize: 32768
    args: [--temp, "0.6"]
```

- **Editing the file:** Quipu reloads it on save. If you save invalid YAML, it keeps the last good config and shows the error in the dashboard.
- **Changes from the dashboard or API** are written back to the file. Your comments are not kept, so keep the annotated version in git if you want them.
- **Apply** (`POST /api/apply` or the Config page) installs the pinned or latest build, downloads missing `hf:` models, starts `autostart` models, and can restart models whose parameters changed.
- **Restart badge:** a running model whose parameters changed shows "restart to apply changes".

Model params map to llama-server flags: `ctxSize`→`-c`, `gpuLayers`→`-ngl`, `flashAttn`→`-fa`, `fit`→`-fit`, `threads`→`-t`, `parallel`→`-np`, `batchSize`→`-b`, `uBatchSize`→`-ub`, `cacheTypeK/V`→`-ctk/-ctv`, `jinja`, `mlock`, `noMmap`, `idleUnloadMinutes`→`--sleep-idle-seconds`, plus raw `args` and `env`. A model value overrides the default; `args` are appended and `env` is merged.

### One model at a time

`server.maxActiveModels` (default **1**, `0` = no limit) caps how many llama-servers run at once. Starting a model stops the least recently used one first, so only one model holds VRAM at a time. The tray shows a notification when that happens. Apply only autostarts that many models.

### Unload when idle

`idleUnloadMinutes` (default **15**, `0` = never) frees a model's VRAM after that many minutes without requests. llama-server keeps running on the same port with the model unloaded, and the next request reloads it. That first request is slower by the load time, for example about 9 s for a 17 GB model from an NVMe SSD.

- **Dashboard:** the card shows "idle unload in 23m", then **Sleeping / unloaded (idle)**.
- **Tray:** shows a notification when a model is unloaded.
- **Per model:** set it on the model to override the default, for example `idleUnloadMinutes: 0` to keep one model always loaded.
- **Restart needed:** a running model has to be restarted to pick up a changed value.

### Speculative decoding and per-model builds

- **Draft models:** `draft:` (with `hf.draft:` for downloading) adds `-md <file>` for speculative decoding.
- **Spec type:** `specType:` sets `--spec-type`. If it's empty, Quipu infers it from the draft's file name (`dspark`→`draft-dspark`, `mtp`→`draft-mtp`, `eagle`→`draft-eagle3`, `dflash`→`draft-dflash`, otherwise `draft-simple`).
- **Fork builds:** some models need a specific llama.cpp fork. For example, Unsloth's Inkling-2-Mini GGUFs need `unslothai/llama.cpp` for their looped-layer layout and DSpark. Set `build: owner/repo@tag` on that model; **Apply** installs that build next to the mainline one, and only that model uses it. `llamaCpp.version: latest` keeps following the main repo.
- **Missing CUDA runtime:** some fork builds ship without `cudart64_*.dll`, which makes llama-server silently fall back to CPU. When that happens, Quipu adds ggml-org's runtime package with the same CUDA major version.

```yaml
  inkling-2-mini:
    build: unslothai/llama.cpp@b12588-mix-c0c3de8
    hf:
      repo: unsloth/Inkling-2-Mini-GGUF
      file: Inkling-2-Mini-UD-Q4_K_M.gguf
      mmproj: mmproj-F16.gguf
      draft: dspark/dspark-Inkling-2-Mini-BF16.gguf
    specType: draft-dspark
    ctxSize: 65536
    gpuLayers: 99
    jinja: true
    args: [--spec-draft-n-max, 2, --n-gpu-layers-draft, 99, --temp, "1.0", --top-p, "1.0", --top-k, 0, --min-p, "0.0", -n, 16384]
```

Each server gets `LLAMA_CACHE=<models dir>` and `HF_TOKEN=<active token>`, so llama.cpp's own `-hf` downloads also land in the models folder.

## API

Interactive docs: **http://127.0.0.1:5250/docs**. OpenAPI: `/openapi/v1.json`.

| Area | Endpoints |
|---|---|
| System | `GET /api/status`, `GET /api/gpu`, `GET /api/logs`, `POST /api/apply` |
| Config | `GET/PATCH /api/config`, `GET/PUT /api/config/yaml`, `POST /api/config/validate`, `POST /api/config/reload` |
| llama.cpp | `GET /api/llamacpp/releases?repo=`, `GET /api/llamacpp/builds`, `POST /api/llamacpp/install` (`{tag, backend, repo, pin}`), `POST /api/llamacpp/builds/{id}/activate`, `POST /api/llamacpp/unpin`, `DELETE /api/llamacpp/builds/{id}` |
| Models | `GET /api/models`, `GET/PUT/PATCH/DELETE /api/models/{name}`, `POST /api/models/{name}/start\|stop\|restart\|download\|rename`, `GET /api/models/{name}/logs?since=`, `GET /api/models/{name}/command`, `GET /api/files` |
| Hugging Face | `GET /api/hf/search?q=`, `GET /api/hf/files?repo=`, `POST /api/hf/add`, `GET /api/hf/tokens`, `PUT/DELETE /api/hf/tokens/{name}`, `POST /api/hf/tokens/{name}/activate\|validate`, `POST /api/hf/tokens/deactivate` |
| Jobs | `GET /api/jobs`, `GET /api/jobs/{id}`, `DELETE /api/jobs/{id}` (cancel), `POST /api/jobs/clear` |

Examples:

```powershell
# install the newest llama.cpp
irm -Method Post http://127.0.0.1:5250/api/llamacpp/install -ContentType application/json -Body '{}'

# add a model from Hugging Face and download it to the models folder
irm -Method Post http://127.0.0.1:5250/api/hf/add -ContentType application/json `
  -Body '{"repo":"unsloth/Qwen3-8B-GGUF","file":"Qwen3-8B-Q4_K_M.gguf","name":"qwen3-8b","profile":{"ctxSize":32768}}'

# change a parameter, then restart
irm -Method Patch http://127.0.0.1:5250/api/models/qwen3-8b -ContentType application/json -Body '{"ctxSize":65536}'
irm -Method Post  http://127.0.0.1:5250/api/models/qwen3-8b/restart
```

Every running model serves the OpenAI-compatible API at `http://127.0.0.1:<port>/v1` and llama.cpp's chat UI at `http://127.0.0.1:<port>/`.

## Use a model in pi

Each model card has a **pi** button. It shows a `models.json` for the [pi coding agent](https://github.com/earendil-works/pi) that points at that model's llama-server, with **Copy**, **Copy model entry** and **Download** buttons.

- **Save it as** `%USERPROFILE%\.pi\agent\models.json` (`~/.pi/agent/models.json` on Linux/macOS), then pick the model in pi with `/model`.
- **Already have a `quipu` provider in that file?** Add only the model entry to its `models` list.
- **What it fills in:** the provider is `quipu` (`openai-completions`). The entry gets the model id (`alias`), its `baseUrl`, the context window per slot, and `maxTokens` from `-n`.
- **Vision and reasoning:** while the model is running, Quipu reads the server's `/props`, which tells it whether the model takes images, whether it reasons, and which reasoning-effort levels pi's thinking levels map to. If the model isn't running, these are guessed from the profile.
- **Address:** "pi connects to" picks the address pi uses, `127.0.0.1` or this PC's LAN address. It defaults to the address you opened the dashboard with.
- **API and MCP:** `GET /api/models/{name}/export/pi?host=` and the `export_pi_config` MCP tool return the same file.

## Use it from the local network

### Only the model endpoints (recommended)

Other machines get the OpenAI-compatible model APIs (`http://<this PC's IP>:8080/v1`). The dashboard, REST API and MCP stay on this PC:

```yaml
server:
  listen: http://127.0.0.1:5250    # management stays local
defaults:
  host: 0.0.0.0                    # model servers on all interfaces
```

Restart the models after changing `host`. In the pi export, pick this PC's LAN address in the host list.

- **No client filter in llama-server:** llama-server can't restrict which machines connect. Limit the model ports with a Windows Firewall rule. Run this once in an admin PowerShell:

  ```powershell
  New-NetFirewallRule -DisplayName "Quipu models (LAN)" -Direction Inbound -Action Allow -Protocol TCP -LocalPort 8080-8099 -RemoteAddress 10.0.0.0/24 -Profile Private
  ```

- **Windows "allow access" prompt:** if Windows asks the first time a server starts, choose **Cancel**. The rule above already covers it, and allowing it there opens the program to every network.
- **Optional key:** to require one on the model APIs too, add `--api-key <key>` to `defaults.args`. The pi export picks it up.

### The dashboard too

```yaml
server:
  listen: http://0.0.0.0:5250
  allowedNetworks: [10.0.0.0/24]   # localhost always works
```

Restart Quipu after changing `listen`, then open `http://<this PC's IP>:5250` (or its Windows computer name) from another machine. `allowedNetworks` makes Quipu reject dashboard, API and MCP requests from any other network with 403. Add port `5250` to the firewall rule.

## Let Claude manage it (MCP)

Quipu has a built-in MCP server at `/mcp` (streamable HTTP). Add it to Claude Code:

```powershell
claude mcp add --transport http quipu http://127.0.0.1:5250/mcp
```

If you set `server.apiKey`, add `--header "X-Api-Key: <key>"`.

The 25 tools cover status/GPU, listing, starting, stopping and restarting models, logs, creating and updating profiles, default params, Hugging Face search, file listing and download, llama.cpp releases, install and pinning, jobs, and reading, replacing and applying `quipu.yaml`. Token values can't be set over MCP; add those yourself in the dashboard.

## Security

- **Network:** it listens on `127.0.0.1` only by default.
- **Other machines:** listening on `0.0.0.0` alone doesn't open Quipu to them. They are refused until you set `server.allowedNetworks` (e.g. `10.0.0.0/24`) or `server.apiKey`. Localhost always works.
- **Warnings:** the dashboard shows a warning while Quipu or a model server can be reached from the network without a key.
- **Browser access:** cross-origin requests to `/api` and `/mcp` are rejected, so web pages can't drive the API. Quipu also only answers to an IP address, `localhost` or this PC's name, so a web page can't get around that by pointing its own domain at your PC (DNS rebinding).
- **API key:** with `server.apiKey` set, every `/api` and `/mcp` call must send it (`X-Api-Key` or `Authorization: Bearer`).
- **Tokens:** stored in `%LOCALAPPDATA%\Quipu\secrets.json`, encrypted with DPAPI for your Windows user. The API only ever returns them masked.
- **llama-server:** it has no client filter of its own. If you bind it beyond localhost, add `--api-key <key>` to a model's `args` (or to `defaults.args`), or limit its ports with a firewall rule.

Please report security problems privately through GitHub's "Report a vulnerability" (Security tab) rather than in a public issue.

## Project layout

```
Dockerfile, build.ps1         Docker test + build -> win-x64 single-file exe
global.json                   test runner (Microsoft.Testing.Platform)
.github/workflows/build.yml   CI: tests, build, release on v* tags
LICENSE                       MIT license of Quipu itself
THIRD-PARTY-NOTICES.txt       licenses of the bundled components (both shipped with every build)
src/Quipu/
  Program.cs                  host, DI, tray/headless startup
  Config/                     quipu.yaml model, load/validate/save/hot-reload, network access rules
  Services/
    BuildManager.cs           GitHub releases, install/activate/prune
    AssetPicker.cs            which release zip fits the backend / CUDA version
    InstanceManager.cs        llama-server processes, logs, health, ports, idle sleep, one-model limit
    CommandBuilder.cs         profile + defaults -> command line
    ModelService.cs           profiles + disk + downloads
    PiExport.cs               models.json for the pi coding agent
    HuggingFaceService.cs     search, file listing, whoami
    SecretStore.cs            DPAPI token storage
    ReconcileService.cs       Apply + startup autostart
    JobService.cs             background jobs + resumable downloader
    GpuService.cs             nvidia-smi telemetry
    JobObject.cs              kill-on-close job object for child processes
  Api/                        REST endpoints, network/API-key/origin guard, error mapping
  Mcp/QuipuTools.cs           MCP tools
  Tray/TrayContext.cs         tray icon
  wwwroot/                    dashboard (embedded into the exe)
tests/Quipu.Tests/            xUnit tests for the platform-neutral parts
```

## License

[MIT](LICENSE). Bundled components keep their own licenses; see [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt). llama.cpp builds and models are downloaded at runtime under their own licenses.
