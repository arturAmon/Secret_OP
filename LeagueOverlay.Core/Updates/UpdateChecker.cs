using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace LeagueOverlay.Core.Updates;

/// <summary>A newer release on GitHub: its version, release page, and the installer to download (with its sha256).</summary>
public sealed record UpdateInfo(Version Version, string PageUrl, string? InstallerUrl, long InstallerSize, string? Sha256);

/// <summary>
/// Checks GitHub for a newer release of the app (the latest non-prerelease release of the public repo) and downloads its
/// installer, checking size and sha256 against what GitHub reports. Anonymous requests with a generic User-Agent: no
/// account or personal details are sent.
/// </summary>
public static class UpdateChecker
{
    public const string Repo = "arturAmon/Secret_OP";
    public const string LatestUrl = $"https://api.github.com/repos/{Repo}/releases/latest";

    /// <summary>A client for GitHub's API (User-Agent required; JSON accept header).</summary>
    public static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SecretOP", "1"));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }

    /// <summary>The newer release, or null when this version is the latest (or newer).</summary>
    public static async Task<UpdateInfo?> CheckAsync(HttpClient http, Version current, CancellationToken ct = default) =>
        Parse(await http.GetStringAsync(LatestUrl, ct), current);

    /// <summary>Reads a GitHub release ("tag_name": "v1.15.0", assets with "digest": "sha256:..."). Null if not newer.</summary>
    public static UpdateInfo? Parse(string releaseJson, Version current)
    {
        using var doc = JsonDocument.Parse(releaseJson);
        var root = doc.RootElement;
        if (root.TryGetProperty("draft", out var d) && d.GetBoolean()) return null;
        if (root.TryGetProperty("prerelease", out var p) && p.GetBoolean()) return null;
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version) || Normalize(version) <= Normalize(current)) return null;

        string? url = null, sha = null;
        long size = 0;
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var a in assets.EnumerateArray())
            {
                string name = a.GetProperty("name").GetString() ?? "";
                if (!name.StartsWith("SecretOP-Setup-", StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                url = a.GetProperty("browser_download_url").GetString();
                size = a.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                sha = a.TryGetProperty("digest", out var dg) && dg.GetString() is { } digest && digest.StartsWith("sha256:") ? digest[7..].ToLowerInvariant() : null;
                break;
            }
        }
        // Only ever download from this repo's releases.
        if (url is not null && !url.StartsWith($"https://github.com/{Repo}/releases/download/", StringComparison.OrdinalIgnoreCase)) url = null;
        return new UpdateInfo(version, root.GetProperty("html_url").GetString() ?? $"https://github.com/{Repo}/releases", url, size, sha);
    }

    /// <summary>1.15 and 1.15.0 are the same version.</summary>
    public static Version Normalize(Version v) => new(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));

    /// <summary>
    /// Downloads the installer into <paramref name="folder"/> and checks it: the size GitHub reported and, when GitHub
    /// gave one, the sha256. Returns the file's path. A file that doesn't match is deleted and an exception thrown.
    /// </summary>
    public static async Task<string> DownloadAsync(HttpClient http, UpdateInfo info, string folder, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (info.InstallerUrl is null) throw new InvalidOperationException("This release has no installer to download.");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, $"SecretOP-Setup-v{info.Version}.exe");
        using (var response = await http.GetAsync(info.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? info.InstallerSize;
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            await using var output = File.Create(path);
            var buffer = new byte[81920];
            long done = 0;
            int n;
            while ((n = await input.ReadAsync(buffer, ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, n), ct);
                done += n;
                if (total > 0) progress?.Report(Math.Min(1, done / (double)total));
            }
        }
        string? problem = null;
        if (info.InstallerSize > 0 && new FileInfo(path).Length != info.InstallerSize) problem = "the download is incomplete";
        else if (info.Sha256 is { } expected)
        {
            await using var file = File.OpenRead(path);
            string actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct));
            if (actual != expected) problem = "the download doesn't match the release (checksum)";
        }
        if (problem is not null)
        {
            try { File.Delete(path); } catch (IOException) { }
            throw new InvalidDataException($"Update not installed: {problem}.");
        }
        return path;
    }
}
