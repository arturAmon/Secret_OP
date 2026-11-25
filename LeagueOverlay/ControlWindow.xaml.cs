using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LeagueOverlay.Core.Analysis;
using LeagueOverlay.Core.Builds;
using LeagueOverlay.Core.Data;
using LeagueOverlay.Core.History;
using LeagueOverlay.Core.Lcu;

namespace LeagueOverlay;

/// <summary>Main window shown at launch: status, current hotkeys, Settings / Move overlay / Stop. Closing it stops the app.</summary>
public partial class ControlWindow : Window
{
    private readonly Settings _settings;
    private readonly OverlayWindow _overlay;
    private readonly IconCache _icons;
    private HistorySnapshot? _pendingHistory;
    private readonly HistoryView _historyView;
    private StaticData _data = StaticData.Empty;
    private readonly RivalryLog _rivalry = new(RivalryLog.DefaultPath);

    /// <summary>Export / import of popular builds, provided by the app (they need the running collector).</summary>
    public Func<string, string>? ExportBuilds { get; set; }
    public Func<string, Task<string>>? ImportBuilds { get; set; }

    /// <summary>Creates a rune page in the League client (page, replace current if full) and returns what happened.</summary>
    public Func<RuneSetup, bool, Task<string>>? ApplyRunes { get; set; }
    /// <summary>Typical rune numbers from collected games, for the post-game rune analysis.</summary>
    public Func<RuneBenchmarks?>? RuneBenchmarks { get; set; }
    private ChampSelectView? _champSelect;

    /// <summary>Surprise build mode switched on/off, or a new surprise build asked for.</summary>
    public event Action<bool>? SurpriseToggled;
    public event Action? RerollRequested;
    private SurprisePlan? _surprisePlan;

    /// <summary>Raised when the user asks to fetch recent games (or changed their Riot ID in Settings).</summary>
    public event Action? ImportRequested;

    /// <summary>Raised when the rival changes (from Settings or a ☆ in the scoreboard).</summary>
    public event Action? RivalChanged;

    public event Action? StopRequested;
    /// <summary>Raised after Settings was saved with a different API key or region.</summary>
    public event Action? BuildSettingsChanged;

    public ControlWindow(Settings settings, OverlayWindow overlay, IconCache icons)
    {
        _settings = settings;
        _overlay = overlay;
        _icons = icons;
        InitializeComponent();
        SourceInitialized += (_, _) => Native.UseDarkTitleBar(this);
        _overlay.OverlayStateChanged += RefreshStatus;
        Closed += (_, _) => StopRequested?.Invoke();
        Graph.Icons = _icons;
        _historyView = new HistoryView(_icons, id => _data.Items.Get(id)?.Name, () => _data.Items, () => _settings.Rival, SetRival, Share)
        {
            Data = () => _data,
            Benchmarks = () => RuneBenchmarks?.Invoke(),
            BlockSurprise = (champ, theme) => BlockSurprise?.Invoke(champ, theme),
            IsSurpriseBlocked = (champ, theme) => _settings.SurpriseBlocked.Contains(SurpriseBuilder.BlockKey(champ, theme), StringComparer.OrdinalIgnoreCase),
            Progress = () => _rewards,
            PickStyle = PickStyle,
        };
        _collection = new CollectionView(_icons);
        ReplacePageBox.IsChecked = _settings.RunesReplaceCurrent;
        PickModeBox.SelectedIndex = (int)_settings.PickMode;
        _icons.IconLoaded += Graph.InvalidateVisual;
        // No point drawing the graph while minimised; catch up when the window comes back.
        StateChanged += (_, _) => { if (WindowState != WindowState.Minimized && _pendingHistory is { } h) ShowHistory(h); };
        RefreshHotkeys();
        RefreshStatus();
        if (System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version is { } v) VersionRun.Text = $"  v{v.ToString(3)}";
        RefreshRival();
        SurpriseBox.IsChecked = _settings.SurpriseMode;
        ShowSurprise(null);
        _icons.IconLoaded += () => { if (_surprisePlan is not null) ShowSurprise(_surprisePlan); };
        _icons.IconLoaded += () => { if (_champSelect is not null || _picks is not null) ShowChampSelect(_champSelect, _picks, _inChampSelect); };
    }

    public void ShowHotkeyWarning(IReadOnlyList<string> failed)
    {
        HotkeyWarning.Visibility = failed.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        HotkeyWarning.Text = failed.Count > 0
            ? $"Another program already uses: {string.Join(", ", failed)}. Pick different keys in Settings."
            : "";
    }

    public void SetDataStatus(string text) => DataStatus.Text = text;

    public void SetStaticData(StaticData data)
    {
        _data = data;
        _icons.SetVersion(data.Version);
    }

    /// <summary>Fills the History tab. With openNewest (a game just ended) it switches to the tab and opens that game.</summary>
    public void ShowGames(IReadOnlyList<GameRecord> games, bool openNewest)
    {
        var selected = (GameList.SelectedItem as ListBoxItem)?.Tag as GameRecord;
        GameList.Items.Clear();
        foreach (var g in games) GameList.Items.Add(new ListBoxItem { Content = _historyView.ListEntry(g), Tag = g });
        NoHistoryText.Visibility = games.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (games.Count == 0) { GameDetail.Children.Clear(); return; }

        int index = openNewest || selected is null ? 0 : Math.Max(0, games.ToList().FindIndex(g => g.EndedAt == selected.EndedAt));
        GameList.SelectedIndex = index;
        if (openNewest) HistoryTab.IsChecked = true;
    }

    private void GameList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ShowSelectedGame();
        DetailScroll.ScrollToTop();
    }

    private void ShowSelectedGame()
    {
        if ((GameList.SelectedItem as ListBoxItem)?.Tag is GameRecord g) _historyView.ShowDetail(GameDetail, g);
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (LivePanel is null || HistoryPanel is null || CollectionPanel is null) return; // still loading
        LivePanel.Visibility = LiveTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        HistoryPanel.Visibility = HistoryTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        CollectionPanel.Visibility = CollectionTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (CollectionTab.IsChecked == true) { CollectionDot.Visibility = Visibility.Collapsed; RefreshCollection(); }
    }

    // ---- surprise rewards (Collection tab) ----

    private readonly CollectionView _collection;
    private RewardProgress? _rewards;
    /// <summary>Saves the reward progress (after a share style was picked).</summary>
    public Action? SaveRewards { get; set; }

    /// <summary>The account's surprise rewards; <paramref name="unlocked"/> = just unlocked (marks the Collection tab).</summary>
    public void ShowRewards(RewardProgress progress, IReadOnlyList<Achievement> unlocked)
    {
        _rewards = progress;
        if (unlocked.Count > 0 && CollectionTab.IsChecked != true) CollectionDot.Visibility = Visibility.Visible;
        if (CollectionTab.IsChecked == true) RefreshCollection();
    }

    private void RefreshCollection()
    {
        if (_rewards is null) { CollectionHost.Children.Clear(); return; }
        var sample = GameList.Items.OfType<ListBoxItem>().Select(i => i.Tag).OfType<GameRecord>().FirstOrDefault();
        _collection.Show(CollectionHost, _rewards, sample, PickStyle);
    }

    private void PickStyle(CardStyle style)
    {
        if (_rewards is null) return;
        _rewards.Style = style;
        SaveRewards?.Invoke();
        if (CollectionTab.IsChecked == true) RefreshCollection();
    }

    /// <summary>The account changed (League client): refresh what shows it.</summary>
    public void RefreshAccount() => RefreshStatus();

    /// <summary>A game started: show it.</summary>
    public void ShowLiveTab() => LiveTab.IsChecked = true;

    /// <summary>A game ended: back to History.</summary>
    public void ShowHistoryTab() => HistoryTab.IsChecked = true;

    public void ShowBuildStatus(CrawlStatus s)
    {
        string region = RiotRegion.Find(s.Region).Label;
        BuildStatus.Text = s.State switch
        {
            CrawlState.NoKey => "Add your Riot API key in Settings to base item suggestions on what high-ranked players build. Until then they're rule-based.",
            CrawlState.Starting => $"Connecting to Riot ({region})…",
            CrawlState.Collecting when s.CollectingRunes => $"Collecting rune pages in {region}: {s.RuneGames:N0} of {BuildCrawler.RuneTargetGames:N0} games for patch {s.Patch} (item builds are complete with {s.Games:N0} games).",
            CrawlState.Collecting => $"Collecting Challenger and Grandmaster games in {region}: {s.Games:N0} of {s.Target:N0} for patch {s.Patch}. A champion's suggestions switch over once it has {BuildLibrary.MinGames} games.",
            CrawlState.Paused => $"Paused while you're in a game, so it doesn't use your connection. {s.Games:N0} games collected for patch {s.Patch}.",
            CrawlState.Complete => $"Up to date: {s.Games:N0} games from {region} for patch {s.Patch}.",
            CrawlState.KeyRejected => $"Riot rejected the API key. Development keys expire after 24 hours: get a new one at developer.riotgames.com and paste it in Settings. The {s.Games:N0} games already collected are still used.",
            CrawlState.Offline => $"Can't reach Riot right now; trying again every minute. The {s.Games:N0} games already collected are still used.",
            _ => "",
        };
        BuildStatus.Foreground = (Brush)FindResource(s.State == CrawlState.KeyRejected ? "ErrBrush" : "MutedBrush");
        BuildProgress.Visibility = s.State is CrawlState.NoKey ? Visibility.Collapsed : Visibility.Visible;
        BuildProgress.Value = s.CollectingRunes ? s.RuneGames / (double)BuildCrawler.RuneTargetGames : Math.Min(1, s.Games / (double)Math.Max(1, s.Target));
    }

    /// <summary>What the suggestions for the current champion are based on (null name = not in a game).</summary>
    public void ShowChampionBuild(string? champion, ChampionBuild? build)
    {
        ChampionBuildText.Visibility = champion is null ? Visibility.Collapsed : Visibility.Visible;
        if (champion is null) return;
        string role = build?.Position is { } p ? $" {RoleName(p)}" : "";
        ChampionBuildText.Text = build is null
            ? $"{champion}: not enough games yet ({BuildLibrary.MinGames} needed), so suggestions are rule-based."
            : $"{champion}{role}: suggestions from {build.Games:N0} games (patch {build.Patch}, {build.WinRate:P0} won).";
    }

    private static string RoleName(string position) => position switch
    {
        "TOP" => "top", "JUNGLE" => "jungle", "MIDDLE" => "mid", "BOTTOM" => "ADC", "UTILITY" => "support", _ => position.ToLowerInvariant(),
    };

    public void ShowHistory(HistorySnapshot history)
    {
        if (WindowState == WindowState.Minimized) { _pendingHistory = history; return; }
        _pendingHistory = null;
        Graph.History = history;
    }

    /// <summary>The surprise build card: the plan for this game, or what the mode does when there's no game yet.</summary>
    public void ShowSurprise(SurprisePlan? plan)
    {
        _surprisePlan = plan;
        bool on = SurpriseBox.IsChecked == true;
        SurpriseItems.Children.Clear();
        SurpriseRunes.Content = null;
        SurpriseTitle.Visibility = SurpriseButtons.Visibility = on && plan is not null ? Visibility.Visible : Visibility.Collapsed;
        RefreshSurpriseBlocks();
        if (!on)
        {
            SurpriseText.Text = "Off. When on, every game gets an unexpected build your champion still scales with (AP Ezreal, healing Senna...).";
            return;
        }
        if (plan is null)
        {
            SurpriseText.Text = "On. Your next game gets a surprise build; the overlay will suggest its items.";
            return;
        }
        SurpriseTitle.Text = plan.Title;
        SurpriseText.Text = plan.Theme.Pitch;
        SurpriseRunes.Content = plan.Runes is { } runes && _data.Runes.IsLoaded ? RunePanel.Page(runes, _data.Runes, _icons, (Brush)FindResource("MutedBrush")) : null;
        foreach (var id in plan.Path.Append(plan.Boots).Where(id => id > 0))
        {
            SurpriseItems.Children.Add(new Border
            {
                Width = 30, Height = 30, CornerRadius = new CornerRadius(4), ClipToBounds = true, Margin = new Thickness(0, 0, 4, 4),
                Background = (Brush)FindResource("Panel2"), ToolTip = _data.Items.Get(id)?.Name,
                Child = new System.Windows.Controls.Image { Source = _icons.Item(id) },
            });
        }
    }

    private void Surprise_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return; // initial value
        SurpriseToggled?.Invoke(SurpriseBox.IsChecked == true);
        ShowSurprise(SurpriseBox.IsChecked == true ? _surprisePlan : null);
    }

    private void Reroll_Click(object sender, RoutedEventArgs e) => RerollRequested?.Invoke();

    /// <summary>Blocks a champion + surprise style (championId, themeKey); provided by the app.</summary>
    public Action<string, string>? BlockSurprise { get; set; }
    public Action? UnblockAllSurprises { get; set; }

    private void Block_Click(object sender, RoutedEventArgs e)
    {
        if (_surprisePlan is { } plan) BlockSurprise?.Invoke(plan.ChampionKey, plan.Theme.Key);
    }

    private void UnblockAll_Click(object sender, RoutedEventArgs e) => UnblockAllSurprises?.Invoke();

    /// <summary>How many champion + style combinations are blocked, with a way to allow them again.</summary>
    public void RefreshSurpriseBlocks(bool refreshHistory = false)
    {
        int n = _settings.SurpriseBlocked.Count;
        BlockedRow.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        BlockedText.Text = n == 1 ? $"Not suggested again: {Describe(_settings.SurpriseBlocked[0])}."
            : $"{n} builds won't be suggested again ({string.Join(", ", _settings.SurpriseBlocked.Take(3).Select(Describe))}{(n > 3 ? "…" : "")}).";
        if (refreshHistory) ShowSelectedGame(); // the History button turns into "won't be suggested again"

        string Describe(string key)
        {
            var parts = key.Split(':');
            string champ = parts[0] is var id && _data.Champion(id) is { } c ? c.Name : parts[0];
            string style = parts.Length > 1 ? SurpriseBuilder.Themes.FirstOrDefault(t => t.Key == parts[1])?.Label ?? parts[1] : "";
            return $"{style} {champ}";
        }
    }

    /// <summary>Champion suggestions mode changed / another surprise pick asked for / hover a champion (returns what happened).</summary>
    public event Action<PickModeSetting>? PickModeChanged;
    public event Action? PickRerollRequested;
    public Func<int, Task<string>>? HoverRequested { get; set; }
    private PickAdvice? _picks;
    private bool _inChampSelect;

    private void PickMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || PickModeBox.SelectedItem is not ComboBoxItem { Tag: string tag } || !Enum.TryParse<PickModeSetting>(tag, out var mode)) return;
        PickModeChanged?.Invoke(mode);
        PicksNote.Text = mode == PickModeSetting.Off ? "Off." : "Updating…";
        PicksList.Children.Clear();
    }

    private void PickReroll_Click(object sender, RoutedEventArgs e) => PickRerollRequested?.Invoke();

    /// <summary>Suggested champions: icon, name, why, and a Hover button that shows it to your team in the client.</summary>
    private void ShowPicks(PickAdvice? picks)
    {
        _picks = picks;
        PicksList.Children.Clear();
        bool any = picks is { Picks.Count: > 0 };
        PicksSection.Visibility = picks is null || picks.Mode == PickMode.Off ? Visibility.Collapsed : Visibility.Visible;
        PickRerollButton.Visibility = picks is { Mode: PickMode.Surprise } ? Visibility.Visible : Visibility.Collapsed;
        if (picks is null) return;
        PicksNote.Text = any ? picks.Note : picks.Mode == PickMode.Surprise ? "No champion fits right now." : "No suggestions yet.";
        foreach (var pick in picks.Picks)
        {
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
            var icon = new Border
            {
                Width = 34, Height = 34, CornerRadius = new CornerRadius(4), ClipToBounds = true, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Top,
                Background = (Brush)FindResource("Panel2"), Child = new System.Windows.Controls.Image { Source = _icons.Champion(pick.ChampionId) },
            };
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            if (picks.CanHover && HoverRequested is not null)
            {
                var hover = new Button { Content = "Hover", Padding = new Thickness(8, 2, 8, 2), FontSize = 11, VerticalAlignment = VerticalAlignment.Top,
                                         ToolTip = $"Show {pick.Name} to your team in the client (doesn't lock it in)" };
                int key = pick.Key;
                hover.Click += async (_, _) => HoverStatus.Text = await HoverRequested(key);
                DockPanel.SetDock(hover, Dock.Right);
                row.Children.Add(hover);
            }
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = pick.Name, FontWeight = FontWeights.SemiBold });
            foreach (var reason in pick.Reasons)
                text.Children.Add(new TextBlock { Text = reason, FontSize = 11, Foreground = (Brush)FindResource(reason.StartsWith("Careful") ? "ErrBrush" : "MutedBrush"), TextWrapping = TextWrapping.Wrap });
            row.Children.Add(text);
            PicksList.Children.Add(row);
        }
    }

    /// <summary>
    /// The champion select card (champion suggestions until you lock in, then your champion's starting items) and the
    /// runes card (the page, where it comes from, and the Apply button). Hidden outside champion select.
    /// </summary>
    public void ShowChampSelect(ChampSelectView? view, PickAdvice? picks = null, bool inChampSelect = false)
    {
        _inChampSelect = inChampSelect;
        bool changed = view?.ChampionId != _champSelect?.ChampionId || view?.Locked != _champSelect?.Locked || !Equals(view?.Runes, _champSelect?.Runes);
        _champSelect = view;
        ChampSelectCard.Visibility = view is null && !inChampSelect ? Visibility.Collapsed : Visibility.Visible;
        ShowPicks(view is { Locked: true } ? null : picks);
        PicksDivider.Visibility = view is not null && picks is not null && view is not { Locked: true } && picks.Mode != PickMode.Off ? Visibility.Visible : Visibility.Collapsed;
        ChampSelectText.Visibility = ItemSetStatus.Visibility = RunesCard.Visibility = view is null ? Visibility.Collapsed : Visibility.Visible;
        if (view is null)
        {
            ApplyRunesStatus.Text = ItemSetStatus.Text = "";
            ChampSelectStarterLabel.Visibility = Visibility.Collapsed;
            ChampSelectStarter.Children.Clear();
            if (!inChampSelect) HoverStatus.Text = "";
            return;
        }
        if (changed) ApplyRunesStatus.Text = "";
        ChampSelectStarter.Children.Clear();
        ChampSelectStarterLabel.Visibility = view.Starter.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var g in view.Starter.GroupBy(id => id))
            ChampSelectStarter.Children.Add(new Border
            {
                Width = 26, Height = 26, CornerRadius = new CornerRadius(3), ClipToBounds = true, Margin = new Thickness(0, 0, 4, 0),
                Background = (Brush)FindResource("Panel2"), ToolTip = (g.Count() > 1 ? $"{g.Count()}× " : "") + _data.Items.Get(g.Key)?.Name,
                Child = new System.Windows.Controls.Image { Source = _icons.Item(g.Key) },
            });
        string role = view.Position is { } p ? $" {p}" : "";
        ChampSelectText.Text = view.Surprise
            ? view.Locked ? $"{view.ChampionName}{role}: surprise build {view.Source}." : $"{view.ChampionName}: lock in to roll your surprise build."
            : $"{view.ChampionName}{role}{(view.Locked ? "" : " (hovering)")}.";
        ShowRuneOrigin(view);
        ChampSelectRunes.Content = view.Runes is { } page && _data.Runes.IsLoaded ? RunePanel.Page(page, _data.Runes, _icons, (Brush)FindResource("MutedBrush")) : null;
        ApplyRunesButton.IsEnabled = view.Runes is not null && ApplyRunes is not null;
        ApplyRunesButton.Visibility = ReplacePageBox.Visibility = view.Runes is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>The runes card's tag and text: whether the page comes from surprise mode, collected games or a standard page.</summary>
    private void ShowRuneOrigin(ChampSelectView view)
    {
        string role = view.Position is { } p ? " " + p : "";
        var (tag, brush, text) = view.RuneOrigin switch
        {
            RuneOrigin.Surprise => ("Surprise mode", "Accent3Brush",
                $"From surprise build mode: these runes match your {view.RuneSource} build. Reroll it in the Surprise build mode card to change both."),
            RuneOrigin.Popular => ("Popular page", "Accent2Brush",
                $"The page high-ranked players run most on {view.ChampionName}{role}: {view.RuneSource}."),
            RuneOrigin.Standard => ("Standard page", "AccentBrush",
                $"A standard {view.RuneSource.ToLowerInvariant()} page for {view.ChampionName}, until collected high-ranked games have one for this " +
                "champion (collecting needs a Riot API key in Settings, or import a builds file)."),
            _ when view.Surprise && !view.Locked => ("Surprise mode", "Accent3Brush",
                "Surprise build mode is on: lock in and the runes for your surprise build show here."),
            _ => ("No runes", "MutedBrush", "Rune data isn't loaded yet."),
        };
        var color = (Brush)FindResource(brush);
        RuneOriginText.Text = tag;
        RuneOriginText.Foreground = color;
        RuneOriginTag.BorderBrush = color;
        RunesText.Text = text;
    }

    /// <summary>What happened to the shop item set sent on lock-in.</summary>
    public void ShowItemSetStatus(string text) => ItemSetStatus.Text = text;

    private async void ApplyRunes_Click(object sender, RoutedEventArgs e)
    {
        if (_champSelect?.Runes is not { } page || ApplyRunes is null) return;
        ApplyRunesButton.IsEnabled = false;
        ApplyRunesStatus.Text = "Applying…";
        try { ApplyRunesStatus.Text = await ApplyRunes(page, _settings.RunesReplaceCurrent); }
        finally { ApplyRunesButton.IsEnabled = _champSelect?.Runes is not null; }
    }

    private void ReplacePage_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _settings.RunesReplaceCurrent = ReplacePageBox.IsChecked == true;
        _settings.Save();
    }

    public void ShowImportStatus(string text, bool busy)
    {
        ImportStatus.Text = text;
        ImportButton.IsEnabled = !busy;
    }

    private void Import_Click(object sender, RoutedEventArgs e) => ImportRequested?.Invoke();

    /// <summary>You vs your rival in the current game; null (rival not in it) hides the card.</summary>
    public void ShowRivalLive(RivalDuel? duel)
    {
        RivalHost.Visibility = duel is null ? Visibility.Collapsed : Visibility.Visible;
        RivalHost.Content = duel is null ? null : _historyView.DuelView(duel, live: true);
    }

    /// <summary>Who the rival is and the head-to-head record (games where one of you won more stats).</summary>
    public void RefreshRival()
    {
        if (_settings.Rival is not { } rival)
        {
            RivalText.Text = "Pick the friend you compete with: click ☆ next to them in a game's scoreboard (History tab), or enter their Riot ID in Settings.";
            return;
        }
        var (games, mine, theirs) = _rivalry.Tally(rival);
        string name = RiotIds.GameName(rival);
        RivalText.Text = games == 0
            ? $"{rival}. Your stats are compared in every game you play together."
            : $"{rival}: {games} game{(games == 1 ? "" : "s")} together. You out-stat {name} in {mine}, {name} wins {theirs}" + (games - mine - theirs > 0 ? $", {games - mine - theirs} tied." : ".");
    }

    /// <summary>Records the duel for the rivalry tally (and updates the card) if the rival played this game.</summary>
    public void RecordRivalry(GameRecord g)
    {
        if (RivalDuel.Compare(g.Players, _settings.Rival, g.Duration / 60, g.PostGame) is not { } duel) return;
        try { _rivalry.Record(g, duel); } catch (System.IO.IOException) { }
        RefreshRival();
    }

    private void SetRival(string riotId)
    {
        _settings.Rival = riotId;
        _settings.Save();
        OnRivalChanged();
    }

    private void OnRivalChanged()
    {
        // Games already saved with this player count toward the tally straight away.
        foreach (var g in GameList.Items.OfType<ListBoxItem>().Select(i => i.Tag).OfType<GameRecord>()) RecordRivalry(g);
        RefreshRival();
        ShowSelectedGame();
        RivalChanged?.Invoke();
    }

    /// <summary>Copies the share picture to the clipboard, or saves it as a PNG. Returns what happened, for the button.</summary>
    private string? Share(GameRecord g, RivalDuel? duel, bool saveToFile)
    {
        try
        {
            var image = ShareCard.Render(g, duel, _icons, _rewards is null ? null : Rewards.Effective(_rewards));
            if (!saveToFile)
            {
                Clipboard.SetImage(image);
                return "Copied. Paste it into a chat with Ctrl+V.";
            }
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                FileName = ShareCard.SuggestedFileName(g, duel),
                Filter = "PNG image|*.png",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            };
            if (dialog.ShowDialog(this) != true) return null;
            ShareCard.SavePng(image, dialog.FileName);
            return $"Saved {System.IO.Path.GetFileName(dialog.FileName)}.";
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return $"Couldn't share the image: {e.Message}";
        }
    }

    /// <summary>You vs your lane opponent in the current game; null hides the card.</summary>
    public void ShowMatchup(LaneMatchup? matchup)
    {
        MatchupHost.Visibility = matchup is null ? Visibility.Collapsed : Visibility.Visible;
        MatchupHost.Content = matchup is null ? null : _historyView.MatchupView(matchup, live: true);
    }

    private void Components_Changed(object sender, RoutedEventArgs e) => Graph.ShowComponents = ComponentsBox.IsChecked == true;

    private void RefreshHotkeys()
    {
        ToggleKeyText.Text = _settings.ToggleOverlay.ToString();
        MoveKeyText.Text = _settings.MoveOverlay.ToString();
        QuitKeyText.Text = _settings.Quit.ToString();
    }

    private void RefreshStatus()
    {

        (string text, string detail, string brush) = (_overlay.MoveMode, _overlay.InGame, _overlay.UserHidden) switch
        {
            (true, _, _) => ("Move mode", $"Drag the overlay where you want it, then press {_settings.MoveOverlay} again to lock it.", "AccentBrush"),
            (_, true, false) => ("In game · overlay showing", $"Press {_settings.ToggleOverlay} to hide it.", "OkBrush"),
            (_, true, true) => ("In game · overlay hidden", $"Press {_settings.ToggleOverlay} to show it again.", "AccentBrush"),
            _ => ("Waiting for a game", "The overlay appears on its own once you're past the loading screen.", "MutedBrush"),
        };
        StatusText.Text = text;
        StatusDetail.Text = detail;
        StatusDot.Fill = (Brush)FindResource(brush);
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        // Release the hotkeys while the dialog captures new ones, otherwise pressing them would trigger the old action.
        _overlay.SuspendHotkeys();
        var dialog = new SettingsWindow(_settings, preview: (bg, all) => _overlay.ApplyAppearance(bg, all), ExportBuilds, ImportBuilds) { Owner = this };
        _overlay.SetPreview(true);
        bool saved;
        try { saved = dialog.ShowDialog() == true; }
        finally { _overlay.SetPreview(false); } // back to the saved values (new ones if saved)
        ShowHotkeyWarning(_overlay.ApplyHotkeys());
        if (saved && dialog.BuildSettingsChanged) BuildSettingsChanged?.Invoke();
        if (saved && dialog.RivalChanged) OnRivalChanged();
        if (saved) ReplacePageBox.IsChecked = _settings.RunesReplaceCurrent;
        if (saved && dialog.SurprisePicksChanged) PickRerollRequested?.Invoke();
        RefreshHotkeys();
        RefreshStatus();
    }


}
