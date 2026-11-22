using Quipu.Config;

namespace Quipu.Services;

/// <summary>
/// "Apply" for config as code: makes the machine match quipu.yaml —
/// installs the wanted llama.cpp build, downloads missing models, starts autostart models,
/// and (optionally) restarts running models whose parameters changed.
/// </summary>
public sealed class ReconcileService(
    ConfigStore config, BuildManager builds, ModelService models, InstanceManager instances, JobService jobs,
    ILogger<ReconcileService> log) : IHostedService
{
    public async Task<ApplyReport> ApplyAsync(ApplyOptions opts, CancellationToken ct)
    {
        var cfg = config.Current;
        var actions = new List<string>();
        var jobIds = new List<string>();
        Job? installJob = null;

        // 1. llama.cpp build
        var wanted = cfg.LlamaCpp.Version;
        var installed = builds.ListInstalled();
        var pinned = !wanted.Equals("latest", StringComparison.OrdinalIgnoreCase);
        if (pinned && builds.Find(wanted) is null)
        {
            var (repo, tag) = BuildSpec.ToInstall(wanted);
            installJob = builds.Install(tag, null, pin: false, instances.BuildDirsInUse, repo);
            actions.Add($"install pinned llama.cpp {wanted}");
        }
        else if (!pinned && (installed.Count == 0 || opts.UpdateLlamaCpp))
        {
            var latest = await builds.LatestReleaseAsync(null, refresh: true, ct);
            if (latest is not null && !latest.Installed)
            {
                installJob = builds.Install(latest.Tag, null, pin: false, instances.BuildDirsInUse);
                actions.Add($"install llama.cpp {latest.Tag} ({latest.Backend})");
            }
        }
        if (installJob is not null) jobIds.Add(installJob.Id);

        // 1b. builds pinned by individual models ("owner/repo@tag"), e.g. a fork needed for one model
        var modelBuildJobs = new Dictionary<string, Job>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, profile) in cfg.Models)
        {
            if (string.IsNullOrWhiteSpace(profile.Build) || builds.Find(profile.Build) is not null) continue;
            if (!BuildSpec.TryParse(profile.Build, out var repo, out var tag))
            {
                actions.Add($"model {name}: build '{profile.Build}' is not installed (use owner/repo@tag so Apply can install it)");
                continue;
            }
            var job = builds.Install(tag, null, pin: false, instances.BuildDirsInUse, repo); // same spec -> same job
            modelBuildJobs[name] = job;
            if (!jobIds.Contains(job.Id))
            {
                jobIds.Add(job.Id);
                actions.Add($"install {repo}@{tag} (for {name})");
            }
        }

        // 2. models
        var downloadJobs = new Dictionary<string, Job>(StringComparer.OrdinalIgnoreCase);
        foreach (var view in models.List())
        {
            if (!view.Downloaded && view.Profile.Hf is not null && opts.DownloadModels)
            {
                var job = models.StartDownload(view.Name);
                downloadJobs[view.Name] = job;
                jobIds.Add(job.Id);
                actions.Add($"download {view.Name}");
            }
        }

        // 3. start / restart (autostart respects server.maxActiveModels so models don't evict each other)
        var maxActive = config.Current.Server.MaxActiveModels;
        var autostarted = 0;
        foreach (var view in models.List())
        {
            if (view.NeedsRestart && opts.RestartChanged)
            {
                actions.Add($"restart {view.Name} (config changed)");
                _ = RunAfter([installJob, modelBuildJobs.GetValueOrDefault(view.Name)], () => instances.RestartAsync(view.Name), view.Name);
            }
            else if (view.Profile.Autostart && opts.StartAutostart && !instances.IsRunning(view.Name))
            {
                if (!view.Downloaded && !downloadJobs.ContainsKey(view.Name))
                {
                    actions.Add($"skip autostart {view.Name}: model file missing and no hf source");
                    continue;
                }
                if (maxActive > 0 && autostarted >= maxActive)
                {
                    actions.Add($"skip autostart {view.Name}: server.maxActiveModels = {maxActive}");
                    continue;
                }
                autostarted++;
                actions.Add($"start {view.Name}");
                _ = RunAfter([installJob, modelBuildJobs.GetValueOrDefault(view.Name), downloadJobs.GetValueOrDefault(view.Name)],
                    () => instances.StartAsync(view.Name), view.Name);
            }
        }

        if (actions.Count == 0) actions.Add("nothing to do — everything matches quipu.yaml");
        return new ApplyReport(actions, jobIds);
    }

    private async Task RunAfter(Job?[] waitFor, Func<Task> action, string name)
    {
        try
        {
            foreach (var j in waitFor)
            {
                if (j?.Task is null) continue;
                await j.Task;
                if (j.State != JobState.Completed) { log.LogWarning("Not starting {Name}: {Job} {State}", name, j.Title, j.State); return; }
            }
            await action();
        }
        catch (Exception ex)
        {
            log.LogWarning("Could not start {Name}: {Error}", name, ex.Message);
        }
    }

    // ---------------------------------------------------------------- startup / shutdown

    public Task StartAsync(CancellationToken cancellationToken)
    {
        config.StartWatching();
        _ = Task.Run(async () =>
        {
            try
            {
                await ApplyAsync(new ApplyOptions
                {
                    UpdateLlamaCpp = config.Current.LlamaCpp.AutoUpdate,
                    DownloadModels = false,
                    StartAutostart = true,
                    RestartChanged = false,
                }, CancellationToken.None);
            }
            catch (Exception ex)
            {
                log.LogWarning("Startup reconcile failed: {Error}", ex.Message);
            }
        }, cancellationToken);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var j in jobs.List().Where(j => j.State == JobState.Running)) jobs.Cancel(j.Id);
        await instances.StopAllAsync();
    }
}

public sealed class ApplyOptions
{
    /// <summary>Install the newest llama.cpp release (when version is "latest").</summary>
    public bool UpdateLlamaCpp { get; set; }
    /// <summary>Download models whose files are missing and that have an hf source.</summary>
    public bool DownloadModels { get; set; } = true;
    /// <summary>Start models marked autostart.</summary>
    public bool StartAutostart { get; set; } = true;
    /// <summary>Restart running models whose parameters / build changed.</summary>
    public bool RestartChanged { get; set; }
}

public sealed record ApplyReport(IReadOnlyList<string> Actions, IReadOnlyList<string> JobIds);
