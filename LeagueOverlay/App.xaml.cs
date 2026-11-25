using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using LeagueOverlay.Core.Analysis;
using LeagueOverlay.Core.Builds;
using LeagueOverlay.Core.Data;
using LeagueOverlay.Core.History;
using LeagueOverlay.Core.Lcu;
using LeagueOverlay.Core.Live;
using LeagueOverlay.Core.Diagnostics;

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
    // Surprise games counted and what they unlocked, for the account logged into the League client.
    private readonly RewardStore _rewardStore = new(RewardStore.DefaultRoot);
    private RewardProgress _rewards = new();
    private DateTime _accountCheckedAt = DateTime.MinValue;
    private readonly StackTracker _stacks = new();
    // Surprise build: one plan per game (and champion) until rerolled.
    private readonly SurpriseSession _surprise = new();
    // The shop item set already sent for this lock-in (champion + plan), so it's written once.
    private string? _itemSetKey;
    // The League client (lobby / champion select), for rune suggestions before the game.
    private readonly LcuClient _lcu = new();
    // Champion suggestions: the champion select being shown, the champions you can play and your mastery (fetched once
    // per champion select), and the "surprise me" pick, kept until a reroll or the end of champion select.
    private ChampSelectState? _champSelectState;
    private IReadOnlySet<int>? _pickable;
    private IReadOnlyDictionary<int, ChampMastery>? _mastery;
    private DateTime _pickDataAt = DateTime.MinValue;
    private int _pickSeed = Random.Shared.Next();
    private int? _pickSurprise;
    // The surprise plan the current game is being played with (null when surprise mode is off), saved with the game.
    private SurprisePlan? _gameSurprise;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        InstallErrorHandlers();

        // Headless: "SecretOP.exe --export-builds <file>" writes the collected builds to a file and exits
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
            var existing = Native.FindWindow(null, "Secret OP");
            if (existing != 0) { Native.ShowWindow(existing, Native.SW_RESTORE); Native.SetForegroundWindow(existing); }
            Shutdown();
            return;
        }

        _settings = Settings.Load();
        _icons = new IconCache();
        _window = new OverlayWindow(_settings, _icons);
        _window.Initialize();

        _control = new ControlWindow(_settings, _window, _icons);
        _control.StopRequested += Shutdown;
        _control.BuildSettingsChanged += RestartBuilds;
        _control.ImportRequested += () => ImportRecentGames(userAsked: true);
        _control.SurpriseToggled += on => { _settings.SurpriseMode = on; _settings.Save(); };
        _control.RerollRequested += () => { _surprise.Reroll(); _itemSetKey = null; }; // a new plan (and item set) on the next read
        _control.BlockSurprise = BlockSurprise;
        _control.PickModeChanged += mode => { _settings.ChampionPickMode = mode.ToString(); _settings.Save(); };
        _control.PickRerollRequested += () => { _pickSeed = Random.Shared.Next(); _pickSurprise = null; };
        _control.HoverRequested = async champion =>
            _champSelectState?.PickActionId is { } action ? await _lcu.HoverAsync(action, champion, _cts.Token) : "It's not your turn to pick yet.";
        _control.ApplyRunes = (page, replace) => _lcu.ApplyPageAsync(page, replace, _cts.Token);
        _control.RuneBenchmarks = () => _library?.Benchmarks;
        _control.ExportBuilds = ExportBuilds;
        _control.ImportBuilds = ImportBuildsAsync;
        _control.UpdateRequested += () => _ = InstallUpdateAsync();
        _control.CheckUpdatesNow = () => CheckForUpdateAsync(_cts.Token);
        _control.SharedBuildsInfo = () => (SharedBuildsStatus(), _canUpload == true);
        _control.ShowHotkeyWarning(_window.ApplyHotkeys());
        _control.SaveRewards = () => SaveRewards();
        _games.Account = _settings.MyRiotId;
        var saved = _games.LoadRecent();
        LoadRewards(saved);
        _control.ShowGames(saved, openNewest: false);
        // Saved games without match-history numbers (played before a key was set) or saved before the app knew all of them.
        foreach (var g in saved.Where(g => g.PostGame is not { Format: >= PostGameStats.CurrentFormat })) FetchMatchHistory(g);
        _control.Show();

        ShowSharedBuilds();

        _ = RunAsync(_cts.Token);
        _ = RunChampSelectAsync(_cts.Token);
        _ = RunSharedBuildsAsync(_cts.Token);
        _ = RunUpdateChecksAsync(_cts.Token);
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
                        _ = LoadPatchChangesAsync(data);
                        ImportRecentGames(userAsked: false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Log.Warn($"Data Dragon load failed: {ex.Message}");
                        _control?.SetDataStatus("Item data unavailable (offline?). Retrying every minute; gold and lanes still work.");
                        nextDataAttempt = DateTime.UtcNow.AddMinutes(1); // gold still works via client prices meanwhile
                    }
                }

                game = await live.TryGetAsync(ct);
                // Learn who "you" are from the game client, for fetching your recent games later.
                // Only from the real game client: a test client (LEAGUE_LIVE_URL) must not overwrite who you are.
                if (live.Uri == LiveClient.DefaultUri && game?.ActivePlayer?.RiotId is { Length: > 0 } myId && !RiotIds.Same(myId, _settings.MyRiotId))
                    SwitchAccount(myId);
                var current = data;
                bool started = game is not null && !_inGame, ended = game is null && _inGame;
                _inGame = game is not null;
                if (started) _control?.ShowLiveTab();
                else if (ended) _control?.ShowHistoryTab(); // a saved game also opens it there
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
                Log.Error("Overlay loop error", ex); // keep running; next poll may succeed
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
        PickAdvice? lastPicks = null;
        bool shown = false;
        while (!ct.IsCancellationRequested)
        {
            ChampSelectView? view = null;
            PickAdvice? picks = null;
            try
            {
                // Follow the account logged into the League client.
                if (DateTime.UtcNow - _accountCheckedAt > TimeSpan.FromSeconds(30))
                {
                    _accountCheckedAt = DateTime.UtcNow;
                    if (await _lcu.CurrentAccountAsync(ct) is { } account && !RiotIds.Same(account, _settings.MyRiotId)) SwitchAccount(account);
                }
                var state = !_inGame && _data.IsLoaded ? await _lcu.TryChampSelectAsync(ct) : null;
                _champSelectState = state;
                if (state is not null)
                {
                    if (state.ChampionId > 0 && _data.ChampionByKey(state.ChampionId) is { } champ) view = ChampSelectViewFor(state, champ);
                    picks = state.Locked ? null : await PickAdviceForAsync(state, ct);
                }
                else ResetPickSession();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { Log.Warn($"Champion select read failed: {ex.Message}"); }

            if (!shown || !Equals(view, last) || !Equals(picks, lastPicks))
            {
                _window?.RenderChampSelect(view, picks);
                _control?.ShowChampSelect(view, picks, inChampSelect: _champSelectState is not null);
                last = view;
                lastPicks = picks;
                shown = true;
            }
            if (view is { Locked: true }) await SendItemSetAsync(view, ct);
            try { await Task.Delay(TimeSpan.FromSeconds(_champSelectState is not null || _lcu.IsConnected ? 2 : 5), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private static string SurprisePickNote(SurprisePickOptions o) =>
        (o.AnyMastery ? (o.Even ? "Any champion, all equally likely" : "Any champion, least played more likely")
                      : (o.Even ? "A champion you've barely played, all equally likely" : "A champion you've barely played"))
        + (o.OnlyMyRole ? ", played in your role." : ", any role (off-meta picks included).");

    private void ResetPickSession()
    {
        _pickable = null;
        _mastery = null;
        _pickDataAt = DateTime.MinValue;
        if (_pickSurprise is not null) { _pickSurprise = null; _pickSeed = Random.Shared.Next(); }
    }

    /// <summary>Champion suggestions for this moment of champion select, in the chosen mode (null when off).</summary>
    private async Task<PickAdvice?> PickAdviceForAsync(ChampSelectState state, CancellationToken ct)
    {
        var mode = _settings.PickMode;
        if (mode == PickModeSetting.Off) return new PickAdvice(PickMode.Off, ValueList<PickSuggestion>.Empty, null, "", false);
        // What you own and your mastery don't change during champion select: fetch them once (and again after a minute).
        if (DateTime.UtcNow - _pickDataAt > TimeSpan.FromMinutes(1))
        {
            _pickable = await _lcu.PickableAsync(ct) ?? _pickable;
            _mastery = await _lcu.MasteryAsync(ct) ?? _mastery;
            _pickDataAt = DateTime.UtcNow;
        }
        string? position = LcuClient.NormalizePosition(state.Position);
        var unavailable = state.Bans.Concat(state.Allies.Select(a => a.Champion)).Concat(state.Enemies.Select(e => e.Champion))
            .Where(c => c > 0).ToHashSet();
        var ctx = new PickContext(position, state.Allies, state.Enemies, unavailable, _pickable, _mastery);
        var stats = _library?.Matchups;
        string role = ChampionRecommender.RoleLabel(position);
        string games = stats is { IsEmpty: false } ? $"{stats.Matches:N0} high-ranked games" : "champion data (collect games with an API key for win rates)";
        if (mode == PickModeSetting.Surprise)
        {
            var pick = ChampionRecommender.Surprise(ctx, _data, stats, _pickSeed, _pickSurprise, _settings.SurprisePicks);
            _pickSurprise = pick?.Key;
            return new PickAdvice(PickMode.Surprise, new ValueList<PickSuggestion>(pick is null ? [] : [pick]), role,
                _mastery is null ? "Couldn't read your mastery, so this is just random." : SurprisePickNote(_settings.SurprisePicks), state.PickActionId is not null);
        }
        var best = ChampionRecommender.Best(ctx, _data, stats);
        return new PickAdvice(PickMode.Best, new ValueList<PickSuggestion>(best), role,
            $"Best {role} picks for this draft, from {games}" + (_pickable is null ? "." : ", among champions you can play."), state.PickActionId is not null);
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
                    RuneOrigin = plan.Runes is null ? RuneOrigin.None : RuneOrigin.Surprise,
                    RuneSource = plan.Title,
                };
            }
        }
        var page = _library?.Runes(champ.Key, position);
        var build = _library?.For(champ.Key, position);
        string source = page is null ? "" : $"{page.Games:N0} games: {page.Share:P0} of them use it, {page.WinRate:P0} of those won";
        // No collected games with a page for this champion: a standard page for how it's usually built.
        var standard = page is null ? SurpriseBuilder.StandardRunes(champ, _data.Runes, _data.Kits.For(champ.Key)) : null;
        var (style, ap) = ItemRecommender.UsualProfile(champ);
        var boots = build?.BootsShare.OrderByDescending(b => b.Value).Select(b => b.Key).FirstOrDefault() ?? 0;
        return new ChampSelectView(champ.Id, champ.Name, roleLabel, state.Locked, page?.Setup ?? standard?.Page,
            page is null && standard is { } s ? $"standard {s.Style.ToLowerInvariant()} page" : source,
            new ValueList<int>(build?.Path.Take(3) ?? []), false)
        {
            Starter = StarterItems.Best(build, position, style, ap, smite: false),
            FullPath = new ValueList<int>((build?.Path ?? []).Append(boots).Where(id => id > 0)),
            RuneOrigin = page is not null ? RuneOrigin.Popular : standard is not null ? RuneOrigin.Standard : RuneOrigin.None,
            RuneSource = page is not null ? source : standard?.Style ?? "",
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

    /// <summary>
    /// Another account is logged into the League client (or played a game): remember it, and show its own History,
    /// surprise games and unlocks. Its recent games are fetched from Riot when there's a key.
    /// </summary>
    private void SwitchAccount(string account)
    {
        _settings.MyRiotId = account;
        _settings.Save();
        _games.Account = account;
        var games = _games.LoadRecent();
        LoadRewards(games);
        _control?.ShowGames(games, openNewest: false);
        _control?.RefreshAccount();
        ImportRecentGames(userAsked: false);
    }

    /// <summary>This account's rewards; games saved before rewards existed are counted the first time.</summary>
    private void LoadRewards(IReadOnlyList<GameRecord> games)
    {
        _rewards = _rewardStore.Load(_settings.MyRiotId);
        if (!_rewards.Backfilled)
        {
            Rewards.Backfill(_rewards, games);
            SaveRewards();
        }
        _control?.ShowRewards(_rewards, []);
    }

    private void SaveRewards()
    {
        try { _rewardStore.Save(_settings.MyRiotId, _rewards); }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException) { Log.Warn($"Saving rewards failed: {e.Message}"); }
    }

    /// <summary>Counts a saved game toward the surprise rewards and tells the window about anything it unlocked.</summary>
    private void CountRewards(GameRecord record)
    {
        if (!GameRecordStore.Belongs(record, _settings.MyRiotId)) return;
        var unlocked = Rewards.Record(_rewards, record);
        if (unlocked.Count == 0 && !Rewards.Counts(record)) return;
        SaveRewards();
        _control?.ShowRewards(_rewards, unlocked);
    }

    private void SaveGame(GameRecord record, bool show)
    {
        // Keep which surprise build this game was played with.
        if (_gameSurprise is { } plan && string.Equals(plan.ChampionKey, record.ChampionKey, StringComparison.OrdinalIgnoreCase))
            record = record with { Surprise = SurpriseRecord.From(plan) };
        _gameSurprise = null;
        try { _games.Save(record); }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException) { Log.Warn($"Saving game failed: {e.Message}"); return; }
        CountRewards(record);
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
            if (userAsked) _control.ShowImportStatus("Open the League client so the app knows which account is yours.", busy: false);
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
                    ? $"Riot doesn't know the Riot ID {me}. Log into the League client so the app picks up your account."
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
                        CountRewards(enriched); // the definite result: a surprise win may count now
                    });
                    return;
                }
                catch (RiotKeyException) { return; }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception e) when (e is HttpRequestException or System.IO.IOException or System.Text.Json.JsonException or KeyNotFoundException or TaskCanceledException)
                {
                    Log.Warn($"Match history lookup failed: {e.Message}"); // try again next minute
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
        library.Changes = _patchChanges; // last patch's games only for what this patch didn't change
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
            catch (Exception ex) { Log.Error("Build crawler stopped", ex); }
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
                var added = BuildShare.Import(BuildStore.DefaultRoot, file, AcceptPatch(), ValidBuild());
                Log.Info($"Bundled builds {System.IO.Path.GetFileName(file)}: {added}");
            }
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException or System.IO.InvalidDataException)
        {
            Log.Warn($"Bundled builds not imported: {e.Message}");
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
        var (summary, error) = await ImportBuildsFileAsync(path);
        return error is not null ? $"Couldn't import that file: {error}"
            : summary is not { Games: > 0 } ? "Nothing new: you already have these games (or they're for older patches)."
            : $"Imported {summary}.";
    }
}
