using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using LeagueOverlay.Core.Analysis;
using LeagueOverlay.Core.Builds;
using LeagueOverlay.Core.Data;
using LeagueOverlay.Core.History;
using LeagueOverlay.Core.Lcu;
using LeagueOverlay.Core.Live;

namespace LeagueOverlay;

public partial class App : Application
{
    private readonly CancellationTokenSource _cts = new();
    private Mutex? _singleInstance;
    private Settings _settings = new();
    private OverlayWindow? _window;
    private ControlWindow? _control;
    private IconCache? _icons;
    private StaticData _data = StaticData.Empty;
    private BuildLibrary? _library;
    private CancellationTokenSource? _crawlCts;
    private Task? _crawlTask;
    private volatile bool _inGame;
    // Records the current game for the History tab; the last game's timeline stays on "This game" until the next one.
    private readonly GameRecorder _recorder = new();
    private readonly GameRecordStore _games = new(GameRecordStore.DefaultRoot);
    private readonly StackTracker _stacks = new();
    // Surprise build: one plan per game (and champion) until rerolled.
    private readonly SurpriseSession _surprise = new();
    // The shop item set already sent for this lock-in (champion + plan), so it's written once.
    private string? _itemSetKey;
    // The League client (lobby / champion select), for rune suggestions before the game.
    private readonly LcuClient _lcu = new();
    // The surprise plan the current game is being played with (null when surprise mode is off), saved with the game.
    private SurprisePlan? _gameSurprise;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Headless: "LeagueOverlay.exe --export-builds <file>" writes the collected builds to a file and exits
        // (publish.ps1 uses it to put your builds into the installer). Works while the app is also running.
        if (e.Args is ["--export-builds", var exportPath])
        {
            int code = 0;
            try { BuildShare.Export(BuildStore.DefaultRoot, exportPath); }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { code = 1; }
            Shutdown(code);
            return;
        }

        _singleInstance = new Mutex(true, @"Local\LeagueOverlay.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            // Already running: bring its window to the front instead of starting a second copy.
            var existing = Native.FindWindow(null, "League Overlay");
            if (existing != 0) { Native.ShowWindow(existing, Native.SW_RESTORE); Native.SetForegroundWindow(existing); }
            Shutdown();
            return;
        }

        _settings = Settings.Load();
        _icons = new IconCache();
        _window = new OverlayWindow(_settings, _icons);
        _window.QuitRequested += Shutdown;
        _window.Initialize();

        _control = new ControlWindow(_settings, _window, _icons);
        _control.StopRequested += Shutdown;
        _control.BuildSettingsChanged += RestartBuilds;
        _control.ImportRequested += () => ImportRecentGames(userAsked: true);
        _control.SurpriseToggled += on => { _settings.SurpriseMode = on; _settings.Save(); };
        _control.RerollRequested += () => { _surprise.Reroll(); _itemSetKey = null; }; // a new plan (and item set) on the next read
        _control.BlockSurprise = BlockSurprise;
        _control.UnblockAllSurprises = () => { _settings.SurpriseBlocked.Clear(); _settings.Save(); _control?.RefreshSurpriseBlocks(refreshHistory: true); };
        _control.ApplyRunes = (page, replace) => _lcu.ApplyPageAsync(page, replace, _cts.Token);
        _control.RuneBenchmarks = () => _library?.Benchmarks;
        _control.ExportBuilds = ExportBuilds;
        _control.ImportBuilds = ImportBuildsAsync;
        _control.ShowHotkeyWarning(_window.ApplyHotkeys());
        var saved = _games.LoadRecent();
        _control.ShowGames(saved, openNewest: false);
        // Saved games without match-history numbers (played before a key was set) or saved before the app knew all of them.
        foreach (var g in saved.Where(g => g.PostGame is not { Format: >= PostGameStats.CurrentFormat })) FetchMatchHistory(g);
        _control.Show();

        _ = RunAsync(_cts.Token);
        _ = RunChampSelectAsync(_cts.Token);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _cts.Cancel();
        _crawlCts?.Cancel();
        GameRecord? unfinished;
        lock (_recorder) unfinished = _recorder.IsRecording ? _recorder.Finish() : null; // closed mid-game: keep it anyway
        if (unfinished is not null) SaveGame(unfinished, show: false);
        _window?.Close();
        _lcu.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// Main loop: read the game every PollSeconds (IdlePollSeconds when no game), analyse off the UI thread,
    /// and only touch the UI when the result actually changed.
    /// </summary>
    private async Task RunAsync(CancellationToken ct)
    {
        using var live = new LiveClient();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var data = StaticData.Empty;
        var nextDataAttempt = DateTime.MinValue;
        OverlayState? last = null;
        bool rendered = false;

        while (!ct.IsCancellationRequested)
        {
            LiveGame? game = null;
            try
            {
                if (!data.IsLoaded && DateTime.UtcNow >= nextDataAttempt)
                {
                    try
                    {
                        data = await DataDragon.LoadAsync(http, DataDragon.DefaultCacheRoot, ct);
                        _control?.SetDataStatus($"Item data: patch {data.Version}");
                        _control?.SetStaticData(data);
                        _window?.SetStaticData(data);
                        _data = data;
                        ImportBundledBuilds();
                        RestartBuilds();
                        ImportRecentGames(userAsked: false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Debug.WriteLine($"Data Dragon load failed: {ex.Message}");
                        _control?.SetDataStatus("Item data unavailable (offline?). Retrying every minute; gold and lanes still work.");
                        nextDataAttempt = DateTime.UtcNow.AddMinutes(1); // gold still works via client prices meanwhile
                    }
                }

                game = await live.TryGetAsync(ct);
                // Learn who "you" are from the game client, for fetching your recent games later.
                // Only from the real game client: a test client (LEAGUE_LIVE_URL) must not overwrite who you are.
                if (live.Uri == LiveClient.DefaultUri && game?.ActivePlayer?.RiotId is { Length: > 0 } myId && !RiotIds.Same(myId, _settings.MyRiotId))
                {
                    _settings.MyRiotId = myId;
                    _settings.Save();
                }
                var current = data;
                _inGame = game is not null;
                var library = _library;
                Func<int, string?, ChampionBuild?>? popular = library is null ? null : library.For;
                string? rivalId = _settings.Rival;
                var surprise = SurpriseFor(game, current);
                if (game is not null) _gameSurprise = surprise;
                else _surprise.NoGame();
                var (state, snapshot, finished, matchup, duel) = game is null ? (null, null, FinishGame(), null, null) : await Task.Run(() =>
                {
                    var s = OverlayState.Build(game, current, _settings.LevelWeight, popular, _stacks, surprise);
                    var mine = game.Me is { } m && current.Champion(m.ChampionKey) is { Key: > 0 } c ? library?.For(c.Key, m.Position) : null;
                    var players = RecordedPlayer.AllFrom(game, current.Items);
                    double minutes = (game.GameData?.GameTime ?? 0) / 60;
                    var lane = LaneMatchup.Find(players, minutes);
                    var rivalDuel = RivalDuel.Compare(players, rivalId, minutes);
                    lock (_recorder)
                    {
                        var (changed, previous) = _recorder.Update(game, current, s, mine);
                        return (s, changed ? _recorder.Snapshot() : null, previous, lane, rivalDuel);
                    }
                }, ct);
                if (snapshot is not null) _control?.ShowHistory(snapshot);
                _control?.ShowMatchup(matchup);
                _control?.ShowRivalLive(duel);
                if (finished is not null) SaveGame(finished, show: true);
                ShowChampionBuild(game, current, library);

                if (!rendered || !Equals(state, last))
                {
                    _window!.Render(state);
                    last = state;
                    rendered = true;
                }
                if (state is not null) _window!.KeepOnTop();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Debug.WriteLine($"Overlay loop error: {ex}"); // keep running; next poll may succeed
            }

            try { await Task.Delay(TimeSpan.FromSeconds(game is null ? _settings.IdlePollSeconds : _settings.PollSeconds), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>The surprise plan for this game when the mode is on: kept for the whole game, new for a new game, champion or reroll.</summary>
    private SurprisePlan? SurpriseFor(LiveGame? game, StaticData data)
    {
        if (!_settings.SurpriseMode || game?.Me is not { } me || data.Champion(me.ChampionKey) is not { } champ) return null;
        // The plan rolled when you locked in (runes already set to match it) carries into the game and stays for all of it.
        if (_surprise.Game(champ, () => RollSurprise(champ, data)))
            _control?.ShowSurprise(_surprise.Plan);
        return _surprise.Plan;
    }

    /// <summary>A new surprise plan: a style the champion's abilities scale with (weighted by fit), minus the ones you blocked.</summary>
    private SurprisePlan? RollSurprise(ChampionInfo champ, StaticData data) =>
        SurpriseBuilder.Plan(champ, data.Items, Random.Shared.Next(), data.Runes, data.Kits.For(champ.Key),
            _settings.SurpriseBlocked.ToHashSet(StringComparer.OrdinalIgnoreCase));

    /// <summary>"Don't suggest this again": blocks the champion + style, and rerolls if it's the current plan.</summary>
    private void BlockSurprise(string championId, string themeKey)
    {
        string key = SurpriseBuilder.BlockKey(championId, themeKey);
        if (!_settings.SurpriseBlocked.Contains(key, StringComparer.OrdinalIgnoreCase)) _settings.SurpriseBlocked.Add(key);
        _settings.Save();
        if (_surprise.Plan is { } plan && SurpriseBuilder.BlockKey(plan.ChampionKey, plan.Theme.Key) == key) { _surprise.Reroll(); _itemSetKey = null; }
        _control?.RefreshSurpriseBlocks(refreshHistory: true);
    }

    /// <summary>
    /// Champion select loop: asks the League client (not the game) every 2 seconds which champion you have, and shows
    /// the suggested runes on the overlay and in the app window. Idle while a game runs; every 5 seconds without a client.
    /// </summary>
    private async Task RunChampSelectAsync(CancellationToken ct)
    {
        ChampSelectView? last = null;
        bool shown = false;
        while (!ct.IsCancellationRequested)
        {
            ChampSelectView? view = null;
            try
            {
                if (!_inGame && _data.IsLoaded && await _lcu.TryChampSelectAsync(ct) is { ChampionId: > 0 } state &&
                    _data.ChampionByKey(state.ChampionId) is { } champ)
                    view = ChampSelectViewFor(state, champ);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { Debug.WriteLine($"Champion select read failed: {ex.Message}"); }

            if (!shown || !Equals(view, last))
            {
                _window?.RenderChampSelect(view);
                _control?.ShowChampSelect(view);
                last = view;
                shown = true;
            }
            if (view is { Locked: true }) await SendItemSetAsync(view, ct);
            try { await Task.Delay(TimeSpan.FromSeconds(view is not null || _lcu.IsConnected ? 2 : 5), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// What to show for your champion: in surprise mode the surprise build rolled on lock-in (kept until the game, a new
    /// champion or a reroll), otherwise the most common rune page and first items from collected games.
    /// </summary>
    private ChampSelectView ChampSelectViewFor(ChampSelectState state, ChampionInfo champ)
    {
        string? position = state.Position?.ToUpperInvariant() switch
        {
            "TOP" => "TOP", "JUNGLE" => "JUNGLE", "MIDDLE" or "MID" => "MIDDLE", "BOTTOM" => "BOTTOM", "UTILITY" => "UTILITY", _ => null,
        };
        string? roleLabel = position switch
        {
            "TOP" => "top", "JUNGLE" => "jungle", "MIDDLE" => "mid", "BOTTOM" => "ADC", "UTILITY" => "support", _ => null,
        };
        if (_settings.SurpriseMode)
        {
            if (!state.Locked) return new ChampSelectView(champ.Id, champ.Name, roleLabel, false, null, "", ValueList<int>.Empty, true);
            if (_surprise.ChampSelect(champ, () => RollSurprise(champ, _data)))
                _control?.ShowSurprise(_surprise.Plan);
            if (_surprise.Plan is { } plan)
            {
                var (themeStyle, themeAp) = StarterItems.ForTheme(plan.Theme.Key);
                var path = new ValueList<int>(plan.Path.Append(plan.Boots).Where(id => id > 0));
                return new ChampSelectView(champ.Id, champ.Name, roleLabel, true, plan.Runes, plan.Title, path, true)
                {
                    Starter = StarterItems.For(position, themeStyle, themeAp, smite: false),
                    FullPath = path,
                };
            }
        }
        var page = _library?.Runes(champ.Key, position);
        var build = _library?.For(champ.Key, position);
        string source = page is null ? "" : $"{page.Games:N0} games: {page.Share:P0} of them use it, {page.WinRate:P0} of those won";
        var (style, ap) = ItemRecommender.UsualProfile(champ);
        var boots = build?.BootsShare.OrderByDescending(b => b.Value).Select(b => b.Key).FirstOrDefault() ?? 0;
        return new ChampSelectView(champ.Id, champ.Name, roleLabel, state.Locked, page?.Setup, source,
            new ValueList<int>(build?.Path.Take(3) ?? []), false)
        {
            Starter = StarterItems.Best(build, position, style, ap, smite: false),
            FullPath = new ValueList<int>((build?.Path ?? []).Append(boots).Where(id => id > 0)),
        };
    }

    /// <summary>
    /// Once you lock in: a custom page in the in-game shop with the starting items and the build (the surprise build, or
    /// the popular one). Sent once per champion and plan; replaces only the app's own item set.
    /// </summary>
    private async Task SendItemSetAsync(ChampSelectView view, CancellationToken ct)
    {
        if (view.FullPath.Count == 0 && view.Starter.Count == 0) return;
        if (_data.Champion(view.ChampionId) is not { Key: > 0 } champ) return;
        string key = $"{view.ChampionId}|{view.Source}|{string.Join(",", view.FullPath)}";
        if (key == _itemSetKey) return;
        _itemSetKey = key;
        string title = view.Surprise ? view.Source : $"{view.ChampionName}{(view.Position is { } p ? " " + p : "")}";
        var core = view.FullPath.Where(id => _data.Items.Get(id) is not { IsBoots: true }).ToList();
        var boots = view.FullPath.Where(id => _data.Items.Get(id) is { IsBoots: true }).ToList();
        var blocks = new List<(string, IReadOnlyList<int>)>
        {
            ("Start", view.Starter.ToList()),
            (view.Surprise ? $"Surprise build: {view.Source}" : "Most common build", core),
            ("Boots", boots),
        };
        string result = await _lcu.ApplyItemSetAsync(title, champ.Key, blocks, ct);
        _control?.ShowItemSetStatus(result);
    }

    /// <summary>The game client went away: wrap up the recorded game (null if none, or too short to keep).</summary>
    private GameRecord? FinishGame()
    {
        lock (_recorder) return _recorder.IsRecording ? _recorder.Finish() : null;
    }

    private void SaveGame(GameRecord record, bool show)
    {
        // Keep which surprise build this game was played with.
        if (_gameSurprise is { } plan && string.Equals(plan.ChampionKey, record.ChampionKey, StringComparison.OrdinalIgnoreCase))
            record = record with { Surprise = SurpriseRecord.From(plan) };
        _gameSurprise = null;
        try { _games.Save(record); }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException) { Debug.WriteLine($"Saving game failed: {e.Message}"); return; }
        if (show) _control?.ShowGames(_games.LoadRecent(), openNewest: true);
        _control?.RecordRivalry(record);
        FetchMatchHistory(record);
    }

    private bool _importing;

    /// <summary>
    /// Fills History with your most recent games from Riot's match history that aren't in it yet
    /// (games played without the app running). Runs at startup and when asked from the History tab.
    /// </summary>
    private void ImportRecentGames(bool userAsked)
    {
        if (_control is null || _importing) return;
        if (_settings.ApiKey is not { } key)
        {
            if (userAsked) _control.ShowImportStatus("Add your Riot API key in Settings first.", busy: false);
            return;
        }
        if (_settings.MyRiotId is not { } me)
        {
            if (userAsked) _control.ShowImportStatus("Enter your Riot ID in Settings (or play a game with the app running) so the app knows whose games to fetch.", busy: false);
            return;
        }
        if (!_data.IsLoaded) { if (userAsked) _control.ShowImportStatus("Still loading item data; try again in a moment.", busy: false); return; }

        _importing = true;
        _control.ShowImportStatus("Fetching your recent games…", busy: true);
        var region = RiotRegion.Find(_settings.Region);
        var data = _data;
        var ct = _cts.Token;
        _ = Task.Run(async () =>
        {
            string status;
            try
            {
                using var client = new RiotClient(key, region);
                var found = await MatchImporter.ImportRecentAsync(client, me, data, _games.LoadRecent(), GameRecordStore.Keep, ct);
                foreach (var g in found) _games.Save(g);
                status = found.Count == 0 ? $"Up to date ({DateTime.Now:HH:mm})." : $"Added {found.Count} game{(found.Count == 1 ? "" : "s")} from match history.";
                await Dispatcher.InvokeAsync(() =>
                {
                    if (found.Count > 0) _control?.ShowGames(_games.LoadRecent(), openNewest: false);
                    foreach (var g in found) _control?.RecordRivalry(g);
                });
            }
            catch (RiotKeyException) { status = "Riot rejected the API key (development keys expire after 24 hours)."; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception e) when (e is HttpRequestException or System.IO.IOException or System.Text.Json.JsonException or KeyNotFoundException or TaskCanceledException)
            {
                status = e is HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound }
                    ? $"Riot doesn't know the Riot ID {me}. Check it in Settings."
                    : "Couldn't reach Riot right now. Try again in a bit.";
            }
            await Dispatcher.InvokeAsync(() => { _importing = false; _control?.ShowImportStatus(status, busy: false); });
        });
    }

    /// <summary>
    /// Adds Riot's final numbers (gold earned, damage, turret plates, wards, and the definite result) to a saved game.
    /// Match history usually has a game a minute or two after it ends, so this retries every minute for a while.
    /// </summary>
    private void FetchMatchHistory(GameRecord record)
    {
        if (record.PostGame is { Format: >= PostGameStats.CurrentFormat } || _settings.ApiKey is not { } key) return;
        var region = RiotRegion.Find(_settings.Region);
        var ct = _cts.Token;
        _ = Task.Run(async () =>
        {
            using var client = new RiotClient(key, region);
            var deadline = record.EndedAt + PostGameFetcher.GiveUpAfter;
            if (deadline < DateTime.Now) deadline = DateTime.Now.AddSeconds(5); // older game: one try
            for (bool first = true; DateTime.Now < deadline && !ct.IsCancellationRequested; first = false)
            {
                if (!first || DateTime.Now - record.EndedAt < TimeSpan.FromMinutes(1))
                    await Task.Delay(TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);
                try
                {
                    if (await PostGameFetcher.TryAsync(record, client, ct) is not { } enriched) continue;
                    _games.Save(enriched);
                    await Dispatcher.InvokeAsync(() =>
                    {
                        _control?.ShowGames(_games.LoadRecent(), openNewest: false);
                        _control?.RecordRivalry(enriched);
                    });
                    return;
                }
                catch (RiotKeyException) { return; }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception e) when (e is HttpRequestException or System.IO.IOException or System.Text.Json.JsonException or KeyNotFoundException or TaskCanceledException)
                {
                    Debug.WriteLine($"Match history lookup failed: {e.Message}"); // try again next minute
                }
            }
        });
    }

    private void ShowChampionBuild(LiveGame? game, StaticData data, BuildLibrary? library)
    {
        if (game?.Me is not { } me || data.Champion(me.ChampionKey) is not { } champ) { _control?.ShowChampionBuild(null, null); return; }
        _control?.ShowChampionBuild(champ.Name, library?.For(champ.Key, me.Position));
    }

    /// <summary>
    /// (Re)opens the saved popular builds for the current patch and region and, if there's an API key,
    /// starts collecting more in the background. Called once item data is loaded and whenever key or region change.
    /// </summary>
    private void RestartBuilds()
    {
        if (!_data.IsLoaded || _control is null) return;
        _crawlCts?.Cancel();
        _crawlCts = null;

        var region = RiotRegion.Find(_settings.Region);
        string patch = _data.Patch;
        string? previous = BuildStore.PreviousPatch(patch);
        var root = BuildStore.DefaultRoot;
        BuildStore.Prune(root, region.Platform, previous is null ? [patch] : [patch, previous]);
        var library = new BuildLibrary(BuildStore.Load(root, region.Platform, patch),
            previous is null ? null : BuildStore.Load(root, region.Platform, previous));
        _library = library; // games already collected are used even without a key

        if (_settings.ApiKey is not { } key)
        {
            _control.ShowBuildStatus(CrawlStatus.NoKey);
            return;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _crawlCts = cts;
        var client = new RiotClient(key, region);
        var crawler = new BuildCrawler(client, library, _data.Items, () => _inGame);
        var control = _control;
        crawler.StatusChanged += s => control.Dispatcher.BeginInvoke(() => { if (!cts.IsCancellationRequested) control.ShowBuildStatus(s); });
        control.ShowBuildStatus(crawler.Status);
        _crawlTask = Task.Run(async () =>
        {
            try { await crawler.RunAsync(cts.Token); }
            catch (Exception ex) { Debug.WriteLine($"Build crawler stopped: {ex}"); }
            finally { client.Dispose(); }
        });
    }

    /// <summary>Builds files shipped next to the app (the installer puts one there) are merged in at startup. Already-known games are skipped.</summary>
    private void ImportBundledBuilds()
    {
        try
        {
            foreach (var file in System.IO.Directory.GetFiles(AppContext.BaseDirectory, "*" + BuildShare.Extension))
            {
                var added = BuildShare.Import(BuildStore.DefaultRoot, file);
                Debug.WriteLine($"Bundled builds {System.IO.Path.GetFileName(file)}: {added}");
            }
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException or System.IO.InvalidDataException)
        {
            Debug.WriteLine($"Bundled builds not imported: {e.Message}");
        }
    }

    private string ExportBuilds(string path)
    {
        try
        {
            _library?.Current.Save(); // include games collected since the last save
            _library?.Previous?.Save();
            var summary = BuildShare.Export(BuildStore.DefaultRoot, path);
            return summary.Games == 0 ? "No builds collected yet." : $"Exported {summary}.";
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException) { return $"Couldn't export: {e.Message}"; }
    }

    private async Task<string> ImportBuildsAsync(string path)
    {
        // Stop collecting while merging, so the collector doesn't write its older copy over the merged file.
        _crawlCts?.Cancel();
        if (_crawlTask is { } running) try { await running; } catch { }
        string message;
        try
        {
            var summary = await Task.Run(() => BuildShare.Import(BuildStore.DefaultRoot, path));
            message = summary.Games == 0 ? "Nothing new: you already have all of these games." : $"Imported {summary}.";
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException or System.IO.InvalidDataException)
        {
            message = $"Couldn't import that file: {e.Message}";
        }
        RestartBuilds(); // reload from disk and carry on collecting
        return message;
    }
}
