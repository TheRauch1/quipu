using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json.Serialization;

namespace Quipu.Services;

public enum JobState { Running, Completed, Failed, Canceled }

/// <summary>A long-running background operation (download, install) with progress.</summary>
public sealed class Job
{
    public string Id { get; } = Guid.NewGuid().ToString("n")[..10];
    public required string Kind { get; init; }
    public required string Title { get; init; }
    /// <summary>Optional key used to stop duplicate jobs (e.g. "download:qwen3").</summary>
    public string? Key { get; init; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public JobState State { get; internal set; } = JobState.Running;
    public string? Message { get; set; }
    public long BytesDone { get; set; }
    public long? BytesTotal { get; set; }
    public double BytesPerSecond { get; set; }
    public double? Percent => BytesTotal is > 0 ? Math.Round(100.0 * BytesDone / BytesTotal.Value, 1) : null;
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;
    public DateTimeOffset? FinishedAt { get; internal set; }
    public string? Error { get; internal set; }
    [JsonIgnore] internal CancellationTokenSource Cts { get; } = new();
    [JsonIgnore] internal Task? Task { get; set; }
}

public sealed class JobService(ILogger<JobService> log)
{
    private readonly ConcurrentDictionary<string, Job> _jobs = new();

    public IReadOnlyList<Job> List() => _jobs.Values.OrderByDescending(j => j.StartedAt).ToList();
    public Job? Get(string id) => _jobs.GetValueOrDefault(id);

    public Job? FindActive(string key) =>
        _jobs.Values.FirstOrDefault(j => j.Key == key && j.State == JobState.Running);

    /// <summary>Starts work in the background. If a running job has the same key, that job is returned instead.</summary>
    public Job Start(string kind, string title, Func<Job, CancellationToken, Task> work, string? key = null)
    {
        if (key is not null && FindActive(key) is { } existing) return existing;

        var job = new Job { Kind = kind, Title = title, Key = key };
        _jobs[job.Id] = job;
        job.Task = Task.Run(async () =>
        {
            try
            {
                await work(job, job.Cts.Token);
                job.State = JobState.Completed;
                job.Message ??= "Done";
            }
            catch (OperationCanceledException) when (job.Cts.IsCancellationRequested)
            {
                job.State = JobState.Canceled;
                job.Message = "Canceled";
            }
            catch (Exception ex)
            {
                job.State = JobState.Failed;
                job.Error = ex.Message;
                log.LogError(ex, "Job {Title} failed", job.Title);
            }
            finally
            {
                job.FinishedAt = DateTimeOffset.Now;
                job.BytesPerSecond = 0;
                Prune();
            }
        });
        return job;
    }

    public bool Cancel(string id)
    {
        if (!_jobs.TryGetValue(id, out var job) || job.State != JobState.Running) return false;
        job.Cts.Cancel();
        return true;
    }

    /// <summary>Removes finished jobs from the list.</summary>
    public int ClearFinished()
    {
        var n = 0;
        foreach (var j in _jobs.Values.Where(j => j.State != JobState.Running).ToList())
            if (_jobs.TryRemove(j.Id, out _)) n++;
        return n;
    }

    private void Prune()
    {
        foreach (var old in _jobs.Values.Where(j => j.State != JobState.Running)
                     .OrderByDescending(j => j.StartedAt).Skip(50).ToList())
            _jobs.TryRemove(old.Id, out _);
    }
}

/// <summary>Resumable HTTP file download with progress reporting into a Job.</summary>
public static class Downloader
{
    public static async Task DownloadAsync(HttpClient http, string url, string destination, Job job,
        string? bearerToken, CancellationToken ct, long? expectedSize = null, bool totalKnown = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(destination) && (expectedSize is null || new FileInfo(destination).Length == expectedSize))
        {
            if (totalKnown) job.BytesDone += new FileInfo(destination).Length;
            job.Message = $"Already present: {Path.GetFileName(destination)}";
            return;
        }

        var part = destination + ".part";
        long existing = File.Exists(part) ? new FileInfo(part).Length : 0;

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(bearerToken)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        if (existing > 0) req.Headers.Range = new RangeHeaderValue(existing, null);

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable && existing > 0)
        {
            if (expectedSize is null || existing == expectedSize)
            {
                File.Move(part, destination, overwrite: true); // .part is already complete
                return;
            }
            // .part is longer than the file now on the server (it was replaced upstream): start over
            File.Delete(part);
            await DownloadAsync(http, url, destination, job, bearerToken, ct, expectedSize, totalKnown);
            return;
        }
        if (resp.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            throw new InvalidOperationException($"Access denied ({(int)resp.StatusCode}) for {url}. Gated or private repo? Set a Hugging Face token and accept the model's license on huggingface.co.");
        resp.EnsureSuccessStatusCode();

        var resumed = resp.StatusCode == System.Net.HttpStatusCode.PartialContent;
        if (!resumed) existing = 0;
        var total = resp.Content.Headers.ContentLength is { } len ? len + existing : expectedSize;

        if (!totalKnown) job.BytesTotal = (job.BytesTotal ?? 0) + (total ?? 0);
        job.BytesDone += existing;

        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = new FileStream(part, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
        {
            var buffer = new byte[1 << 20];
            var sw = Stopwatch.StartNew();
            long windowBytes = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                job.BytesDone += read;
                windowBytes += read;
                if (sw.ElapsedMilliseconds >= 1000)
                {
                    job.BytesPerSecond = windowBytes / sw.Elapsed.TotalSeconds;
                    windowBytes = 0;
                    sw.Restart();
                }
            }
        }

        if (expectedSize is { } exp && new FileInfo(part).Length != exp)
            throw new IOException($"Size mismatch for {Path.GetFileName(destination)}: expected {exp} bytes, got {new FileInfo(part).Length}");
        File.Move(part, destination, overwrite: true);
    }
}
