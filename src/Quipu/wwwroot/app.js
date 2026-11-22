"use strict";

// ================================================================ helpers
const $ = (s, el = document) => el.querySelector(s);
const $$ = (s, el = document) => [...el.querySelectorAll(s)];
const esc = (v) => String(v ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);
const fmtBytes = (b) => {
  if (b == null) return "–";
  const u = ["B", "KB", "MB", "GB", "TB"]; let i = 0; let n = b;
  while (n >= 1024 && i < u.length - 1) { n /= 1024; i++; }
  return `${n.toFixed(n >= 100 || i === 0 ? 0 : 1)} ${u[i]}`;
};
const fmtCtx = (n) => (n % 1024 === 0 ? `${n / 1024}k` : fmtNum(n));
const fmtNum = (n) => n >= 1e6 ? (n / 1e6).toFixed(1) + "M" : n >= 1e3 ? (n / 1e3).toFixed(1) + "k" : String(n ?? 0);
const ago = (d) => {
  if (!d) return "–";
  const s = (Date.now() - new Date(d).getTime()) / 1000;
  if (s < 60) return "just now";
  if (s < 3600) return `${Math.floor(s / 60)} min ago`;
  if (s < 86400) return `${Math.floor(s / 3600)} h ago`;
  return `${Math.floor(s / 86400)} d ago`;
};
const storage = {
  get(k) { try { return localStorage.getItem(k); } catch { return null; } },
  set(k, v) { try { v == null ? localStorage.removeItem(k) : localStorage.setItem(k, v); } catch { } },
};

function toast(msg, kind = "") {
  const el = document.createElement("div");
  el.className = `toast ${kind}`;
  el.textContent = msg;
  $("#toasts").append(el);
  setTimeout(() => el.remove(), kind === "err" ? 7000 : 3500);
}

async function api(path, { method = "GET", body, raw = false, text = false } = {}) {
  const headers = {};
  const key = storage.get("quipu.apiKey");
  if (key) headers["X-Api-Key"] = key;
  let payload;
  if (body !== undefined) {
    if (raw) { headers["Content-Type"] = "text/yaml"; payload = body; }
    else { headers["Content-Type"] = "application/json"; payload = JSON.stringify(body); }
  }
  const res = await fetch(`/api${path}`, { method, headers, body: payload });
  if (res.status === 401) {
    const k = prompt("This Quipu requires an API key:");
    if (k) { storage.set("quipu.apiKey", k); return api(path, { method, body, raw, text }); }
    throw new Error("API key required");
  }
  if (res.status === 204 || res.status === 202) return null;
  const ct = res.headers.get("content-type") || "";
  const data = text || !ct.includes("json") ? await res.text() : await res.json();
  if (!res.ok) throw new Error((data && data.error) || (typeof data === "string" && data) || `HTTP ${res.status}`);
  return data;
}

async function act(promise, okMsg) {
  try {
    const r = await promise;
    if (okMsg) toast(okMsg, "ok");
    refresh();
    return r;
  } catch (e) {
    toast(e.message, "err");
    throw e;
  }
}

/** Split "--temp 0.7 --top-k 20" / multi-line text into args, honoring quotes. */
function splitArgs(s) {
  const out = []; let cur = ""; let q = null; let has = false;
  for (const ch of s) {
    if (q) { if (ch === q) q = null; else cur += ch; continue; }
    if (ch === '"' || ch === "'") { q = ch; has = true; continue; }
    if (/\s/.test(ch)) { if (cur || has) { out.push(cur); cur = ""; has = false; } continue; }
    cur += ch;
  }
  if (cur || has) out.push(cur);
  return out;
}
const isDraftFile = (f) => /dspark|mtp|eagle|dflash|draft/i.test(f);
const specFromName = (f) => {
  const n = f.toLowerCase();
  return n.includes("dspark") ? "draft-dspark" : n.includes("dflash2") ? "draft-dflash2" : n.includes("dflash") ? "draft-dflash"
    : n.includes("mtp") ? "draft-mtp" : n.includes("eagle") ? "draft-eagle3" : "draft-simple";
};
const joinArgs = (a) => (a || []).map((x) => (/\s/.test(x) || x === "" ? `"${x}"` : x)).join(" ");

// ================================================================ state
const state = { status: null, models: [], files: null, jobs: [], view: "models", hfRepo: null };

// ================================================================ navigation
const titles = { models: "Models", discover: "Discover", llamacpp: "llama.cpp", tokens: "Hugging Face tokens", config: "Configuration" };
function show(view) {
  state.view = view;
  $$("#nav a").forEach((a) => a.classList.toggle("active", a.dataset.view === view));
  $$(".view").forEach((v) => v.classList.toggle("hidden", v.id !== `view-${view}`));
  $("#view-title").textContent = titles[view];
  history.replaceState(null, "", `#${view}`);
  if (view === "llamacpp") loadLlamaCpp();
  if (view === "tokens") loadTokens();
  if (view === "config") loadConfig();
  if (view === "discover") setTimeout(() => $("#hf-query").focus(), 50);
}
$$("#nav a").forEach((a) => a.addEventListener("click", () => show(a.dataset.view)));
document.addEventListener("click", (e) => {
  const g = e.target.closest("[data-goto]");
  if (g) show(g.dataset.goto);
});

// ================================================================ polling
let refreshing = false;
async function refresh() {
  if (refreshing) return;
  refreshing = true;
  try {
    const [status, models, jobs] = await Promise.all([api("/status"), api("/models"), api("/jobs")]);
    state.status = status; state.models = models; state.jobs = jobs;
    renderStatus();
    renderJobs();
    if (state.view === "models") renderModels();
  } catch (e) {
    $("#status-chips").innerHTML = `<span class="chip"><span class="dot err"></span>${esc(e.message)}</span>`;
  } finally {
    refreshing = false;
  }
}
let filesAt = 0;
async function refreshFiles(force) {
  if (!force && Date.now() - filesAt < 10000) return;
  filesAt = Date.now();
  try { state.files = await api("/files"); renderFiles(); } catch { }
}

// ================================================================ status / sidebar
function renderStatus() {
  const s = state.status;
  $("#brand-version").textContent = `v${s.version} · ${s.backend}`;
  const b = s.llamaCpp;
  const running = s.models.running;
  const chips = [
    b ? `<span class="chip"><span class="dot ok"></span>llama.cpp <b>${esc(b.tag)}</b> ${esc(b.backend)}${b.cudaVersion ? " " + esc(b.cudaVersion) : ""}</span>`
      : `<span class="chip"><span class="dot err"></span>llama.cpp not installed</span>`,
    `<span class="chip"><span class="dot ${running ? "ok" : ""}"></span><b>${running}</b>/${s.models.configured} running</span>`,
    `<span class="chip">HF token <b>${s.huggingFace.hasToken ? esc(s.huggingFace.tokenSource.replace("stored:", "")) : "none"}</b></span>`,
  ];
  if (s.disk?.totalBytes) chips.push(`<span class="chip">${esc(s.disk.drive)} <b>${fmtBytes(s.disk.freeBytes)}</b> free</span>`);
  $("#status-chips").innerHTML = chips.join("");

  const err = $("#config-error");
  err.classList.toggle("hidden", !s.configError);
  if (s.configError) err.innerHTML = `<b>quipu.yaml has an error — still using the last good config.</b><br>${esc(s.configError)}`;
  const warn = $("#security-warnings");
  const warnings = s.warnings || [];
  warn.classList.toggle("hidden", !warnings.length);
  if (warnings.length) warn.innerHTML = `<b>Network access</b><br>${warnings.map(esc).join("<br>")}`;

  const g = s.gpus?.[0];
  $("#gpu-card").innerHTML = g ? `
    <div class="name">${esc(g.name)}</div>
    <div class="row"><span>VRAM</span><span>${(g.memoryUsedMiB / 1024).toFixed(1)} / ${(g.memoryTotalMiB / 1024).toFixed(1)} GB</span></div>
    <div class="meter"><div style="width:${(100 * g.memoryUsedMiB / g.memoryTotalMiB).toFixed(1)}%"></div></div>
    <div class="row"><span>Load ${g.utilizationPct ?? "–"}%</span><span>${g.temperatureC ?? "–"}°C · ${g.powerW != null ? Math.round(g.powerW) + " W" : "–"}</span></div>`
    : `<div class="muted">No NVIDIA GPU telemetry</div>`;
}

// ================================================================ models
const stateDot = { Ready: "ok", Sleeping: "sleep", Starting: "warn", Stopping: "warn", Failed: "err", Stopped: "" };
const fmtTime = (t) => (t ? new Date(t).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" }) : "");
function fmtUntil(t) {
  const s = Math.max(0, (new Date(t) - Date.now()) / 1000);
  if (s < 60) return `${Math.round(s)}s`;
  const m = Math.round(s / 60);
  return m < 60 ? `${m}m` : `${Math.floor(m / 60)}h ${m % 60}m`;
}

function renderModels() {
  const grid = $("#models-grid");
  if (!state.models.length) {
    grid.innerHTML = `<div class="card empty" style="grid-column:1/-1">
      No models yet. <a data-goto="discover" href="#">Find one on Hugging Face</a> or create a profile for a GGUF already on disk.</div>`;
    refreshFiles();
    return;
  }
  const jobsById = Object.fromEntries(state.jobs.map((j) => [j.id, j]));
  grid.innerHTML = state.models.map((m) => {
    const p = m.profile;
    const running = m.state === "Ready" || m.state === "Starting";
    const job = m.downloadJobId ? jobsById[m.downloadJobId] : null;
    const tags = [
      m.quant && `<span class="tag accent">${esc(m.quant)}</span>`,
      m.sizeBytes && `<span class="tag">${fmtBytes(m.sizeBytes)}</span>`,
      p.ctxSize && `<span class="tag">ctx ${fmtCtx(p.ctxSize)}</span>`,
      p.mmproj || p.hf?.mmproj ? `<span class="tag">vision</span>` : "",
      p.draft || p.hf?.draft || p.specType ? `<span class="tag accent">spec ${esc((p.specType || specFromName(p.draft || p.hf?.draft || "")).replace("draft-", ""))}</span>` : "",
      p.build && `<span class="tag" title="${esc(p.build)}">build ${esc(p.build.split("@").pop())}</span>`,
      p.autostart && `<span class="tag ok">autostart</span>`,
      !m.downloaded && !job && `<span class="tag err">file missing</span>`,
      m.needsRestart && `<span class="tag warn">restart to apply changes</span>`,
      m.asleep && `<span class="tag" title="Unloaded from VRAM since ${esc(fmtTime(m.asleepSince))}. The next request reloads it.">unloaded (idle)</span>`,
      !m.asleep && m.unloadAt && `<span class="tag" title="Unloads from VRAM after ${m.idleUnloadMinutes} min without requests">idle unload in ${fmtUntil(m.unloadAt)}</span>`,
      !running && m.idleUnloadMinutes && `<span class="tag" title="Unloads from VRAM after ${m.idleUnloadMinutes} min without requests">idle ${m.idleUnloadMinutes}m</span>`,
    ].filter(Boolean).join("");
    const shown = m.state === "Ready" && m.asleep ? "Sleeping" : m.state;
    return `<div class="model ${running ? "running" : ""}" data-name="${esc(m.name)}">
      <div class="model-head">
        <div style="min-width:0">
          <div class="model-name">${esc(m.name)}</div>
          <div class="model-desc">${esc(p.description || p.hf?.repo || p.file || "")}</div>
        </div>
        <span class="state"><span class="dot ${stateDot[shown] ?? ""}"></span>${esc(shown)}</span>
      </div>
      <div class="model-meta">${tags}</div>
      ${job ? `<div><div class="progress"><div style="width:${job.percent ?? 0}%"></div></div>
        <div class="muted small" style="margin-top:4px">${esc(job.message || "")} · ${job.percent ?? 0}% · ${fmtBytes(job.bytesPerSecond)}/s</div></div>` : ""}
      ${m.url ? `<div class="model-url"><span>${esc(viewUrl(m.url))}/v1</span><a href="${esc(viewUrl(m.url))}" target="_blank">open chat ↗</a></div>` : ""}
      ${m.error && m.state === "Failed" ? `<div class="model-err">${esc(m.error)}</div>` : ""}
      <div class="model-actions">
        ${running
          ? `<button class="btn small" data-act="stop">■ Stop</button>${m.needsRestart ? `<button class="btn small primary" data-act="restart">↻ Restart</button>` : `<button class="btn small ghost" data-act="restart">↻</button>`}`
          : m.downloaded ? `<button class="btn small primary" data-act="start">▶ Start</button>`
          : p.hf && !job ? `<button class="btn small primary" data-act="download">⭳ Download</button>` : ""}
        <button class="btn small ghost" data-act="logs">Logs</button>
        <button class="btn small ghost" data-act="edit">Edit</button>
        <button class="btn small ghost" data-act="pi" title="Export a models.json entry for the pi coding agent">pi</button>
        <div class="spacer"></div>
        <button class="btn small ghost danger" data-act="delete" title="Remove">✕</button>
      </div>
    </div>`;
  }).join("");
  refreshFiles();
}

$("#models-grid").addEventListener("click", async (e) => {
  const btn = e.target.closest("[data-act]");
  if (!btn) return;
  const name = btn.closest("[data-name]").dataset.name;
  const n = encodeURIComponent(name);
  btn.disabled = true;
  try {
    switch (btn.dataset.act) {
      case "start": await act(api(`/models/${n}/start`, { method: "POST" }), `Starting ${name}…`); break;
      case "stop": await act(api(`/models/${n}/stop`, { method: "POST" }), `Stopped ${name}`); break;
      case "restart": await act(api(`/models/${n}/restart`, { method: "POST" }), `Restarting ${name}…`); break;
      case "download": await act(api(`/models/${n}/download`, { method: "POST" }), `Downloading ${name}`); break;
      case "logs": openLogs(name); break;
      case "edit": openEditor(name); break;
      case "pi": await openPiExport(name); break;
      case "delete": openDelete(name); break;
    }
  } catch { } finally { btn.disabled = false; }
});

$("#btn-stop-all").addEventListener("click", async () => {
  const running = state.models.filter((m) => m.state === "Ready" || m.state === "Starting");
  for (const m of running) await api(`/models/${encodeURIComponent(m.name)}/stop`, { method: "POST" }).catch(() => { });
  toast(`Stopped ${running.length} model(s)`, "ok");
  refresh();
});

function renderFiles() {
  const f = state.files;
  if (!f) return;
  $("#disk-summary").textContent = `${f.disk.modelsDir} · ${fmtBytes(f.disk.modelBytes)} in models · ${fmtBytes(f.disk.freeBytes)} free on ${f.disk.drive}`;
  const t = $("#files-table");
  if (!f.files.length) { t.innerHTML = `<tr><td class="empty">No GGUF files in ${esc(f.disk.modelsDir)} yet.</td></tr>`; return; }
  t.innerHTML = `<tr><th>File</th><th>Quant</th><th class="num">Size</th><th>Used by</th><th></th></tr>` + f.files.map((x) => `
    <tr>
      <td class="mono">${esc(x.path)}${x.parts > 1 ? ` <span class="tag">${x.parts} parts</span>` : ""}${x.complete ? "" : ` <span class="tag err">incomplete</span>`}${x.isMmproj ? ` <span class="tag">mmproj</span>` : ""}</td>
      <td>${esc(x.quant || "")}</td>
      <td class="num">${fmtBytes(x.size)}</td>
      <td>${x.usedBy.map((u) => `<span class="tag accent">${esc(u)}</span>`).join(" ") || `<span class="muted">—</span>`}</td>
      <td class="right">${!x.isMmproj && !x.usedBy.length ? `<button class="btn small" data-file="${esc(x.path)}">Create profile</button>` : ""}</td>
    </tr>`).join("");
}
$("#files-table").addEventListener("click", (e) => {
  const b = e.target.closest("[data-file]");
  if (b) openEditor(null, { file: b.dataset.file });
});
$("#btn-new-model").addEventListener("click", () => openEditor(null, {}));

// ================================================================ drawer
function openDrawer(title, body, foot = "") {
  $("#drawer-title").textContent = title;
  $("#drawer-body").innerHTML = body;
  $("#drawer-foot").innerHTML = foot;
  $("#drawer").classList.remove("hidden");
  $("#drawer-backdrop").classList.remove("hidden");
}
function closeDrawer() {
  $("#drawer").classList.add("hidden");
  $("#drawer-backdrop").classList.add("hidden");
  clearInterval(logTimer);
}
$("#drawer-close").addEventListener("click", closeDrawer);
$("#drawer-backdrop").addEventListener("click", closeDrawer);
document.addEventListener("keydown", (e) => { if (e.key === "Escape") closeDrawer(); });

// ---------------------------------------------------------------- params form (shared by models + defaults)
const tri = (v) => (v === true ? "true" : v === false ? "false" : "");
function paramFields(p, { forDefaults = false } = {}) {
  const sel = (id, val, opts) => `<select id="${id}">${opts.map(([v, l]) => `<option value="${v}" ${String(val ?? "") === v ? "selected" : ""}>${l}</option>`).join("")}</select>`;
  const inh = forDefaults ? "llama.cpp default" : "inherit";
  const kv = [["", inh], ["f16", "f16"], ["bf16", "bf16"], ["q8_0", "q8_0"], ["q5_1", "q5_1"], ["q4_0", "q4_0"]];
  return `
  <div class="grid3">
    <label>Context size <span class="hint">-c (0 = model max)</span><input id="p-ctx" type="number" min="0" value="${esc(p.ctxSize ?? "")}" placeholder="${inh}"></label>
    <label>GPU layers <span class="hint">-ngl: n | auto | all</span><input id="p-ngl" value="${esc(p.gpuLayers ?? "")}" placeholder="${inh}"></label>
    <label>Flash attention <span class="hint">-fa</span>${sel("p-fa", p.flashAttn, [["", inh], ["auto", "auto"], ["on", "on"], ["off", "off"]])}</label>
    <label>Auto-fit to VRAM <span class="hint">-fit</span>${sel("p-fit", p.fit, [["", inh], ["on", "on"], ["off", "off"]])}</label>
    <label>Parallel slots <span class="hint">-np</span><input id="p-np" type="number" value="${esc(p.parallel ?? "")}" placeholder="${inh}"></label>
    <label>Threads <span class="hint">-t</span><input id="p-t" type="number" value="${esc(p.threads ?? "")}" placeholder="${inh}"></label>
    <label>Batch <span class="hint">-b</span><input id="p-b" type="number" value="${esc(p.batchSize ?? "")}" placeholder="${inh}"></label>
    <label>Micro-batch <span class="hint">-ub</span><input id="p-ub" type="number" value="${esc(p.uBatchSize ?? "")}" placeholder="${inh}"></label>
    <label>Host<input id="p-host" value="${esc(p.host ?? "")}" placeholder="${forDefaults ? "127.0.0.1" : inh}"></label>
    <label>KV cache K <span class="hint">-ctk</span>${sel("p-ctk", p.cacheTypeK, kv)}</label>
    <label>KV cache V <span class="hint">-ctv</span>${sel("p-ctv", p.cacheTypeV, kv)}</label>
    <label>Jinja templates${sel("p-jinja", tri(p.jinja), [["", inh], ["true", "on"], ["false", "off"]])}</label>
    <label>mlock${sel("p-mlock", tri(p.mlock), [["", inh], ["true", "on"], ["false", "off"]])}</label>
    <label>Disable mmap${sel("p-nommap", tri(p.noMmap), [["", inh], ["true", "yes"], ["false", "no"]])}</label>
    <label>Unload when idle <span class="hint">minutes, 0 = never; reloads on next request</span><input id="p-idle" type="number" min="0" value="${esc(p.idleUnloadMinutes ?? "")}" placeholder="${forDefaults ? "never" : inh}"></label>
  </div>
  <label class="mt">Extra arguments <span class="hint">anything llama-server accepts, e.g. --temp 0.6 --top-k 20 --n-cpu-moe 10</span>
    <textarea id="p-args" rows="2">${esc(joinArgs(p.args))}</textarea></label>
  <label>Environment <span class="hint">KEY=value per line, e.g. CUDA_VISIBLE_DEVICES=0</span>
    <textarea id="p-env" rows="2">${esc(Object.entries(p.env || {}).map(([k, v]) => `${k}=${v}`).join("\n"))}</textarea></label>`;
}
function readParams(target = {}) {
  const num = (id) => { const v = $(id).value.trim(); return v === "" ? null : Number(v); };
  const str = (id) => { const v = $(id).value.trim(); return v === "" ? null : v; };
  const bool = (id) => { const v = $(id).value; return v === "" ? null : v === "true"; };
  Object.assign(target, {
    ctxSize: num("#p-ctx"), gpuLayers: str("#p-ngl"), flashAttn: str("#p-fa"), fit: str("#p-fit"),
    parallel: num("#p-np"), threads: num("#p-t"), batchSize: num("#p-b"), uBatchSize: num("#p-ub"),
    host: str("#p-host"), cacheTypeK: str("#p-ctk"), cacheTypeV: str("#p-ctv"),
    jinja: bool("#p-jinja"), mlock: bool("#p-mlock"), noMmap: bool("#p-nommap"), idleUnloadMinutes: num("#p-idle"),
  });
  const args = splitArgs($("#p-args").value);
  target.args = args.length ? args : null;
  const env = {};
  $("#p-env").value.split("\n").map((l) => l.trim()).filter(Boolean).forEach((l) => {
    const i = l.indexOf("="); if (i > 0) env[l.slice(0, i).trim()] = l.slice(i + 1).trim();
  });
  target.env = Object.keys(env).length ? env : null;
  for (const k of Object.keys(target)) if (target[k] === null || target[k] === undefined) delete target[k];
  return target;
}

// ---------------------------------------------------------------- model editor
async function openEditor(name, preset = {}) {
  const m = name ? state.models.find((x) => x.name === name) : null;
  const p = m ? structuredClone(m.profile) : { ...preset };
  if (!state.files) await refreshFiles(true);
  const suggested = !name && p.file ? p.file.split(/[\\/]/).pop().replace(/(-\d{5}-of-\d{5})?\.gguf$/i, "").toLowerCase().replace(/[^a-z0-9._-]+/g, "-") : "";
  const files = (state.files?.files || []);
  const builds = await api("/llamacpp/builds").catch(() => []);
  openDrawer(name ? `Edit ${name}` : "New model profile", `
    <div class="form">
      <div class="grid2">
        <label>Name <span class="hint">also the model id on the OpenAI API unless alias is set</span>
          <input id="e-name" value="${esc(name || suggested)}" pattern="[A-Za-z0-9._-]+" required></label>
        <label>Description<input id="e-desc" value="${esc(p.description || "")}"></label>
      </div>
      <label>GGUF file <span class="hint">relative to the models directory, or absolute</span>
        <input id="e-file" list="dl-files" value="${esc(p.file || "")}" placeholder="owner\\repo\\model-Q4_K_M.gguf"></label>
      <label>Multimodal projector (mmproj) <span class="hint">optional, for vision models</span>
        <input id="e-mmproj" list="dl-mmproj" value="${esc(p.mmproj || "")}"></label>
      <div class="grid3">
        <label>Draft model <span class="hint">speculative decoding (-md)</span>
          <input id="e-draft" list="dl-files" value="${esc(p.draft || "")}" placeholder="optional"></label>
        <label>Spec type <span class="hint">--spec-type, empty = from draft name</span>
          <input id="e-spec" list="dl-spec" value="${esc(p.specType || "")}" placeholder="${esc(p.draft ? specFromName(p.draft) : "none")}"></label>
        <label>llama.cpp build <span class="hint">empty = active build</span>
          <input id="e-build" list="dl-builds" value="${esc(p.build || "")}" placeholder="owner/repo@tag or build id"></label>
      </div>
      <datalist id="dl-files">${files.filter((f) => !f.isMmproj).map((f) => `<option value="${esc(f.path)}">`).join("")}</datalist>
      <datalist id="dl-mmproj">${files.filter((f) => f.isMmproj).map((f) => `<option value="${esc(f.path)}">`).join("")}</datalist>
      <datalist id="dl-spec">${["draft-dspark", "draft-mtp", "draft-eagle3", "draft-dflash", "draft-dflash2", "draft-simple", "ngram-mod", "ngram-simple"].map((s) => `<option value="${s}">`).join("")}</datalist>
      <datalist id="dl-builds">${builds.map((b) => `<option value="${esc(b.repo)}@${esc(b.tag)}">${esc(b.id)}</option>`).join("")}</datalist>
      <div class="grid3">
        <label>Port <span class="hint">empty = auto</span><input id="e-port" type="number" value="${esc(p.port ?? "")}"></label>
        <label>Alias <span class="hint">-a</span><input id="e-alias" value="${esc(p.alias || "")}" placeholder="${esc(name || "name")}"></label>
        <label class="check" style="margin-top:22px"><input type="checkbox" id="e-auto" ${p.autostart ? "checked" : ""}> Autostart</label>
      </div>
      <fieldset><legend>Start parameters</legend>${paramFields(p)}</fieldset>
      <fieldset><legend>Hugging Face source</legend>
        <div class="grid3">
          <label>Repo<input id="e-hf-repo" value="${esc(p.hf?.repo || "")}" placeholder="unsloth/Qwen3-8B-GGUF"></label>
          <label>File<input id="e-hf-file" value="${esc(p.hf?.file || "")}"></label>
          <label>mmproj<input id="e-hf-mmproj" value="${esc(p.hf?.mmproj || "")}"></label>
          <label>Draft<input id="e-hf-draft" value="${esc(p.hf?.draft || "")}"></label>
          <label>Revision<input id="e-hf-rev" value="${esc(p.hf?.revision || "")}" placeholder="main"></label>
        </div>
        <p class="muted small">Lets Quipu re-download the file (Apply / Download) on another machine.</p>
      </fieldset>
      ${name ? `<div><div class="muted small" style="margin-bottom:6px">Command line</div><div class="cmd" id="e-cmd">…</div></div>` : ""}
    </div>`,
    `<button class="btn ghost" id="e-cancel">Cancel</button><button class="btn primary" id="e-save">${name ? "Save" : "Create"}</button>`);
  $("#e-cancel").onclick = closeDrawer;
  if (name) api(`/models/${encodeURIComponent(name)}/command`).then((c) => { $("#e-cmd").textContent = c.commandLine; }).catch(() => { });
  $("#e-save").onclick = async () => {
    const newName = $("#e-name").value.trim();
    const profile = readParams({});
    const s = (id) => $(id).value.trim() || null;
    Object.assign(profile, {
      description: s("#e-desc"), file: s("#e-file"), mmproj: s("#e-mmproj"),
      draft: s("#e-draft"), specType: s("#e-spec"), build: s("#e-build"),
      port: $("#e-port").value ? Number($("#e-port").value) : null, alias: s("#e-alias"), autostart: $("#e-auto").checked,
    });
    const repo = s("#e-hf-repo"), hfFile = s("#e-hf-file");
    profile.hf = repo && hfFile ? { repo, file: hfFile, mmproj: s("#e-hf-mmproj"), draft: s("#e-hf-draft"), revision: s("#e-hf-rev") } : null;
    if (profile.hf) for (const k of Object.keys(profile.hf)) if (profile.hf[k] === null) delete profile.hf[k];
    for (const k of Object.keys(profile)) if (profile[k] === null) delete profile[k];
    try {
      if (name && newName !== name) await api(`/models/${encodeURIComponent(name)}/rename`, { method: "POST", body: { newName } });
      await api(`/models/${encodeURIComponent(newName)}`, { method: "PUT", body: profile });
      toast(`Saved ${newName}`, "ok");
      closeDrawer();
      refresh(); refreshFiles(true);
    } catch (e) { toast(e.message, "err"); }
  };
}

function openDelete(name) {
  openDrawer(`Remove ${name}?`, `
    <p>This removes the profile from <code>quipu.yaml</code> (and stops the server if it's running).</p>
    <label class="check" style="display:flex;gap:8px;align-items:center"><input type="checkbox" id="d-files"> Also delete the GGUF file(s) from disk — this cannot be undone</label>`,
    `<button class="btn ghost" id="d-cancel">Cancel</button><button class="btn danger" id="d-ok">Remove</button>`);
  $("#d-cancel").onclick = closeDrawer;
  $("#d-ok").onclick = async () => {
    const del = $("#d-files").checked;
    try {
      const r = await api(`/models/${encodeURIComponent(name)}?deleteFiles=${del}`, { method: "DELETE" });
      toast(`Removed ${name}${r.files.length ? ` and ${r.files.length} file(s)` : ""}`, "ok");
      closeDrawer(); refresh(); refreshFiles(true);
    } catch (e) { toast(e.message, "err"); }
  };
}

// ---------------------------------------------------------------- logs
let logTimer = null;
function openLogs(name) {
  let since = 0;
  openDrawer(`${name} — logs`, `<pre class="logs" id="logbox"></pre>`,
    `<label class="check small" style="margin-right:auto;display:flex;gap:6px;align-items:center"><input type="checkbox" id="log-follow" checked> Follow</label>
     <button class="btn ghost" id="log-copy">Copy</button>`);
  const box = $("#logbox");
  $("#log-copy").onclick = () => copyText(box.textContent).then(() => toast("Copied", "ok"));
  const cls = (t) => (t.startsWith("[quipu]") ? "d" : /\bE\s|error|failed/i.test(t) ? "e" : /\bW\s|warn/i.test(t) ? "w" : "");
  const poll = async () => {
    try {
      const r = await api(`/models/${encodeURIComponent(name)}/logs?since=${since}&tail=${since ? 0 : 500}`);
      if (r.lines.length) {
        since = r.lines[r.lines.length - 1].seq;
        const frag = document.createDocumentFragment();
        for (const l of r.lines) { const d = document.createElement("div"); d.className = cls(l.text); d.textContent = l.text; frag.append(d); }
        box.append(frag);
        if ($("#log-follow")?.checked) box.scrollTop = box.scrollHeight;
      } else if (!since && !box.childElementCount) box.textContent = "No output yet — start the model to see llama-server's log.";
    } catch { }
  };
  clearInterval(logTimer);
  poll();
  logTimer = setInterval(poll, 1000);
}

// ================================================================ pi export
async function openPiExport(name, host) {
  const q = host ? `?host=${encodeURIComponent(host)}` : "";
  let r;
  try { r = await api(`/models/${encodeURIComponent(name)}/export/pi${q}`); }
  catch (err) { toast(err.message, "err"); return; }
  const full = JSON.stringify(r.config, null, 2);
  const entry = JSON.stringify(r.model, null, 2);
  openDrawer(`${name} — pi config`, `
    <div class="form">
      <label>pi connects to
        <span class="hint">127.0.0.1 if pi runs on this PC, the LAN address if it runs on another machine</span>
        <select id="pi-host">${r.hosts.map((h) => `<option ${h === r.host ? "selected" : ""}>${esc(h)}</option>`).join("")}</select>
      </label>
    </div>
    <p class="muted small">Save as <code>${esc(r.path)}</code>. If that file already has a <code>${esc(r.provider)}</code> provider, add just the model entry to its <code>models</code> list. Then pick it in pi with <code>/model</code>.</p>
    ${r.notes.map((n) => `<p class="small warn-text">${esc(n)}</p>`).join("")}
    <pre class="cmd" id="pi-json">${esc(full)}</pre>`,
    `<button class="btn ghost" id="pi-copy-entry" style="margin-right:auto">Copy model entry</button>
     <button class="btn ghost" id="pi-copy">Copy</button>
     <button class="btn primary" id="pi-download">⭳ models.json</button>`);
  $("#pi-host").onchange = (e) => openPiExport(name, e.target.value);
  $("#pi-copy").onclick = () => copyText(full).then(() => toast("Copied models.json", "ok"));
  $("#pi-copy-entry").onclick = () => copyText(entry).then(() => toast("Copied model entry", "ok"));
  $("#pi-download").onclick = () => {
    const a = document.createElement("a");
    a.href = URL.createObjectURL(new Blob([full + "\n"], { type: "application/json" }));
    a.download = "models.json";
    a.click();
    setTimeout(() => URL.revokeObjectURL(a.href), 1000);
  };
}

// ================================================================ jobs dock
function renderJobs() {
  const dock = $("#jobs-dock");
  const recent = state.jobs.filter((j) => j.state === "Running" || Date.now() - new Date(j.finishedAt).getTime() < 15000);
  dock.classList.toggle("hidden", !recent.length);
  if (!recent.length) return;
  const dot = { Running: "warn", Completed: "ok", Failed: "err", Canceled: "" };
  dock.innerHTML = `<div class="jobs-head">Activity<div class="spacer"></div><button class="btn small ghost" id="jobs-clear">Clear</button></div>` +
    recent.map((j) => `<div class="job">
      <div class="job-top"><span class="dot ${dot[j.state]}"></span><span class="job-title" title="${esc(j.title)}">${esc(j.title)}</span>
        ${j.state === "Running" ? `<button class="btn small ghost" data-cancel="${j.id}">Cancel</button>` : `<span class="muted">${esc(j.state)}</span>`}</div>
      ${j.state === "Running" ? `<div class="progress"><div style="width:${j.percent ?? 0}%"></div></div>` : ""}
      <div class="muted">${esc(j.error || j.message || "")}${j.state === "Running" && j.bytesTotal ? ` · ${fmtBytes(j.bytesDone)} / ${fmtBytes(j.bytesTotal)} · ${fmtBytes(j.bytesPerSecond)}/s` : ""}</div>
    </div>`).join("");
}
$("#jobs-dock").addEventListener("click", async (e) => {
  const c = e.target.closest("[data-cancel]");
  if (c) await act(api(`/jobs/${c.dataset.cancel}`, { method: "DELETE" }), "Canceled");
  if (e.target.id === "jobs-clear") await act(api("/jobs/clear", { method: "POST" }));
});

// ================================================================ discover (hugging face)
$("#hf-search-form").addEventListener("submit", async (e) => {
  e.preventDefault();
  const q = $("#hf-query").value.trim();
  if (!q) return;
  const list = $("#hf-results");
  if (/^[\w.-]+\/[\w.-]+$/.test(q)) { openRepo(q); }
  list.innerHTML = `<div class="empty">Searching…</div>`;
  try {
    const repos = await api(`/hf/search?q=${encodeURIComponent(q)}&limit=40`);
    list.innerHTML = repos.length ? repos.map((r) => `
      <div class="item" data-repo="${esc(r.id)}">
        <div class="grow"><div class="title">${esc(r.id)}</div>
          <div class="muted small">⭳ ${fmtNum(r.downloads)} · ♥ ${fmtNum(r.likes)} · ${ago(r.lastModified)}</div></div>
        ${r.gated ? `<span class="tag warn">gated</span>` : ""}
      </div>`).join("") : `<div class="empty">No GGUF repos found.</div>`;
  } catch (err) { list.innerHTML = `<div class="empty">${esc(err.message)}</div>`; }
});
$("#hf-results").addEventListener("click", (e) => {
  const it = e.target.closest("[data-repo]");
  if (!it) return;
  $$("#hf-results .item").forEach((x) => x.classList.toggle("active", x === it));
  openRepo(it.dataset.repo);
});

async function openRepo(repo) {
  state.hfRepo = repo;
  const box = $("#hf-files");
  box.innerHTML = `<div class="empty">Loading files of ${esc(repo)}…</div>`;
  try {
    const files = await api(`/hf/files?repo=${encodeURIComponent(repo)}`);
    const vram = (state.status?.gpus?.[0]?.memoryTotalMiB || 0) * 1024 * 1024;
    const mmprojs = files.filter((f) => f.isMmproj);
    const drafts = files.filter((f) => !f.isMmproj && isDraftFile(f.file));
    const models = files.filter((f) => !f.isMmproj && !isDraftFile(f.file));
    const fit = (size) => !vram ? "" : size < vram * 0.8 ? `<span class="tag ok">fits</span>` : size < vram ? `<span class="tag warn">tight</span>` : `<span class="tag err">> VRAM</span>`;
    box.innerHTML = `
      <div class="card-head"><h3>${esc(repo)}</h3><div class="spacer"></div><a class="small" href="https://huggingface.co/${esc(repo)}" target="_blank">huggingface.co ↗</a></div>
      ${mmprojs.length ? `<label class="small muted" style="display:block;margin-bottom:12px">Vision projector
        <select id="hf-mmproj"><option value="">none</option>${mmprojs.map((m) => `<option value="${esc(m.file)}">${esc(m.file)} (${fmtBytes(m.size)})</option>`).join("")}</select></label>` : ""}
      ${drafts.length ? `<label class="small muted" style="display:block;margin-bottom:12px">Speculative decoding draft
        <select id="hf-draft"><option value="">none</option>${drafts.map((m) => `<option value="${esc(m.file)}">${esc(m.file)} (${fmtBytes(m.size)}) · ${specFromName(m.file)}</option>`).join("")}</select></label>` : ""}
      <table class="table">
        <tr><th>Quant</th><th>File</th><th class="num">Size</th><th>VRAM</th><th></th></tr>
        ${models.map((f) => `<tr>
          <td><span class="tag accent">${esc(f.quant || "?")}</span></td>
          <td class="mono small">${esc(f.file)}${f.parts.length > 1 ? ` <span class="tag">${f.parts.length} parts</span>` : ""}</td>
          <td class="num">${fmtBytes(f.size)}</td>
          <td>${fit(f.size)}</td>
          <td class="right"><button class="btn small primary" data-add="${esc(f.file)}">Add</button></td>
        </tr>`).join("")}
      </table>`;
    if (!models.length) box.innerHTML += `<div class="empty">No GGUF files in this repo.</div>`;
    if (window.innerWidth <= 1100) box.scrollIntoView({ behavior: "smooth", block: "start" });
  } catch (err) { box.innerHTML = `<div class="empty">${esc(err.message)}</div>`; }
}
$("#hf-files").addEventListener("click", (e) => {
  const b = e.target.closest("[data-add]");
  if (!b) return;
  const file = b.dataset.add;
  const mmproj = $("#hf-mmproj")?.value || null;
  const draft = $("#hf-draft")?.value || null;
  const suggested = file.split("/").pop().replace(/(-\d{5}-of-\d{5})?\.gguf$/i, "").toLowerCase().replace(/[^a-z0-9._-]+/g, "-");
  openDrawer("Add model", `
    <div class="form">
      <p class="muted">Downloads <code>${esc(file)}</code>${mmproj ? `, <code>${esc(mmproj)}</code>` : ""}${draft ? `, draft <code>${esc(draft)}</code>` : ""} from <b>${esc(state.hfRepo)}</b> into <code>${esc(state.status?.paths?.models || "")}</code>.
        Check the model card for required settings or a specific llama.cpp build (set it later under Edit → llama.cpp build).</p>
      <label>Profile name<input id="a-name" value="${esc(suggested)}"></label>
      <fieldset><legend>Start parameters</legend>${paramFields({})}</fieldset>
      <label class="check"><input type="checkbox" id="a-auto"> Autostart with Quipu</label>
    </div>`,
    `<button class="btn ghost" id="a-cancel">Cancel</button><button class="btn primary" id="a-ok">Add &amp; download</button>`);
  $("#a-cancel").onclick = closeDrawer;
  $("#a-ok").onclick = async () => {
    const profile = readParams({});
    if ($("#a-auto").checked) profile.autostart = true;
    try {
      await api("/hf/add", { method: "POST", body: { repo: state.hfRepo, file, name: $("#a-name").value.trim() || null, mmproj, draft, profile } });
      toast("Download started", "ok");
      closeDrawer(); refresh();
    } catch (err) { toast(err.message, "err"); }
  };
});

// ================================================================ llama.cpp
async function loadLlamaCpp(refreshReleases = false) {
  const cfg = await api("/config");
  $("#set-backend").value = cfg.llamaCpp.backend;
  $("#set-cuda").value = cfg.llamaCpp.cudaVersion === "auto" ? "" : cfg.llamaCpp.cudaVersion;
  $("#set-keep").value = cfg.llamaCpp.keepBuilds;
  $("#set-autoupdate").checked = cfg.llamaCpp.autoUpdate;
  const pinned = cfg.llamaCpp.version !== "latest";

  const builds = await api("/llamacpp/builds");
  const active = builds.find((b) => b.active);
  $("#build-hero").innerHTML = `
    <div class="logo" style="width:54px;height:54px;font-size:26px">⚙</div>
    <div class="stat"><span>Active build</span><span class="big">${active ? esc(active.tag) : "none"}</span></div>
    <div class="stat"><span>Backend</span><b>${esc(state.status?.backend || cfg.llamaCpp.backend)}${active?.cudaVersion ? " " + esc(active.cudaVersion) : ""}</b></div>
    <div class="stat"><span>Mode</span><b>${pinned ? `pinned (${esc(cfg.llamaCpp.version)})` : "follow latest installed"}</b></div>
    <div class="stat"><span>Released</span><b>${active ? ago(active.publishedAt) : "–"}</b></div>
    <div class="spacer"></div>
    ${pinned ? `<button class="btn ghost" id="btn-unpin">Unpin</button>` : ""}
    <button class="btn primary" id="btn-install-latest">⭳ Install latest</button>`;
  $("#btn-install-latest").onclick = () => act(api("/llamacpp/install", { method: "POST", body: {} }), "Installing newest release…");
  if (pinned) $("#btn-unpin").onclick = () => act(api("/llamacpp/unpin", { method: "POST" }), "Following latest").then(() => loadLlamaCpp());

  $("#builds-table").innerHTML = builds.length ? `<tr><th>Build</th><th>Installed</th><th></th></tr>` + builds.map((b) => `
    <tr><td><b>${esc(b.tag)}</b> <span class="muted">${esc(b.backend)}${b.cudaVersion ? " " + esc(b.cudaVersion) : ""}</span> ${b.active ? `<span class="tag ok">active</span>` : ""}
      ${b.repo !== cfg.llamaCpp.repo ? `<div class="muted small">${esc(b.repo)} · use per model: <code>${esc(b.repo)}@${esc(b.tag)}</code></div>` : ""}</td>
    <td class="muted">${ago(b.installedAt)}</td>
    <td class="right">${b.active ? "" : `<button class="btn small" data-activate="${esc(b.id)}">Use</button> <button class="btn small ghost danger" data-delbuild="${esc(b.id)}">✕</button>`}</td></tr>`).join("")
    : `<tr><td class="empty">No builds installed yet.</td></tr>`;

  const rt = $("#releases-table");
  rt.innerHTML = `<tr><td class="empty">Loading releases…</td></tr>`;
  try {
    const rels = await api(`/llamacpp/releases?limit=15${refreshReleases ? "&refresh=true" : ""}`);
    $("#releases-note").textContent = rels[0] ? `· ${rels[0].backend}${rels[0].cudaVersion ? " " + rels[0].cudaVersion : ""} assets` : "";
    rt.innerHTML = `<tr><th>Release</th><th>Published</th><th>Asset</th><th class="num">Download</th><th></th></tr>` + rels.map((r, i) => `
      <tr><td><a href="${esc(r.url)}" target="_blank"><b>${esc(r.tag)}</b></a> ${i === 0 ? `<span class="tag accent">newest</span>` : ""}</td>
      <td class="muted">${ago(r.publishedAt)}</td>
      <td class="mono small">${esc(r.asset)}</td>
      <td class="num">${fmtBytes(r.downloadSize)}</td>
      <td class="right">${r.installed ? `<span class="tag ok">installed</span>` : `<button class="btn small" data-install="${esc(r.tag)}">Install</button>`}</td></tr>`).join("");
  } catch (e) { rt.innerHTML = `<tr><td class="empty">${esc(e.message)}</td></tr>`; }
}
$("#view-llamacpp").addEventListener("click", async (e) => {
  const t = e.target.closest("[data-install],[data-activate],[data-delbuild]");
  if (!t) return;
  if (t.dataset.install) await act(api("/llamacpp/install", { method: "POST", body: { tag: t.dataset.install } }), `Installing ${t.dataset.install}…`);
  if (t.dataset.activate) await act(api(`/llamacpp/builds/${encodeURIComponent(t.dataset.activate)}/activate`, { method: "POST" }), `Pinned ${t.dataset.activate}`);
  if (t.dataset.delbuild && confirm(`Delete build ${t.dataset.delbuild}?`)) await act(api(`/llamacpp/builds/${encodeURIComponent(t.dataset.delbuild)}`, { method: "DELETE" }), "Deleted");
  loadLlamaCpp();
});
$("#btn-refresh-releases").addEventListener("click", () => loadLlamaCpp(true));
$("#fork-form").addEventListener("submit", async (e) => {
  e.preventDefault();
  const repo = $("#fork-repo").value.trim(), tag = $("#fork-tag").value.trim() || null;
  await act(api("/llamacpp/install", { method: "POST", body: { repo, tag } }), `Installing ${repo} ${tag || "latest"}…`).catch(() => { });
});
$("#btn-save-build-settings").addEventListener("click", () => act(api("/config", {
  method: "PATCH",
  body: { llamaCpp: { backend: $("#set-backend").value, cudaVersion: $("#set-cuda").value.trim() || "auto", keepBuilds: Number($("#set-keep").value || 0), autoUpdate: $("#set-autoupdate").checked } },
}), "Saved").then(() => loadLlamaCpp()));

// ================================================================ tokens
async function loadTokens() {
  const t = await api("/hf/tokens");
  $("#tokens-list").innerHTML = t.tokens.length ? `<table class="table">${t.tokens.map((k) => `
    <tr><td><b>${esc(k.name)}</b> ${t.active?.toLowerCase() === k.name.toLowerCase() ? `<span class="tag ok">active</span>` : ""}<div class="mono muted small">${esc(k.masked)}</div></td>
    <td>${k.user ? `${esc(k.user)} <span class="tag">${esc(k.role || "")}</span>` : `<span class="muted">not validated</span>`}<div class="muted small">${k.validatedAt ? "checked " + ago(k.validatedAt) : ""}</div></td>
    <td class="right">
      <button class="btn small ghost" data-tval="${esc(k.name)}">Check</button>
      ${t.active?.toLowerCase() === k.name.toLowerCase() ? `<button class="btn small ghost" data-tdeact="1">Stop using</button>` : `<button class="btn small" data-tact="${esc(k.name)}">Use</button>`}
      <button class="btn small ghost danger" data-tdel="${esc(k.name)}">✕</button>
    </td></tr>`).join("")}</table>` : `<div class="empty">No tokens stored.</div>`;
  $("#tokens-foot").innerHTML = `In use: <b>${esc(t.source)}</b>${t.envTokenPresent ? " · HF_TOKEN environment variable is set (used as fallback)" : ""}<br>Stored encrypted in <code>${esc(t.storage)}</code>. The active token is passed to llama-server as HF_TOKEN.`;
}
$("#token-form").addEventListener("submit", async (e) => {
  e.preventDefault();
  const name = $("#token-name").value.trim();
  try {
    const r = await api(`/hf/tokens/${encodeURIComponent(name)}`, { method: "PUT", body: { token: $("#token-value").value, activate: $("#token-activate").checked } });
    $("#token-value").value = "";
    toast(`Token saved${r.user ? ` — logged in as ${r.user}` : ""}`, "ok");
    loadTokens(); refresh();
  } catch (err) { toast(err.message, "err"); }
});
$("#tokens-list").addEventListener("click", async (e) => {
  const b = e.target.closest("button");
  if (!b) return;
  const d = b.dataset;
  try {
    if (d.tval) { const w = await api(`/hf/tokens/${encodeURIComponent(d.tval)}/validate`, { method: "POST" }); toast(w.valid ? `Valid — ${w.user} (${w.role})` : `Invalid: ${w.error}`, w.valid ? "ok" : "err"); }
    if (d.tact) await api(`/hf/tokens/${encodeURIComponent(d.tact)}/activate`, { method: "POST" });
    if (d.tdeact) await api(`/hf/tokens/deactivate`, { method: "POST" });
    if (d.tdel && confirm(`Delete token ${d.tdel}?`)) await api(`/hf/tokens/${encodeURIComponent(d.tdel)}`, { method: "DELETE" });
  } catch (err) { toast(err.message, "err"); }
  loadTokens(); refresh();
});

// ================================================================ config
async function loadConfig() {
  const [cfg, yaml] = await Promise.all([api("/config"), api("/config/yaml", { text: true })]);
  $("#set-models").value = cfg.paths.models;
  $("#set-installs").value = cfg.paths.installs;
  $("#set-logs").value = cfg.paths.logs || "";
  $("#set-listen").value = cfg.server.listen;
  $("#set-networks").value = (cfg.server.allowedNetworks || []).join(", ");
  $("#set-portstart").value = cfg.server.modelPortStart;
  $("#set-maxactive").value = cfg.server.maxActiveModels ?? 0;
  $("#set-apikey").value = cfg.server.apiKey || "";
  $("#set-openbrowser").checked = cfg.server.openBrowserOnStart;
  $("#defaults-form").innerHTML = `<div class="form">${paramFields(cfg.defaults || {}, { forDefaults: true })}</div>`;
  $("#yaml-editor").value = yaml;
  $("#config-path").textContent = state.status?.configFile || "";
  $("#yaml-result").textContent = "";
}
$("#btn-save-settings").addEventListener("click", async () => {
  const apiKey = $("#set-apikey").value.trim() || null;
  await act(api("/config", {
    method: "PATCH",
    body: {
      paths: { models: $("#set-models").value.trim(), installs: $("#set-installs").value.trim(), logs: $("#set-logs").value.trim() },
      server: { listen: $("#set-listen").value.trim(), allowedNetworks: $("#set-networks").value.split(/[\s,;]+/).filter(Boolean), modelPortStart: Number($("#set-portstart").value), maxActiveModels: Number($("#set-maxactive").value || 0), apiKey, openBrowserOnStart: $("#set-openbrowser").checked },
    },
  }), "Settings saved");
  storage.set("quipu.apiKey", apiKey);
  loadConfig();
});
$("#btn-save-defaults").addEventListener("click", async () => {
  const defaults = readParams({});
  // full replace of defaults: send nulls for cleared fields
  const all = ["host", "ctxSize", "gpuLayers", "flashAttn", "fit", "threads", "parallel", "batchSize", "uBatchSize", "cacheTypeK", "cacheTypeV", "jinja", "mlock", "noMmap", "idleUnloadMinutes", "args", "env"];
  const patch = Object.fromEntries(all.map((k) => [k, defaults[k] ?? null]));
  await act(api("/config", { method: "PATCH", body: { defaults: patch } }), "Defaults saved");
  loadConfig();
});
$("#btn-yaml-reload").addEventListener("click", loadConfig);
$("#btn-yaml-validate").addEventListener("click", async () => {
  const r = await api("/config/validate", { method: "POST", body: $("#yaml-editor").value, raw: true });
  $("#yaml-result").innerHTML = r.valid ? `<span style="color:var(--ok)">✓ Valid — ${r.models} model(s)</span>` : `<span style="color:var(--err)">✕ ${r.errors.map(esc).join("<br>")}</span>`;
});
$("#btn-yaml-save").addEventListener("click", async () => {
  try {
    await api("/config/yaml", { method: "PUT", body: $("#yaml-editor").value, raw: true });
    toast("quipu.yaml saved", "ok");
    $("#yaml-result").innerHTML = `<span style="color:var(--ok)">✓ Saved</span>`;
    refresh();
  } catch (e) {
    $("#yaml-result").innerHTML = `<span style="color:var(--err)">✕ ${esc(e.message)}</span>`;
  }
});
$("#yaml-editor").addEventListener("keydown", (e) => {
  if (e.key === "Tab") { e.preventDefault(); document.execCommand("insertText", false, "  "); }
  if (e.key === "s" && (e.ctrlKey || e.metaKey)) { e.preventDefault(); $("#btn-yaml-save").click(); }
});
$("#btn-apply").addEventListener("click", async () => {
  const r = await act(api("/apply", {
    method: "POST",
    body: { updateLlamaCpp: $("#apply-update").checked, downloadModels: $("#apply-download").checked, startAutostart: $("#apply-start").checked, restartChanged: $("#apply-restart").checked },
  }));
  $("#apply-result").innerHTML = r.actions.map((a) => `<li>${esc(a)}</li>`).join("");
});

// ================================================================ boot
show(location.hash.slice(1) in titles ? location.hash.slice(1) : "models");
refresh();
setInterval(() => { if (!document.hidden) refresh(); }, 2000);

// navigator.clipboard only exists on localhost/HTTPS; the dashboard may be opened over plain HTTP on the LAN
function copyText(text) {
  if (navigator.clipboard && window.isSecureContext) return navigator.clipboard.writeText(text);
  const ta = document.createElement("textarea");
  ta.value = text;
  ta.style.cssText = "position:fixed;opacity:0;left:-9999px";
  document.body.append(ta);
  ta.select();
  const ok = document.execCommand("copy");
  ta.remove();
  return ok ? Promise.resolve() : Promise.reject(new Error("Copy failed - select the text and copy it by hand"));
}

// model URLs are reported as 127.0.0.1; from another machine, use the address the dashboard was opened with
function viewUrl(url) {
  const h = location.hostname;
  return h && !["localhost", "127.0.0.1", "[::1]"].includes(h) ? url.replace("://127.0.0.1:", `://${h}:`) : url;
}
