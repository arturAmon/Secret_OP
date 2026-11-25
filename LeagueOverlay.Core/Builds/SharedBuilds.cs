using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using LeagueOverlay.Core.Diagnostics;
using LeagueOverlay.Core.Updates;

namespace LeagueOverlay.Core.Builds;

/// <summary>The shared builds file on GitHub: where to download it, its sha256 and when it was last replaced.</summary>
public sealed record SharedBuildsAsset(string Url, long Size, string? Sha256, DateTime UpdatedAt);

/// <summary>
/// Popular builds shared between apps through the public GitHub repo: a rolling "builds" release (marked pre-release, so
/// it's never the "latest" release the update check looks at) with one file, <see cref="FileName"/>. Every app downloads
/// it; uploading needs the GitHub CLI (gh) logged in with push access to the repo, so only the repo owner's app uploads
/// and no token is ever stored by the app.
/// </summary>
public static class SharedBuilds
{
    public const string Tag = "builds";
    public const string FileName = "PopularBuilds" + BuildShare.Extension;
    public const string ReleaseUrl = $"https://api.github.com/repos/{UpdateChecker.Repo}/releases/tags/{Tag}";

    /// <summary>The shared file on GitHub, or null when there's none yet.</summary>
    public static async Task<SharedBuildsAsset?> FindAsync(HttpClient http, CancellationToken ct = default)
    {
        using var response = await http.GetAsync(ReleaseUrl, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return Parse(await response.Content.ReadAsStringAsync(ct));
    }

    public static SharedBuildsAsset? Parse(string releaseJson)
    {
        using var doc = JsonDocument.Parse(releaseJson);
        if (!doc.RootElement.TryGetProperty("assets", out var assets)) return null;
        foreach (var a in assets.EnumerateArray())
        {
            if (a.GetProperty("name").GetString() != FileName) continue;
            string url = a.GetProperty("browser_download_url").GetString() ?? "";
            if (!url.StartsWith($"https://github.com/{UpdateChecker.Repo}/releases/download/", StringComparison.OrdinalIgnoreCase)) return null;
            string? sha = a.TryGetProperty("digest", out var d) && d.GetString() is { } digest && digest.StartsWith("sha256:") ? digest[7..].ToLowerInvariant() : null;
            DateTime updated = a.TryGetProperty("updated_at", out var u) && u.TryGetDateTime(out var t) ? t : DateTime.MinValue;
            return new SharedBuildsAsset(url, a.TryGetProperty("size", out var s) ? s.GetInt64() : 0, sha, updated);
        }
        return null;
    }

    /// <summary>Downloads the shared file (at most <see cref="BuildShare.MaxFileBytes"/>) and checks its sha256.</summary>
    public static async Task<string> DownloadAsync(HttpClient http, SharedBuildsAsset asset, string folder, CancellationToken ct = default)
    {
        if (asset.Size > BuildShare.MaxFileBytes) throw new InvalidDataException("The shared builds file is too big.");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, FileName);
        using (var response = await http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            await using var output = File.Create(path);
            var buffer = new byte[81920];
            long total = 0;
            int n;
            while ((n = await input.ReadAsync(buffer, ct)) > 0)
            {
                total += n;
                if (total > BuildShare.MaxFileBytes) throw new InvalidDataException("The shared builds file is too big.");
                await output.WriteAsync(buffer.AsMemory(0, n), ct);
            }
        }
        if (asset.Sha256 is { } expected && Sha256Of(path) != expected)
        {
            File.Delete(path);
            throw new InvalidDataException("The shared builds file doesn't match its checksum.");
        }
        return path;
    }

    public static string Sha256Of(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(file));
    }

    // ---- uploading, through the GitHub CLI ----

    /// <summary>The gh executable, or null when the GitHub CLI isn't installed.</summary>
    public static string? FindGh()
    {
        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GitHub CLI", "gh.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "GitHub CLI", "gh.exe"),
        ];
        foreach (var c in candidates) if (File.Exists(c)) return c;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { var p = Path.Combine(dir.Trim(), "gh.exe"); if (File.Exists(p)) return p; }
            catch (ArgumentException) { }
        }
        return null;
    }

    /// <summary>True when gh is logged in with push access to the repo (only then does the app upload).</summary>
    public static async Task<bool> CanUploadAsync(CancellationToken ct = default)
    {
        if (FindGh() is not { } gh) return false;
        var (code, output) = await RunAsync(gh, ["api", $"repos/{UpdateChecker.Repo}", "-q", ".permissions.push"], ct);
        return code == 0 && output.Trim() == "true";
    }

    /// <summary>Replaces the shared file on GitHub with <paramref name="file"/> (named <see cref="FileName"/>), creating the release the first time.</summary>
    public static async Task UploadAsync(string file, CancellationToken ct = default)
    {
        if (Path.GetFileName(file) != FileName) throw new ArgumentException($"The file must be named {FileName}.", nameof(file));
        var gh = FindGh() ?? throw new InvalidOperationException("The GitHub CLI (gh) isn't installed.");
        var (code, _) = await RunAsync(gh, ["release", "view", Tag, "--repo", UpdateChecker.Repo], ct);
        if (code != 0)
        {
            var (created, output) = await RunAsync(gh, ["release", "create", Tag, "--repo", UpdateChecker.Repo, "--prerelease", "--latest=false",
                "--title", "Shared builds", "--notes", "Popular builds collected by Secret OP (public match data). The app downloads this file by itself; it's replaced as more games are collected."], ct);
            if (created != 0) throw new InvalidOperationException($"Couldn't create the shared builds release: {output.Trim()}");
        }
        string result = "";
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (attempt > 0) await Task.Delay(TimeSpan.FromSeconds(10), ct); // GitHub sometimes answers 500 for a moment
            var (uploaded, output) = await RunAsync(gh, ["release", "upload", Tag, file, "--repo", UpdateChecker.Repo, "--clobber"], ct);
            if (uploaded == 0) return;
            result = output;
            // gh can report a failure after the upload went through (it retries a slow request and the retry finds the file
            // already there): what's on GitHub decides.
            var (_, digest) = await RunAsync(gh, ["api", $"repos/{UpdateChecker.Repo}/releases/tags/{Tag}", "-q", $".assets[] | select(.name == \"{FileName}\") | .digest"], ct);
            if (digest.Trim() == "sha256:" + Sha256Of(file)) return;
        }
        throw new InvalidOperationException($"Couldn't upload the shared builds: {result.Trim()}");
    }

    private static async Task<(int Code, string Output)> RunAsync(string exe, IEnumerable<string> args, CancellationToken ct)
    {
        var start = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) start.ArgumentList.Add(a);
        using var p = Process.Start(start) ?? throw new InvalidOperationException("gh didn't start.");
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        try { await p.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch (InvalidOperationException) { } throw; }
        string output = await stdout + await stderr;
        if (p.ExitCode != 0) Log.Info($"gh {string.Join(' ', start.ArgumentList.Take(2))} exited {p.ExitCode}: {output.Trim()}");
        return (p.ExitCode, output);
    }
}
