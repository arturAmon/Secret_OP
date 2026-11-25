using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using LeagueOverlay.Core.History;

namespace LeagueOverlay;

/// <summary>The "Match report": what Riot's match history adds after the game.</summary>
internal sealed partial class HistoryView
{
    // Damage types: validated for colour-blind separation and contrast on the card colour (#1F232C); always shown with a legend.
    private static readonly Brush Physical = Brush(0xC0, 0x84, 0x1A), Magic = Brush(0x8B, 0x7B, 0xF0), TrueDmg = Brush(0x1E, 0x9E, 0x8E);

    private FrameworkElement MatchReport(GameRecord g, PostGameStats post, PostGamePlayer me)
    {
        var panel = new StackPanel();
        var opp = post.Opponent(me);
        // Display name ("Lee Sin") from the saved scoreboard; match history only has the key ("LeeSin").
        string oppName = opp is null ? "" : g.Players.FirstOrDefault(p => RiotIds.Same(p.Key, opp.RiotId))?.Name ?? opp.Champion;
        double minutes = Math.Max(1, g.Duration / 60);

        // Damage to champions, everyone.
        panel.Children.Add(Section("Damage to champions"));
        panel.Children.Add(DamageChart(g, post, me));

        // Combat.
        panel.Children.Add(Section("Combat"));
        var combat = new WrapPanel();
        combat.Children.Add(Tile("Damage to champions", $"{me.ChampionDamage:N0}", $"{me.TeamDamageShare:0%} of your team · {me.DamagePerMinute:0}/min"));
        combat.Children.Add(DamageTypes(me));
        combat.Children.Add(Tile("Damage taken", $"{me.DamageTaken:N0}", $"{me.DamageMitigated:N0} blocked or reduced"));
        combat.Children.Add(Tile("Healing & shielding allies", $"{me.HealAndShield:N0}", $"{me.HealsOnTeammates:N0} healed · {me.ShieldsOnTeammates:N0} shielded"));
        combat.Children.Add(Tile("Crowd control on enemies", $"{me.CcSeconds}s", ""));
        combat.Children.Add(Tile("Skillshots", $"{me.SkillshotsHit} hit", $"{me.SkillshotsDodged} dodged"));
        panel.Children.Add(combat);

        // Laning, against the player in your position.
        if (opp is not null)
        {
            panel.Children.Add(Section($"Laning vs {oppName}"));
            var lane = new WrapPanel();
            int? cs10 = me.CsAt(10) ?? me.CsAt10, oppCs10 = opp.CsAt(10) ?? opp.CsAt10;
            lane.Children.Add(DiffTile("CS at 10 min", cs10, oppCs10, "0"));
            lane.Children.Add(DiffTile("Gold at 10 min", me.GoldAt(10), opp.GoldAt(10), "#,0"));
            lane.Children.Add(DiffTile("Gold at 15 min", me.GoldAt(15), opp.GoldAt(15), "#,0"));
            if (me.MaxCsLead > 0) lane.Children.Add(Tile("Biggest CS lead", $"+{me.MaxCsLead}", "over your lane opponent"));
            lane.Children.Add(DiffTile("Gold per minute", (int)me.GoldPerMinute, (int)opp.GoldPerMinute, "0"));
            panel.Children.Add(lane);
            if (me.GoldByMinute is { Length: > 2 } mine && opp.GoldByMinute is { Length: > 2 } theirs)
            {
                var diffs = mine.Zip(theirs, (a, b) => a - b).ToArray();
                panel.Children.Add(new TextBlock { Text = $"Your gold minus {oppName}'s, minute by minute", FontSize = 11, Foreground = Muted, Margin = new Thickness(0, 4, 0, 4) });
                panel.Children.Add(new LaneGoldChart(diffs) { Height = 110, Margin = new Thickness(0, 0, 0, 4) });
            }
        }

        // Runes: what each one did, compared with usual, and what else could have done more.
        if (RunesReport(post, me, minutes, g.ChampionKey) is { } runes)
        {
            panel.Children.Add(Section("Runes"));
            panel.Children.Add(runes);
        }

        // Objectives.
        panel.Children.Add(Section("Objectives"));
        var objectives = new WrapPanel();
        objectives.Children.Add(Tile("Dragons", $"{me.DragonTakedowns}", "taken part in"));
        objectives.Children.Add(Tile("Barons", $"{me.BaronTakedowns}", "taken part in"));
        objectives.Children.Add(Tile("Herald & Voidgrubs", $"{me.HeraldTakedowns + me.VoidgrubKills}", $"{me.HeraldTakedowns} herald · {me.VoidgrubKills} grubs"));
        objectives.Children.Add(Tile("Turrets", $"{me.TurretTakedowns}", $"{me.TurretPlates} plates · {me.BuildingDamage:N0} damage"));
        if (me.ObjectivesStolen > 0) objectives.Children.Add(Tile("Stolen", $"{me.ObjectivesStolen}", "epic monsters"));
        panel.Children.Add(objectives);

        // Highlights.
        panel.Children.Add(Section("Highlights"));
        var fun = new WrapPanel();
        string multis = string.Join(" · ", new[] { (me.PentaKills, "penta"), (me.QuadraKills, "quadra"), (me.TripleKills, "triple"), (me.DoubleKills, "double") }
            .Where(x => x.Item1 > 0).Select(x => $"{x.Item1} {x.Item2}"));
        fun.Children.Add(Tile("Multikills", $"{me.Multikills}", multis.Length > 0 ? multis : "none this time"));
        fun.Children.Add(Tile("Largest killing spree", $"{me.LargestKillingSpree}", me.FirstBlood ? "got first blood" : ""));
        fun.Children.Add(Tile("Time spent dead", $"{me.TimeDeadSeconds / 60}:{me.TimeDeadSeconds % 60:00}", $"{me.TimeDeadSeconds / (minutes * 60):0%} of the game"));
        fun.Children.Add(Tile("Vision", $"{me.VisionScore}", $"{me.WardsPlaced} placed · {me.WardsKilled} destroyed · {me.ControlWards} control"));
        fun.Children.Add(Tile("Pings", $"{me.Pings}", me.DancedWithHerald ? "and a dance with the Herald" : ""));
        if (me.SavedAllies > 0) fun.Children.Add(Tile("Saves", $"{me.SavedAllies}", "allies saved from death"));
        panel.Children.Add(fun);

        var head = new TextBlock { FontSize = 11, Foreground = Muted, Margin = new Thickness(0, 0, 0, 4), Text = $"From Riot's match history ({post.MatchId})" };
        panel.Children.Insert(0, head);
        return CardOf(panel);
    }

    private RuneReport? AnalyseRunes(PostGameStats post, PostGamePlayer me, double minutes, string championKey)
    {
        if (Data?.Invoke() is not { Runes.IsLoaded: true } data) return null;
        int champ = data.Champion(championKey)?.Key ?? 0;
        return RuneAnalysis.Analyse(me, post, minutes, data.Runes, Benchmarks?.Invoke(), champ);
    }

    /// <summary>The surprise build a game was played with (items, runes) and how it went.</summary>
    private FrameworkElement SurpriseCard(GameRecord g)
    {
        var plan = g.Surprise!;
        var data = Data?.Invoke();
        var me = g.Me is { } m ? g.PostGame?.Find(m.Key) : null;
        var runes = me is not null && g.PostGame is { } post ? AnalyseRunes(post, me, Math.Max(1, g.Duration / 60), g.ChampionKey) : null;
        var review = SurpriseReviewer.Review(g, me, runes, data?.Runes ?? LeagueOverlay.Core.Data.RuneCatalog.Empty);

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "SURPRISE BUILD", FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Theme.PinkBrush });
        panel.Children.Add(new TextBlock { Text = review?.Headline ?? plan.Title, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 6) });

        var items = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        foreach (int id in plan.Path.Append(plan.Boots).Where(id => id > 0))
        {
            var icon = Icon(() => _icons.Item(id), 28);
            icon.Margin = new Thickness(0, 0, 4, 0);
            icon.ToolTip = _itemName(id);
            items.Children.Add(icon);
        }
        panel.Children.Add(items);
        if (plan.RunePage is { } page && data is { Runes.IsLoaded: true })
            panel.Children.Add(RunePanel.Page(page, data.Runes, _icons, Muted, showNames: false));
        foreach (var line in review?.Lines ?? [])
            panel.Children.Add(new TextBlock { Text = line, FontSize = 12, Foreground = Text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
        if (BlockSurprise is not null)
        {
            bool blocked = IsSurpriseBlocked?.Invoke(g.ChampionKey, plan.Theme) == true;
            var block = new Button
            {
                Content = blocked ? "Won't be suggested again" : "Not fun? Don't suggest this again", IsEnabled = !blocked,
                HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0),
                ToolTip = $"Never suggest {plan.Title} again",
            };
            block.Click += (_, _) => BlockSurprise(g.ChampionKey, plan.Theme);
            panel.Children.Add(block);
        }
        var card = CardOf(panel);
        card.BorderBrush = Theme.Tint(Theme.Pink, 0x80);
        card.BorderThickness = new Thickness(1);
        return card;
    }

    private FrameworkElement? RunesReport(PostGameStats post, PostGamePlayer me, double minutes, string championKey)
    {
        if (Data?.Invoke() is not { Runes.IsLoaded: true } data) return null;
        if (AnalyseRunes(post, me, minutes, championKey) is not { } report) return null;

        var panel = new StackPanel();
        panel.Children.Add(RunePanel.Page(report.Page, data.Runes, _icons, Muted, showNames: false));
        foreach (var line in report.Lines)
        {
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var title = new TextBlock { TextWrapping = TextWrapping.Wrap };
            title.Inlines.Add(new Run(line.Name) { FontWeight = FontWeights.SemiBold });
            if (line.Stats.Count == 0) title.Inlines.Add(new Run("   no numbers reported for this rune") { Foreground = Muted, FontSize = 11 });
            foreach (var st in line.Stats) title.Inlines.Add(new Run($"   {st.Label}: {st.Value}") { Foreground = Muted, FontSize = 12 });
            text.Children.Add(title);
            if (line.Compare is { } cmp)
                text.Children.Add(new TextBlock
                {
                    Text = cmp, FontSize = 11, TextWrapping = TextWrapping.Wrap,
                    Foreground = line.Ratio >= RuneAnalysis.ClearGap ? Green : line.Ratio < 0.8 ? Red : Muted,
                });
            if (line.Suggestion is { } sug)
            {
                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
                if (line.SuggestedRune is { } alt)
                {
                    var icon = RunePanel.Icon(data.Runes, _icons, alt, 18);
                    icon.Margin = new Thickness(0, 0, 6, 0);
                    DockPanel.SetDock(icon, Dock.Left);
                    row.Children.Add(icon);
                }
                row.Children.Add(new TextBlock { Text = sug, FontSize = 11, Foreground = Accent, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
                text.Children.Add(row);
            }
            var rowPanel = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            var runeIcon = RunePanel.Icon(data.Runes, _icons, line.RuneId, 26);
            runeIcon.Margin = new Thickness(0, 0, 10, 0);
            runeIcon.VerticalAlignment = VerticalAlignment.Top;
            DockPanel.SetDock(runeIcon, Dock.Left);
            rowPanel.Children.Add(runeIcon);
            rowPanel.Children.Add(text);
            panel.Children.Add(rowPanel);
        }
        foreach (var note in report.Notes)
            panel.Children.Add(new TextBlock { Text = note, FontSize = 12, Foreground = Accent, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) });
        panel.Children.Add(new TextBlock
        {
            FontSize = 11, Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0),
            Text = (report.HasBenchmarks ? "Typical values come from the high-ranked games collected for popular builds. " :
                   "Collect high-ranked games (Riot API key) or import a builds file to compare with what each rune usually does. ") +
                   "Match history only gives each rune's own end-of-game numbers, so activations are only counted for runes that report them.",
        });
        return panel;
    }

    /// <summary>Horizontal bars, one per player, your team first; bars in team colours, value at the end, you highlighted.</summary>
    private FrameworkElement DamageChart(GameRecord g, PostGameStats post, PostGamePlayer me)
    {
        var rows = new StackPanel();
        int max = Math.Max(1, post.Players.Max(p => p.ChampionDamage));
        var ordered = post.Players.OrderBy(p => p.TeamId != me.TeamId).ThenByDescending(p => p.ChampionDamage);
        foreach (var p in ordered)
        {
            bool isMe = ReferenceEquals(p, me);
            bool ally = p.TeamId == me.TeamId;
            var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var icon = Icon(() => _icons.Champion(p.Champion), 20);
            var name = new TextBlock
            {
                Text = isMe ? "You" : RiotIds.GameName(p.RiotId), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
                Foreground = isMe ? YouBrush : Text, FontWeight = isMe ? FontWeights.SemiBold : FontWeights.Normal, ToolTip = p.Champion,
            };
            // Bar and value share a star column; the bar's width is a share of it.
            var barArea = new Grid();
            barArea.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(p.ChampionDamage, GridUnitType.Star) });
            barArea.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0, max - p.ChampionDamage) + max * 0.18, GridUnitType.Star) });
            var bar = new Border { Height = 12, CornerRadius = new CornerRadius(0, 4, 4, 0), Background = ally ? MyTeam : Enemy, VerticalAlignment = VerticalAlignment.Center, Opacity = isMe ? 1 : 0.75 };
            var value = new TextBlock { Text = $"{p.ChampionDamage / 1000.0:0.0}k", FontSize = 11, Foreground = isMe ? Text : Muted, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(value, 1);
            barArea.Children.Add(bar);
            barArea.Children.Add(value);
            Grid.SetColumn(name, 1);
            Grid.SetColumn(barArea, 2);
            icon.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(icon);
            row.Children.Add(name);
            row.Children.Add(barArea);
            rows.Children.Add(row);
        }
        return rows;
    }

    /// <summary>Your champion damage split into physical / magic / true, as one stacked bar with a legend.</summary>
    private FrameworkElement DamageTypes(PostGamePlayer me)
    {
        double total = Math.Max(1, me.PhysicalDamage + me.MagicDamage + me.TrueDamage);
        var bar = new Grid { Height = 10, Margin = new Thickness(0, 6, 0, 6) };
        var parts = new[] { (me.PhysicalDamage, Physical, "Physical"), (me.MagicDamage, Magic, "Magic"), (me.TrueDamage, TrueDmg, "True") };
        foreach (var (amount, brush, _) in parts)
        {
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(amount, GridUnitType.Star) });
            var segment = new Border { Background = brush, Margin = new Thickness(0, 0, 2, 0), CornerRadius = new CornerRadius(2) };
            Grid.SetColumn(segment, bar.ColumnDefinitions.Count - 1);
            bar.Children.Add(segment);
        }
        var legend = new WrapPanel();
        foreach (var (amount, brush, label) in parts)
        {
            legend.Children.Add(new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(2), Background = brush, Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
            legend.Children.Add(new TextBlock { Text = $"{label} {amount / total:0%}", FontSize = 10, Foreground = Muted, Margin = new Thickness(0, 0, 10, 0) });
        }
        return TileFrame(new StackPanel { Children = { Caption("Damage types"), bar, legend } });
    }

    private static TextBlock Section(string text) => new()
    {
        Text = text.ToUpperInvariant(), FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Muted, Margin = new Thickness(0, 12, 0, 6),
    };

    private static FrameworkElement Tile(string label, string value, string detail) => TileFrame(new StackPanel
    {
        Children =
        {
            Caption(label),
            new TextBlock { Text = value, FontSize = 17, FontWeight = FontWeights.Bold },
            new TextBlock { Text = detail, FontSize = 10, Foreground = Muted, TextWrapping = TextWrapping.Wrap },
        },
    });

    /// <summary>A tile comparing you with your lane opponent: the difference (green/red), and both values.</summary>
    private static FrameworkElement DiffTile(string label, int? mine, int? theirs, string format)
    {
        if (mine is not { } m || theirs is not { } t) return Tile(label, "—", "not in this game's timeline");
        int diff = m - t;
        var value = new TextBlock { FontSize = 17, FontWeight = FontWeights.Bold, Foreground = diff > 0 ? Green : diff < 0 ? Red : Text, Text = (diff > 0 ? "+" : "") + diff.ToString(format, CultureInfo.CurrentCulture) };
        return TileFrame(new StackPanel
        {
            Children = { Caption(label), value, new TextBlock { Text = $"{m.ToString(format)} vs {t.ToString(format)}", FontSize = 10, Foreground = Muted } },
        });
    }

    private static Border TileFrame(UIElement child) => new()
    {
        Width = 158, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(10, 7, 10, 7), CornerRadius = new CornerRadius(6),
        BorderBrush = Line, BorderThickness = new Thickness(1), Child = child,
    };
}

/// <summary>Small chart of a gold difference per minute: above zero shaded in your team's colour, below in the enemy's.</summary>
internal sealed class LaneGoldChart(int[] diffs) : FrameworkElement
{
    private static readonly Pen Axis = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x3A, 0x40, 0x4D)), 1));
    private static readonly Brush Muted = Freeze(new SolidColorBrush(Color.FromRgb(0x8B, 0x93, 0xA3)));
    private static readonly Brush Up = Freeze(new SolidColorBrush(Color.FromArgb(0x55, GoldGraph.MyTeamColor.R, GoldGraph.MyTeamColor.G, GoldGraph.MyTeamColor.B)));
    private static readonly Brush Down = Freeze(new SolidColorBrush(Color.FromArgb(0x55, GoldGraph.EnemyColor.R, GoldGraph.EnemyColor.G, GoldGraph.EnemyColor.B)));
    private static readonly Pen UpPen = Freeze(new Pen(new SolidColorBrush(GoldGraph.MyTeamColor), 2) { LineJoin = PenLineJoin.Round });
    private static readonly Pen DownPen = Freeze(new Pen(new SolidColorBrush(GoldGraph.EnemyColor), 2) { LineJoin = PenLineJoin.Round });

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight, left = 44, bottom = h - 16, top = 4;
        if (diffs.Length < 2 || w < left + 20) return;
        double max = Math.Max(500, diffs.Max(d => Math.Abs(d))) * 1.1;
        double zero = (top + bottom) / 2;
        double X(int i) => left + i * (w - left - 4) / (diffs.Length - 1);
        double Y(double v) => zero - v / max * (bottom - top) / 2;

        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var c = line.Open()) { c.BeginFigure(new Point(X(0), Y(diffs[0])), false, false); for (int i = 1; i < diffs.Length; i++) c.LineTo(new Point(X(i), Y(diffs[i])), true, true); }
        using (var c = area.Open())
        {
            c.BeginFigure(new Point(X(0), zero), true, true);
            for (int i = 0; i < diffs.Length; i++) c.LineTo(new Point(X(i), Y(diffs[i])), false, false);
            c.LineTo(new Point(X(diffs.Length - 1), zero), false, false);
        }
        line.Freeze(); area.Freeze();
        foreach (var (clip, fill, pen) in new[] { (new Rect(left, top - 2, w, zero - top + 2), Up, UpPen), (new Rect(left, zero, w, bottom - zero + 2), Down, DownPen) })
        {
            dc.PushClip(new RectangleGeometry(clip));
            dc.DrawGeometry(fill, null, area);
            dc.DrawGeometry(null, pen, line);
            dc.Pop();
        }
        dc.DrawLine(Axis, new Point(left, Math.Round(zero) + 0.5), new Point(w, Math.Round(zero) + 0.5));

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        FormattedText T(string s) => new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, Muted, dpi);
        double step = Math.Max(500, Math.Ceiling(max / 2 / 500) * 500);
        foreach (var v in new[] { step, 0, -step })
        {
            if (Math.Abs(v) > max) continue;
            var t = T(v > 0 ? $"+{v / 1000:0.#}k" : v < 0 ? $"-{-v / 1000:0.#}k" : "0");
            dc.DrawText(t, new Point(left - 6 - t.Width, Y(v) - t.Height / 2));
        }
        for (int m = 0; m < diffs.Length; m += 5)
        {
            var t = T($"{m}:00");
            dc.DrawText(t, new Point(X(m) - t.Width / 2, bottom + 2));
        }
    }

    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }
}
