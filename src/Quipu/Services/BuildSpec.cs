namespace Quipu.Services;

/// <summary>How quipu.yaml names a llama.cpp build: "owner/repo@tag", an installed build id, or a tag.</summary>
public static class BuildSpec
{
    public static bool TryParse(string spec, out string repo, out string tag)
    {
        var at = spec.LastIndexOf('@');
        if (at > 0 && spec[..at].Contains('/'))
        {
            repo = spec[..at];
            tag = spec[(at + 1)..];
            return tag.Length > 0;
        }
        repo = tag = "";
        return false;
    }

    /// <summary>The installed build a spec names; when several match, the one for this backend.</summary>
    public static InstalledBuild? Match(IEnumerable<InstalledBuild> builds, string spec, string backend)
    {
        var list = builds.ToList();
        if (TryParse(spec, out var repo, out var tag))
            return list.Where(b => b.Repo.Equals(repo, StringComparison.OrdinalIgnoreCase) && b.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(b => b.Backend == backend).FirstOrDefault();
        return list.FirstOrDefault(b => b.Id.Equals(spec, StringComparison.OrdinalIgnoreCase))
               ?? list.Where(b => b.Tag.Equals(spec, StringComparison.OrdinalIgnoreCase)).OrderByDescending(b => b.Backend == backend).FirstOrDefault();
    }

    /// <summary>What to install for a spec that isn't installed: its repo (null = llamaCpp.repo) and release tag.</summary>
    public static (string? Repo, string Tag) ToInstall(string spec) =>
        TryParse(spec, out var repo, out var tag) ? (repo, tag) : (null, spec.Split('-')[0]);
}

public sealed record InstalledBuild(string Id, string Tag, string Backend, string? CudaVersion, string Directory,
    DateTimeOffset PublishedAt, DateTimeOffset InstalledAt, bool Active, string Repo)
{
    /// <summary>Spec usable in a model's "build" field or llamaCpp.version.</summary>
    public string Spec => $"{Repo}@{Tag}";
    public string ServerExe => Path.Combine(Directory, "llama-server.exe");
}
