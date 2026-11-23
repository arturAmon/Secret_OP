using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using LeagueOverlay.Core.Analysis;
using LeagueOverlay.Core.History;

namespace LeagueOverlay;

/// <summary>
/// Fills the History tab: one list entry per saved game, and for the selected game the result, your stats and items,
/// gold and lanes at the end, your build next to what the overlay suggested, the scoreboard and the gold graph.
/// Built in code because rows are few and icons arrive asynchronously.
/// </summary>
internal sealed partial class HistoryView
{
    private static readonly Brush Text = Brush(0xE6, 0xE8, 0xEC);
    private static readonly Brush Muted = Brush(0x8B, 0x93, 0xA3);
    private static readonly Brush Green = Brush(0x4C, 0xAF, 0x7A);
    private static readonly Brush Red = Brush(0xE0, 0x5D, 0x5D);
    private static readonly Brush Accent = Brush(0xC8, 0x9B, 0x3C);
    private static readonly Brush Card = Brush(0x1F, 0x23, 0x2C);
    private static readonly Brush Line = Brush(0x2A, 0x2F, 0x3A);
    private static readonly Brush IconBg = Brush(0x2F, 0x35, 0x42);
    private static readonly Brush MyTeam = new SolidColorBrush(GoldGraph.MyTeamColor);
    private static readonly Brush Enemy = new SolidColorBrush(GoldGraph.EnemyColor);

    private readonly IconCache _icons;
    private readonly Func<int, string?> _itemName;
    private readonly Func<LeagueOverlay.Core.Data.ItemCatalog> _catalog;
    private readonly Func<string?> _rival;
    private readonly Action<string> _setRival;
    private readonly Func<GameRecord, RivalDuel?, bool, string?> _share; // (game, duel, save to file? else copy) -> message
    private readonly List<(Image Image, Func<ImageSource?> Get)> _pendingIcons = new();

    public HistoryView(IconCache icons, Func<int, string?> itemName, Func<LeagueOverlay.Core.Data.ItemCatalog> catalog, Func<string?> rival, Action<string> setRival, Func<GameRecord, RivalDuel?, bool, string?> share)
    {
        _icons = icons;
        _itemName = itemName;
        _catalog = catalog;
        _rival = rival;
        _setRival = setRival;
        _share = share;
        _icons.IconLoaded += FillPendingIcons;
    }

    // ---- list ----

    public FrameworkElement ListEntry(GameRecord g)
    {
        var me = g.Me;
        var title = new TextBlock { FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        title.Inlines.Add(new Run(g.ChampionName));
        if (RoleName(g.Position) is { } role) title.Inlines.Add(new Run($" · {role}") { Foreground = Muted, FontWeight = FontWeights.Normal });

        var sub = new TextBlock { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
        sub.Inlines.Add(new Run(ResultText(g.Result)) { Foreground = ResultBrush(g.Result), FontWeight = FontWeights.SemiBold });
        sub.Inlines.Add(new Run($"  {Clock(g.Duration)}" + (me is null ? "" : $"  {me.Kills}/{me.Deaths}/{me.Assists}")) { Foreground = Muted });

        var when = new TextBlock { FontSize = 11, Foreground = Muted, Text = When(g.EndedAt), TextTrimming = TextTrimming.CharacterEllipsis };
        if (Awards.For(g).FirstOrDefault() is { } best) when.Text += $"  ·  {best.Title}";
        if (g.Imported) when.ToolTip = "Fetched from Riot's match history (played without the app running)";
        var text = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Children = { title, sub, when } };

        var stripe = new Border { Width = 3, CornerRadius = new CornerRadius(1.5), Background = ResultBrush(g.Result), Margin = new Thickness(0, 2, 8, 2) };
        var panel = new DockPanel();
        DockPanel.SetDock(stripe, Dock.Left);
        panel.Children.Add(stripe);
        var icon = Icon(() => _icons.Champion(g.ChampionKey), 38);
        DockPanel.SetDock(icon, Dock.Left);
        panel.Children.Add(icon);
        panel.Children.Add(text);
        return panel;
    }

    // ---- detail ----

    public void ShowDetail(Panel host, GameRecord g)
    {
        host.Children.Clear();
        var me = g.Me;
        var duel = RivalDuel.Compare(g.Players, _rival(), g.Duration / 60, g.PostGame);

        // Header: champion, result, length, when, mode; share buttons on the right.
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
        var copy = new Button { Content = "Copy image", Style = (Style)Application.Current.FindResource("AccentButton"), Margin = new Thickness(8, 0, 0, 0),
                                ToolTip = "Copies a picture of this game to the clipboard, ready to paste into a chat" };
        var save = new Button { Content = "Save image…", Margin = new Thickness(8, 0, 0, 0) };
        var feedback = new TextBlock { FontSize = 11, Foreground = Green, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, 0, 0) };
        copy.Click += (_, _) => feedback.Text = _share(g, duel, false) ?? "";
        save.Click += (_, _) => feedback.Text = _share(g, duel, true) ?? "";
        var shareButtons = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children = { new StackPanel { Orientation = Orientation.Horizontal, Children = { save, copy } }, feedback },
        };
        DockPanel.SetDock(shareButtons, Dock.Right);
        header.Children.Add(shareButtons);
        var bigIcon = Icon(() => _icons.Champion(g.ChampionKey), 52);
        DockPanel.SetDock(bigIcon, Dock.Left);
        header.Children.Add(bigIcon);
        var title = new TextBlock { FontSize = 20, FontWeight = FontWeights.Bold };
        title.Inlines.Add(new Run(g.ChampionName));
        if (RoleName(g.Position) is { } role) title.Inlines.Add(new Run($"  {role}") { FontSize = 14, Foreground = Muted, FontWeight = FontWeights.Normal });
        var line2 = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap };
        line2.Inlines.Add(new Run(ResultText(g.Result)) { Foreground = ResultBrush(g.Result), FontWeight = FontWeights.SemiBold });
        if (g.PostGame is null && g.ResultInferred) line2.Inlines.Add(new Run(" (from the nexus turrets)") { Foreground = Muted, FontSize = 11 });
        line2.Inlines.Add(new Run($"  ·  {Clock(g.Duration)}  ·  {g.EndedAt:ddd d MMM, HH:mm}  ·  {ModeName(g.GameMode)}") { Foreground = Muted });
        if (g.Imported) line2.Inlines.Add(new Run("  ·  from match history") { Foreground = Muted, FontSize = 11 });
        header.Children.Add(new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Children = { title, line2 } });
        host.Children.Add(header);

        // Titles: something that went well, always at least one.
        var awards = Awards.For(g);
        host.Children.Add(CardOf(TitlesView(awards)));

        // The rival duel, when your rival played this game.
        if (duel is not null) host.Children.Add(DuelView(duel, live: false));

        // Your stats + final items.
        if (me is not null)
        {
            double minutes = Math.Max(1, g.Duration / 60);
            var stats = new WrapPanel();
            stats.Children.Add(Stat("KDA", $"{me.Kills} / {me.Deaths} / {me.Assists}"));
            stats.Children.Add(Stat("CS", $"{me.CreepScore}  ({me.CreepScore / minutes:0.0}/min)"));
            stats.Children.Add(Stat("Level", me.Level.ToString()));
            stats.Children.Add(Stat("Item gold", $"{me.ItemGold:N0}"));
            stats.Children.Add(new StackPanel { Children = { Caption("Final items"), SlotRow(me, 30) } });
            host.Children.Add(CardOf(stats));
        }

        // You vs your lane opponent.
        if (LaneMatchup.Find(g.Players, g.Duration / 60) is { } matchup) host.Children.Add(MatchupView(matchup, live: false));

        // Everything Riot's match history adds after the game.
        if (g.PostGame is { } post && me is not null && post.Find(me.Key) is { } pm)
        {
            host.Children.Add(Heading("Match report"));
            host.Children.Add(MatchReport(g, post, pm));
        }

        // Gold and lanes when it ended.
        host.Children.Add(Heading("When the game ended"));
        int lead = g.EnemyTeamGold - g.MyTeamGold;
        var gold = new TextBlock { FontSize = 16, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 8) };
        gold.Inlines.Add(new Run(lead > 0 ? $"Enemy +{lead:N0} g" : lead < 0 ? $"Your team +{-lead:N0} g" : "Gold even") { Foreground = lead > 0 ? Red : lead < 0 ? Green : Text });
        gold.Inlines.Add(new Run($"   {(g.Imported ? "gold earned" : "items")} {g.MyTeamGold:N0} vs {g.EnemyTeamGold:N0}") { FontSize = 12, FontWeight = FontWeights.Normal, Foreground = Muted });
        var lanes = new UniformGrid { Rows = 1, Columns = Math.Max(1, g.Lanes.Count) };
        var biggest = g.Lanes.Count == 0 ? null : g.Lanes.MaxBy(l => Math.Abs(l.Score));
        foreach (var l in g.Lanes) lanes.Children.Add(LaneCell(l, l == biggest && l.Score != 0, g.Position));
        host.Children.Add(CardOf(new StackPanel { Children = { gold, lanes } }));

        // Build vs suggestions (imported games: just the build, the app wasn't there to suggest anything).
        host.Children.Add(Heading(g.Imported ? "Your build" : "Your build vs. the suggestions"));
        host.Children.Add(CardOf(BuildComparison(g)));

        // Scoreboard.
        host.Children.Add(Heading("Scoreboard"));
        host.Children.Add(CardOf(Scoreboard(g)));

        // Gold graph + purchase / level timeline, the same view as during the game.
        host.Children.Add(Heading(g.Imported ? "Gold difference over time (gold earned, from match history)" : "Gold difference over time"));
        var graph = new GoldGraph { Icons = _icons, History = g.Timeline, Height = 300 + 36 + g.Timeline.Players.Count * 34 };
        _icons.IconLoaded += graph.InvalidateVisual;
        graph.Unloaded += (_, _) => _icons.IconLoaded -= graph.InvalidateVisual;
        host.Children.Add(CardOf(graph));
    }

    private FrameworkElement TitlesView(List<Award> awards)
    {
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var score = new TextBlock { FontSize = 13, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center };
        score.Inlines.Add(new Run("Score "));
        score.Inlines.Add(new Run($"{Awards.Score(awards):N0}") { Foreground = Accent, FontWeight = FontWeights.Bold, FontSize = 18 });
        DockPanel.SetDock(score, Dock.Right);
        header.Children.Add(score);
        header.Children.Add(new TextBlock { Text = awards.Count == 1 ? "Title earned" : $"{awards.Count} titles earned", FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });

        var badges = new WrapPanel();
        foreach (var a in awards)
        {
            badges.Children.Add(new Border
            {
                BorderBrush = Accent, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Color.FromArgb(0x22, 0xC8, 0x9B, 0x3C)),
                Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 8, 8),
                Child = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = a.Title, FontWeight = FontWeights.Bold, Foreground = Brush(0xF0, 0xC8, 0x70) },
                        new TextBlock { Text = a.Reason, FontSize = 11, Foreground = Muted },
                    },
                },
            });
        }
        return new StackPanel { Children = { header, badges } };
    }

    /// <summary>You vs your lane opponent: gold, CS per minute, KDA, level and vision, as differences.</summary>
    public FrameworkElement MatchupView(LaneMatchup m, bool live)
    {
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var mine = Icon(() => _icons.Champion(m.Me.ChampionKey), 26);
        var theirs = Icon(() => _icons.Champion(m.Opponent.ChampionKey), 26);
        DockPanel.SetDock(mine, Dock.Left);
        header.Children.Add(mine);
        var vs = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
        vs.Inlines.Add(new Run(live ? "You vs your lane opponent " : "You vs your lane opponent at the end: "));
        vs.Inlines.Add(new Run($"{m.Opponent.Name}") { Foreground = Enemy });
        DockPanel.SetDock(vs, Dock.Left);
        header.Children.Add(vs);
        DockPanel.SetDock(theirs, Dock.Left);
        header.Children.Add(theirs);
        header.Children.Add(new TextBlock());

        var cells = new UniformGrid { Rows = 1, Columns = 5 };
        cells.Children.Add(Metric("Item gold", Signed(m.GoldDiff, "N0"), m.GoldDiff, $"{m.Me.ItemGold / 1000.0:0.0}k vs {m.Opponent.ItemGold / 1000.0:0.0}k"));
        cells.Children.Add(Metric("CS per min", Signed(m.CsPerMinDiff, "0.0"), m.CsPerMinDiff, $"{m.CsPerMin(m.Me):0.0} vs {m.CsPerMin(m.Opponent):0.0}"));
        cells.Children.Add(Metric("KDA", Signed(m.KdaDiff, "0.0"), m.KdaDiff,
            $"{m.Me.Kills}/{m.Me.Deaths}/{m.Me.Assists} vs {m.Opponent.Kills}/{m.Opponent.Deaths}/{m.Opponent.Assists}"));
        cells.Children.Add(Metric("Level", Signed(m.LevelDiff, "0"), m.LevelDiff, $"{m.Me.Level} vs {m.Opponent.Level}"));
        cells.Children.Add(Metric("Vision", Signed(m.VisionDiff, "0"), m.VisionDiff, $"{m.Me.VisionScore:0} vs {m.Opponent.VisionScore:0}"));
        return CardOf(new StackPanel { Children = { header, cells } });
    }

    private static FrameworkElement Metric(string label, string value, double sign, string detail) => new Border
    {
        Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(4, 5, 4, 5), CornerRadius = new CornerRadius(5),
        BorderThickness = new Thickness(1), BorderBrush = Line,
        Child = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = label, FontSize = 10, Foreground = Muted, HorizontalAlignment = HorizontalAlignment.Center },
                new TextBlock { Text = value, FontSize = 16, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center,
                                Foreground = sign > 0.05 ? Green : sign < -0.05 ? Red : Text },
                new TextBlock { Text = detail, FontSize = 10, Foreground = Muted, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis },
            },
        },
    };

    private static string Signed(double v, string format) => (v > 0 ? "+" : "") + v.ToString(format);

    private FrameworkElement BuildComparison(GameRecord g)
    {
        var panel = new StackPanel();
        if (g.Build.Count == 0)
        {
            panel.Children.Add(new TextBlock { Foreground = Muted, TextWrapping = TextWrapping.Wrap, Text = "No finished items were bought while the app was watching." });
            return panel;
        }

        if (g.Imported) return ImportedBuild(g);
        int different = g.Build.Count(s => s.Match == StepMatch.Different);
        var summary = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
        summary.Inlines.Add(new Run(g.Suggested == 0
            ? "The overlay had no suggestions during this game."
            : different == 0 ? $"You followed every suggestion ({g.Followed} of {g.Suggested})."
            : $"You followed {g.Followed} of {g.Suggested} suggestions. Where you went another way, the suggestion is shown next to your item.")
        { Foreground = different == 0 ? Green : Text });
        panel.Children.Add(summary);

        foreach (var step in g.Build)
        {
            var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.3, GridUnitType.Star) });

            var time = new TextBlock { Text = Clock(step.Time), Foreground = Muted, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            var bought = new StackPanel { Orientation = Orientation.Horizontal };
            bought.Children.Add(ItemIcon(step.ItemId, step.ItemName, 26));
            bought.Children.Add(new TextBlock { Text = step.ItemName, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });

            FrameworkElement verdict = step.Match switch
            {
                StepMatch.Followed => Badge("✓ as suggested", Green),
                StepMatch.Situational => Badge("✓ situational pick", Green),
                StepMatch.NoSuggestion => Badge("no suggestion at the time", Muted),
                _ => Suggestion(step),
            };
            Grid.SetColumn(bought, 1);
            Grid.SetColumn(verdict, 2);
            row.Children.Add(time);
            row.Children.Add(bought);
            row.Children.Add(verdict);
            panel.Children.Add(row);
        }

        // The full popular path, when the game had build data and you left it.
        if (g.PopularPath.Count > 0)
        {
            var mine = g.Build.Select(s => s.ItemId).ToList();
            bool samePath = g.PopularPath.Take(mine.Count).SequenceEqual(mine.Take(g.PopularPath.Count));
            if (!samePath)
            {
                panel.Children.Add(new Border { Height = 1, Background = Line, Margin = new Thickness(0, 10, 0, 10) });
                panel.Children.Add(PathRow($"Most common build ({g.PopularGames:N0} games)", g.PopularPath, mine));
                panel.Children.Add(PathRow("Your build", mine, g.PopularPath));
            }
        }
        return panel;
    }

    /// <summary>Finished items in the order you bought them; no comparison, since there were no live suggestions.</summary>
    private FrameworkElement ImportedBuild(GameRecord g)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Foreground = Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
            Text = "Played without the app running, so there were no suggestions to compare. Your finished items, in the order you bought them:",
        });
        var row = new WrapPanel();
        foreach (var step in g.Build)
        {
            row.Children.Add(new StackPanel
            {
                Margin = new Thickness(0, 0, 10, 6),
                Children =
                {
                    ItemIcon(step.ItemId, step.ItemName, 32),
                    new TextBlock { Text = Clock(step.Time), FontSize = 10, Foreground = Muted, HorizontalAlignment = HorizontalAlignment.Center },
                },
            });
        }
        panel.Children.Add(row);
        return panel;
    }

    private FrameworkElement Suggestion(BuildStep step)
    {
        var p = new DockPanel(); // last child (the name) takes the remaining width, so long reasons wrap
        var label = new TextBlock { Text = "suggested", Foreground = Accent, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        DockPanel.SetDock(label, Dock.Left);
        p.Children.Add(label);
        if (step.SuggestedId is { } id)
        {
            var icon = ItemIcon(id, step.SuggestedName, 26);
            DockPanel.SetDock(icon, Dock.Left);
            p.Children.Add(icon);
        }
        var name = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), TextWrapping = TextWrapping.Wrap };
        name.Inlines.Add(new Run(step.SuggestedName ?? "—"));
        if (step.SuggestedReason is { Length: > 0 } reason) name.Inlines.Add(new Run($"\n{reason}") { FontSize = 11, Foreground = Muted });
        p.Children.Add(name);
        return p;
    }

    /// <summary>A row of item icons; items that the other path doesn't have are dimmed.</summary>
    private FrameworkElement PathRow(string label, IReadOnlyList<int> path, IReadOnlyList<int> other)
    {
        var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
        var caption = new TextBlock { Text = label, Width = 200, Foreground = Muted, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(caption, Dock.Left);
        row.Children.Add(caption);
        var icons = new WrapPanel();
        for (int i = 0; i < path.Count; i++)
        {
            if (i > 0) icons.Children.Add(new TextBlock { Text = "›", Foreground = Muted, Margin = new Thickness(5, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
            var icon = ItemIcon(path[i], null, 28);
            if (!other.Contains(path[i])) icon.Opacity = 0.45;
            icons.Children.Add(icon);
        }
        row.Children.Add(icons);
        return row;
    }

    private FrameworkElement Scoreboard(GameRecord g)
    {
        var panel = new StackPanel();
        foreach (var team in new[] { true, false })
        {
            panel.Children.Add(new TextBlock
            {
                Text = team ? "YOUR TEAM" : "ENEMY TEAM", FontSize = 10, FontWeight = FontWeights.Bold,
                Foreground = team ? MyTeam : Enemy, Margin = new Thickness(0, team ? 0 : 10, 0, 4),
            });
            foreach (var p in g.Players.Where(p => p.MyTeam == team))
            {
                var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                foreach (var w in new[] { 24.0, 30, 96, 26, 62, 52, 44 }) row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var cells = new FrameworkElement[]
                {
                    RivalStar(p),
                    Icon(() => _icons.Champion(p.ChampionKey), 24),
                    new TextBlock { Text = p.IsMe ? $"{p.Name} (you)" : p.Name, Foreground = p.IsMe ? Accent : Text, FontWeight = p.IsMe ? FontWeights.SemiBold : FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis },
                    new TextBlock { Text = $"{p.Level}", Foreground = Muted, ToolTip = "Level" },
                    new TextBlock { Text = $"{p.Kills}/{p.Deaths}/{p.Assists}", ToolTip = "Kills / deaths / assists" },
                    new TextBlock { Text = $"{p.CreepScore} cs", Foreground = Muted },
                    new TextBlock { Text = $"{p.ItemGold / 1000.0:0.0}k", Foreground = Muted, ToolTip = "Gold spent on items" },
                    SlotRow(p, 20),
                };
                for (int i = 0; i < cells.Length; i++)
                {
                    cells[i].VerticalAlignment = VerticalAlignment.Center;
                    Grid.SetColumn(cells[i], i);
                    row.Children.Add(cells[i]);
                }
                panel.Children.Add(row);
            }
        }
        return panel;
    }

    /// <summary>☆ to make this player your rival (★ = already is). Nothing for yourself.</summary>
    private FrameworkElement RivalStar(RecordedPlayer p)
    {
        if (p.IsMe) return new TextBlock();
        bool isRival = RiotIds.Same(p.Key, _rival());
        var star = new TextBlock
        {
            Text = isRival ? "★" : "☆", FontSize = 15, Foreground = isRival ? Accent : Muted, Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = isRival ? $"{RiotIds.GameName(p.Key)} is your rival" : $"Make {RiotIds.GameName(p.Key)} your rival",
        };
        star.MouseLeftButtonUp += (_, _) => _setRival(p.Key);
        return star;
    }

    /// <summary>You vs your rival, stat by stat; the better value in each row is highlighted.</summary>
    public FrameworkElement DuelView(RivalDuel d, bool live)
    {
        string rivalName = RiotIds.GameName(d.Rival.Key);
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var tally = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 13, Foreground = Muted };
        tally.Inlines.Add(new Run(live ? "so far  " : "stats won  "));
        tally.Inlines.Add(new Run($"{d.MyWins} – {d.RivalWins}") { FontSize = 18, FontWeight = FontWeights.Bold, Foreground = d.MyWins > d.RivalWins ? Green : d.MyWins < d.RivalWins ? Red : Text });
        DockPanel.SetDock(tally, Dock.Right);
        header.Children.Add(tally);
        var title = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        title.Inlines.Add(new Run("Rival duel: you vs "));
        title.Inlines.Add(new Run(rivalName) { Foreground = Accent });
        title.Inlines.Add(new Run($" ({d.Rival.Name}{(d.SameTeam ? ", same team" : ", enemy team")})") { Foreground = Muted, FontWeight = FontWeights.Normal, FontSize = 12 });
        header.Children.Add(title);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition());
        void Put(UIElement e, int r, int c) { Grid.SetRow(e, r); Grid.SetColumn(e, c); grid.Children.Add(e); }

        var meHead = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        meHead.Children.Add(Icon(() => _icons.Champion(d.Me.ChampionKey), 24));
        meHead.Children.Add(new TextBlock { Text = "You", Foreground = MyTeam, FontWeight = FontWeights.Bold, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        Put(meHead, 0, 0);
        var themHead = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 0, 6) };
        themHead.Children.Add(new TextBlock { Text = rivalName, Foreground = Accent, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        themHead.Children.Add(Icon(() => _icons.Champion(d.Rival.ChampionKey), 24));
        Put(themHead, 0, 2);

        for (int i = 0; i < d.Stats.Count; i++)
        {
            var s = d.Stats[i];
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) });
            int r = i + 1;
            if (i % 2 == 0)
            {
                var stripe = new Border { Background = Brush(0x26, 0x2B, 0x35), CornerRadius = new CornerRadius(4) };
                Grid.SetColumnSpan(stripe, 3);
                Put(stripe, r, 0);
            }
            TextBlock V(double? v, bool win, HorizontalAlignment side) => new()
            {
                Text = s.Show(v), FontWeight = win ? FontWeights.Bold : FontWeights.Normal, Foreground = win ? Green : s.Winner == 0 ? Text : Muted,
                HorizontalAlignment = side, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0),
            };
            Put(V(s.Mine, s.Winner > 0, HorizontalAlignment.Left), r, 0);
            Put(new TextBlock { Text = s.Label, FontSize = 12, Foreground = Muted, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }, r, 1);
            Put(V(s.Theirs, s.Winner < 0, HorizontalAlignment.Right), r, 2);
        }

        var panel = new StackPanel { Children = { header, grid } };
        if (!d.HasMatchHistory)
            panel.Children.Add(new TextBlock
            {
                Margin = new Thickness(0, 8, 0, 0), FontSize = 11, Foreground = Muted, TextWrapping = TextWrapping.Wrap,
                Text = live
                    ? "Gold earned, damage, turret plates and wards are added from Riot's match history a few minutes after the game (needs your API key in Settings)."
                    : "Gold earned, damage, turret plates and wards come from Riot's match history; they're added a few minutes after the game when an API key is set in Settings.",
            });
        return CardOf(panel);
    }

    /// <summary>
    /// The same layout for every player: six item slots (empty ones drawn as empty squares, vision items left out)
    /// and, for ADCs, the role-quest boots slot after a small gap.
    /// </summary>
    private FrameworkElement SlotRow(RecordedPlayer p, double size)
    {
        var slots = ItemSlots.Of(p, _catalog());
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var id in slots.Regular) row.Children.Add(Slot(id, size));
        if (slots.Boots is { } boots)
        {
            var slot = Slot(boots, size);
            slot.Margin = new Thickness(size * 0.3, 0, 3, 0);
            if (boots == 0) slot.ToolTip = "Boots slot (unlocked by the bot lane role quest)";
            row.Children.Add(slot);
        }
        return row;
    }

    private FrameworkElement Slot(int id, double size)
    {
        if (id != 0) return ItemIcon(id, null, size);
        return new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 3, 0),
            Background = Brush(0x16, 0x19, 0x20), BorderBrush = Line, BorderThickness = new Thickness(1),
        };
    }

    private FrameworkElement LaneCell(RecordedLane l, bool highlight, string? myPosition)
    {
        string label = l.Lane switch { Lane.Top => "TOP", Lane.Jungle => "JG", Lane.Mid => "MID", _ => "BOT" };
        bool mine = LaneAnalyzer.LaneOf(new Core.Live.Player { Position = myPosition }) == l.Lane;
        return new Border
        {
            Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(4, 5, 4, 5), CornerRadius = new CornerRadius(5),
            BorderThickness = new Thickness(highlight ? 2 : 1), BorderBrush = highlight ? Accent : Line,
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = mine ? $"{label} • YOU" : label, FontSize = 10, Foreground = mine ? Text : Muted, HorizontalAlignment = HorizontalAlignment.Center },
                    new TextBlock { Text = Short(l.Score), FontSize = 15, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center,
                                    Foreground = l.Score > 0 ? Green : l.Score < 0 ? Red : Text },
                    new TextBlock { Text = $"{Short(l.GoldDiff)}g {(l.LevelDiff > 0 ? "+" : "")}{l.LevelDiff}lv", FontSize = 10, Foreground = Muted, HorizontalAlignment = HorizontalAlignment.Center },
                },
            },
        };
    }

    // ---- small pieces ----

    private FrameworkElement ItemIcon(int id, string? name, double size)
    {
        var icon = Icon(() => _icons.Item(id), size);
        icon.ToolTip = name ?? _itemName(id) ?? $"Item {id}";
        icon.Margin = new Thickness(0, 0, 3, 0);
        return icon;
    }

    private Border Icon(Func<ImageSource?> get, double size)
    {
        var image = new Image { Width = size, Height = size, Source = get() };
        if (image.Source is null) _pendingIcons.Add((image, get));
        return new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(4), Background = IconBg, ClipToBounds = true,
            Child = image,
        };
    }

    private void FillPendingIcons()
    {
        for (int i = _pendingIcons.Count - 1; i >= 0; i--)
        {
            var (image, get) = _pendingIcons[i];
            if (get() is { } source) { image.Source = source; _pendingIcons.RemoveAt(i); }
        }
    }

    private static FrameworkElement Stat(string caption, string value) => new StackPanel
    {
        Margin = new Thickness(0, 0, 24, 6),
        Children = { Caption(caption), new TextBlock { Text = value, FontSize = 15, FontWeight = FontWeights.SemiBold } },
    };

    private static TextBlock Caption(string text) => new() { Text = text, FontSize = 11, Foreground = Muted, Margin = new Thickness(0, 0, 0, 2) };

    private static TextBlock Heading(string text) => new() { Text = text, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 6) };

    private static Border CardOf(UIElement child) => new()
    {
        Background = Card, CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 0, 0, 14), Child = child,
    };

    private static Border Badge(string text, Brush brush) => new()
    {
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, Foreground = brush, FontSize = 12, FontWeight = FontWeights.SemiBold },
    };

    public static string ResultText(string result) => result switch { "Win" => "Victory", "Lose" => "Defeat", _ => "Result unknown" };
    private static Brush ResultBrush(string result) => result switch { "Win" => Green, "Lose" => Red, _ => Muted };

    private static string? RoleName(string? position) => position?.ToUpperInvariant() switch
    {
        "TOP" => "Top", "JUNGLE" => "Jungle", "MIDDLE" => "Mid", "BOTTOM" => "ADC", "UTILITY" => "Support", _ => null,
    };

    private static string ModeName(string mode) => mode switch
    {
        "CLASSIC" => "Summoner's Rift", "ARAM" => "ARAM", "PRACTICETOOL" => "Practice Tool", "CHERRY" => "Arena", "" => "Unknown mode", _ => mode,
    };

    private static string When(DateTime t) =>
        t.Date == DateTime.Today ? $"Today, {t:HH:mm}" : t.Date == DateTime.Today.AddDays(-1) ? $"Yesterday, {t:HH:mm}" : $"{t:ddd d MMM, HH:mm}";

    private static string Clock(double seconds) => $"{(int)seconds / 60}:{(int)seconds % 60:00}";

    private static string Short(int v)
    {
        string sign = v > 0 ? "+" : v < 0 ? "-" : "";
        int a = Math.Abs(v);
        return a >= 1000 ? $"{sign}{a / 1000.0:0.0}k" : $"{sign}{a}";
    }

    private static Brush Brush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
