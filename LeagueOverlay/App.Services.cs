using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using LeagueOverlay.Core.Builds;
using LeagueOverlay.Core.Data;
using LeagueOverlay.Core.Diagnostics;
using LeagueOverlay.Core.Updates;

namespace LeagueOverlay;

// Error log, patch changes, builds shared on GitHub and updates.
public partial class App
{
    private PatchChanges? _patchChanges;
    private UpdateInfo? _update;
    private bool? _canUpload;
    private bool _showingError;
    private readonly HashSet<string> _errorsShown = new();

    /// <summary>This app's version (SECRETOP_FAKE_VERSION overrides it, to test the update button).</summary>
    internal static Version AppVersion =>
        Environment.GetEnvironmentVariable("SECRETOP_FAKE_VERSION") is { Length: > 0 } fake && Version.TryParse(fake, out var v) ? v
        : System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);

    /// <summary>Installed by the installer (updates install themselves) rather than run from the zip (updates open the release page).</summary>
    internal static bool IsInstalledCopy =>
        AppContext.BaseDirectory.StartsWith(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Secret OP"),
            StringComparison.OrdinalIgnoreCase);

    internal static void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(Log.Root);
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { Log.Root }, UseShellExecute = false });
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception) { Log.Warn($"Couldn't open the log folder: {e.Message}"); }
    }

    // ---- errors ----

    /// <summary>Unexpected errors are logged; on the UI thread the app keeps running and says so (once per kind of error).</summary>
    private void InstallErrorHandlers()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            Log.Error("Unexpected error", e.Exception);
            e.Handled = true;
            ShowErrorOnce(e.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Error("Fatal error, the app has to close", e.ExceptionObject as Exception);
            try { MessageBox.Show($"Secret OP has to close because of an unexpected error:\n\n{(e.ExceptionObject as Exception)?.Message}\n\nThe details are in the log folder:\n{Log.Root}", "Secret OP", MessageBoxButton.OK, MessageBoxImage.Error); }
            catch (Exception) { }
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("Background task error", e.Exception);
            e.SetObserved();
        };
        Log.Info($"Secret OP {AppVersion.ToString(3)} starting on {Environment.OSVersion} ({(IsInstalledCopy ? "installed" : "portable")})");

        // Test hook: an error on the UI thread 5 seconds after start, to see the handler at work.
        if (Environment.GetEnvironmentVariable("SECRETOP_TEST_ERROR") == "1")
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            timer.Tick += (_, _) => { timer.Stop(); throw new InvalidOperationException("Test error (SECRETOP_TEST_ERROR)."); };
            timer.Start();
        }
    }

    private void ShowErrorOnce(Exception e)
    {
        if (_showingError || !_errorsShown.Add($"{e.GetType().FullName}|{e.Message}")) return;
        _showingError = true;
        try
        {
            var answer = MessageBox.Show(
                $"Secret OP ran into a problem and kept running:\n\n{e.Message}\n\nThe details are saved in the log. Open the log folder?",
                "Secret OP", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer == MessageBoxResult.Yes) OpenLogFolder();
        }
        finally { _showingError = false; }
    }

    // ---- patch changes ----

    /// <summary>Works out what this patch changed (in the background) and stops using last patch's games for those champions and items.</summary>
    private async Task LoadPatchChangesAsync(StaticData data)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        var changes = await DataDragon.LoadChangesAsync(http, DataDragon.DefaultCacheRoot, data.Version, _cts.Token);
        // Only meaningful when it compares this patch with the previous patch the saved builds are for.
        if (changes is not null && (changes.ToPatch != data.Patch || changes.FromPatch != BuildStore.PreviousPatch(data.Patch))) changes = null;
        _patchChanges = changes;
        if (_library is { } library) library.Changes = changes;
        _control?.ShowPatchChanges(DescribeChanges(changes));
        if (changes is not null) Log.Info($"Patch {changes.ToPatch}: {changes.Champions.Count} champions, {changes.Items.Count} items, {changes.Runes.Count} runes changed since {changes.FromPatch}");
    }

    private string? DescribeChanges(PatchChanges? c)
    {
        if (c is null) return null;
        if (c.Champions.Count == 0 && c.Items.Count == 0) return $"Patch {c.ToPatch} didn't change champions or items (in Riot's data), so {c.FromPatch} games count for everyone.";
        static string Names(IEnumerable<string> names, int total) { var list = names.Take(5).ToList(); return string.Join(", ", list) + (total > list.Count ? "…" : ""); }
        var parts = new List<string>();
        if (c.Champions.Count > 0)
            parts.Add($"{c.Champions.Count} champion{(c.Champions.Count == 1 ? "" : "s")} ({Names(c.Champions.Select(k => _data.ChampionByKey(k)?.Name ?? $"#{k}"), c.Champions.Count)})");
        if (c.Items.Count > 0)
            parts.Add($"{c.Items.Count} item{(c.Items.Count == 1 ? "" : "s")} ({Names(c.Items.Select(i => _data.Items.Get(i)?.Name ?? $"#{i}"), c.Items.Count)})");
        return $"Patch {c.ToPatch} changed {string.Join(" and ", parts)}: their {c.FromPatch} games aren't used, only {c.ToPatch} ones.";
    }

    // ---- importing builds files ----

    private static readonly HashSet<string> Positions = ["", "TOP", "JUNGLE", "MIDDLE", "BOTTOM", "UTILITY"];

    /// <summary>Only this patch and the previous one are worth importing (older ones would be deleted anyway).</summary>
    private Func<string, string, bool> AcceptPatch()
    {
        string patch = _data.Patch;
        string? previous = BuildStore.PreviousPatch(patch);
        return (_, p) => p == patch || p == previous;
    }

    /// <summary>Builds that make sense: a known champion, plausible item ids and a real position.</summary>
    private Func<BuildRecord, bool> ValidBuild()
    {
        var data = _data;
        return b => data.ChampionByKey(b.Champion) is not null && b.Items.All(i => i is > 0 and < 1_000_000) && b.Boots is >= 0 and < 1_000_000
                    && Positions.Contains(b.Position ?? "");
    }

    /// <summary>Merges a builds file in (collecting paused meanwhile, so it doesn't overwrite the merge), then carries on.</summary>
    private async Task<(BuildShare.Summary? Summary, string? Error)> ImportBuildsFileAsync(string path)
    {
        _crawlCts?.Cancel();
        if (_crawlTask is { } running) try { await running; } catch { }
        (BuildShare.Summary?, string?) result;
        try
        {
            var accept = AcceptPatch();
            var valid = ValidBuild();
            result = (await Task.Run(() => BuildShare.Import(BuildStore.DefaultRoot, path, accept, valid)), null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        {
            Log.Warn($"Builds file {Path.GetFileName(path)} not imported: {e.Message}");
            result = (null, e.Message);
        }
        RestartBuilds(); // reload from disk and carry on collecting
        return result;
    }

    // ---- builds shared on GitHub ----

    /// <summary>Downloads the builds shared on GitHub every 6 hours and, on a PC with push access, uploads new games.</summary>
    private async Task RunSharedBuildsAsync(CancellationToken ct)
    {
        using var http = UpdateChecker.CreateClient();
        http.Timeout = TimeSpan.FromMinutes(5);
        var nextDownload = DateTime.MinValue;
        try { await Task.Delay(TimeSpan.FromSeconds(20), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_data.IsLoaded && _library is not null)
                {
                    if (_settings.SharedBuildsDownload && DateTime.UtcNow >= nextDownload)
                    {
                        nextDownload = DateTime.UtcNow.AddHours(6);
                        await DownloadSharedBuildsAsync(http, ct);
                    }
                    if (_settings.SharedBuildsUpload && !_inGame) await UploadSharedBuildsAsync(ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or InvalidDataException
                                      or InvalidOperationException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception)
            {
                Log.Warn($"Shared builds: {e.Message}");
            }
            ShowSharedBuilds();
            try { await Task.Delay(TimeSpan.FromMinutes(_data.IsLoaded ? 10 : 1), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task DownloadSharedBuildsAsync(HttpClient http, CancellationToken ct)
    {
        var asset = await SharedBuilds.FindAsync(http, ct);
        if (asset is null) { Log.Info("Shared builds: none on GitHub yet."); return; }
        if (asset.Sha256 is { } known && known == _settings.SharedBuildsDigest) return; // already have this one
        string path = await SharedBuilds.DownloadAsync(http, asset, Path.Combine(Path.GetTempPath(), "SecretOP-shared"), ct);
        string sha = SharedBuilds.Sha256Of(path);
        if (sha == _settings.SharedBuildsDigest) return;
        var (summary, error) = await ImportBuildsFileAsync(path);
        if (error is not null) return;
        _settings.SharedBuildsDigest = sha;
        _settings.SharedBuildsDownloadedAt = DateTime.UtcNow;
        _settings.SharedBuildsDownloadedGames = summary?.Games ?? 0;
        _settings.Save();
        Log.Info($"Shared builds downloaded: {summary}");
    }

    /// <summary>Uploads all saved builds when there are enough new games (150+ and 2 hours since the last upload, or any after 12 hours).</summary>
    private async Task UploadSharedBuildsAsync(CancellationToken ct)
    {
        _canUpload ??= await SharedBuilds.CanUploadAsync(ct);
        if (_canUpload != true || _library is not { } library) return;
        int count = library.Current.MatchCount + (library.Previous?.MatchCount ?? 0);
        int fresh = count - _settings.SharedBuildsUploadedCount;
        if (fresh < 0) fresh = count; // a new patch pruned old games: count from scratch
        var since = DateTime.UtcNow - (_settings.SharedBuildsUploadedAt ?? DateTime.MinValue);
        if (!((fresh >= 150 && since >= TimeSpan.FromHours(2)) || (fresh > 0 && since >= TimeSpan.FromHours(12)))) return;

        library.Current.Save();
        library.Previous?.Save();
        string folder = Path.Combine(Path.GetTempPath(), "SecretOP-shared-upload");
        string file = Path.Combine(folder, SharedBuilds.FileName);
        await Task.Run(() => { Directory.CreateDirectory(folder); BuildShare.Export(BuildStore.DefaultRoot, file); }, ct);
        await SharedBuilds.UploadAsync(file, ct);
        _settings.SharedBuildsDigest = SharedBuilds.Sha256Of(file); // our own upload: nothing to import back
        _settings.SharedBuildsUploadedAt = DateTime.UtcNow;
        _settings.SharedBuildsUploadedCount = count;
        _settings.Save();
        Log.Info($"Shared builds uploaded: {count:N0} games ({fresh:N0} new).");
    }

    private static string Ago(DateTime utc)
    {
        var d = DateTime.UtcNow - utc;
        return d.TotalMinutes < 2 ? "just now" : d.TotalHours < 1 ? $"{d.TotalMinutes:0} min ago" : d.TotalDays < 1 ? $"{d.TotalHours:0} h ago" : $"{d.TotalDays:0} days ago";
    }

    /// <summary>One line for the Popular builds card and Settings: what came from / went to GitHub.</summary>
    internal string? SharedBuildsStatus()
    {
        var parts = new List<string>();
        if (_settings.SharedBuildsDownload && _settings.SharedBuildsDownloadedAt is { } down)
            parts.Add($"{(_settings.SharedBuildsDownloadedGames > 0 ? $"+{_settings.SharedBuildsDownloadedGames:N0} games" : "nothing new")} downloaded {Ago(down)}");
        if (_canUpload == true && _settings.SharedBuildsUpload && _settings.SharedBuildsUploadedAt is { } up)
            parts.Add($"{_settings.SharedBuildsUploadedCount:N0} games uploaded {Ago(up)}");
        return parts.Count == 0 ? null : "Shared builds on GitHub: " + string.Join(" · ", parts) + ".";
    }

    private void ShowSharedBuilds() => _control?.ShowSharedBuilds(SharedBuildsStatus());

    // ---- updates ----

    /// <summary>Looks for a newer version 15 seconds after start, then every 6 hours.</summary>
    private async Task RunUpdateChecksAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(15), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            if (_settings.CheckForUpdates) await CheckForUpdateAsync(ct);
            try { await Task.Delay(TimeSpan.FromHours(6), ct); } catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Checks GitHub now; returns a sentence for Settings.</summary>
    internal async Task<string> CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            using var http = UpdateChecker.CreateClient();
            var info = await UpdateChecker.CheckAsync(http, AppVersion, ct);
            _update = info;
            _control?.ShowUpdate(info, IsInstalledCopy || Environment.GetEnvironmentVariable("SECRETOP_UPDATE_DRYRUN") == "1");
            if (info is not null) Log.Info($"Update available: {info.Version.ToString(3)}");
            return info is null ? $"You have the latest version ({AppVersion.ToString(3)})." : $"Version {info.Version.ToString(3)} is available: click \"Update\" at the top of the window.";
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Log.Warn($"Update check failed: {e.Message}");
            return $"Couldn't check for updates: {e.Message}";
        }
    }

    /// <summary>
    /// Installed copy: downloads the new installer, checks it, and runs it silently (it closes this app, updates it and
    /// starts it again). Portable copy: opens the release page.
    /// </summary>
    private async Task InstallUpdateAsync()
    {
        if (_update is not { } info || _control is null) return;
        bool dryRun = Environment.GetEnvironmentVariable("SECRETOP_UPDATE_DRYRUN") == "1"; // test hook: download and check only
        if ((!IsInstalledCopy && !dryRun) || info.InstallerUrl is null)
        {
            try { Process.Start(new ProcessStartInfo(info.PageUrl) { UseShellExecute = true }); }
            catch (System.ComponentModel.Win32Exception e) { Log.Warn($"Couldn't open the release page: {e.Message}"); }
            return;
        }
        if (_inGame) return;
        string path;
        try
        {
            using var http = UpdateChecker.CreateClient();
            http.Timeout = TimeSpan.FromMinutes(15);
            var control = _control;
            path = await UpdateChecker.DownloadAsync(http, info, Path.Combine(Path.GetTempPath(), "SecretOP-update"),
                new Progress<double>(p => control.ShowUpdateProgress(p)), _cts.Token);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or InvalidDataException or InvalidOperationException)
        {
            Log.Warn($"Update download failed: {e.Message}");
            _control.ShowUpdateFailed(e.Message);
            return;
        }
        if (dryRun)
        {
            Log.Info($"Update {info.Version} downloaded and checked (dry run, not installing): {path}");
            _control.ShowUpdateFailed("Downloaded and checked (test run): not installing.");
            return;
        }
        Log.Info($"Installing update {info.Version.ToString(3)}");
        try
        {
            // Let the installer see the app as closed, then close it for real.
            try { _singleInstance?.ReleaseMutex(); } catch (ApplicationException) { }
            _singleInstance?.Dispose();
            _singleInstance = null;
            var start = new ProcessStartInfo(path) { UseShellExecute = false };
            foreach (var a in new[] { "/SILENT", "/SUPPRESSMSGBOXES", "/CLOSEAPPLICATIONS", "/NORESTART" }) start.ArgumentList.Add(a);
            Process.Start(start);
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            Log.Warn($"Couldn't start the installer: {e.Message}");
            _control.ShowUpdateFailed(e.Message);
            return;
        }
        Shutdown();
    }
}
