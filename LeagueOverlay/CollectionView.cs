using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using LeagueOverlay.Core.History;

namespace LeagueOverlay;

/// <summary>
/// The Collection tab: how many surprise games you've played, the achievements (done and still to do) and the share
/// image styles they unlock, with a preview of your share image in the chosen style.
/// </summary>
internal sealed class CollectionView(IconCache icons)
{
    private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

    public void Show(Panel host, RewardProgress p, GameRecord? sample, Action<CardStyle> pick)
    {
        host.Children.Clear();

        // Surprise games played, and the next milestone.
        host.Children.Add(new TextBlock { Text = "Surprise games", FontSize = 15, FontWeight = FontWeights.SemiBold });
        var count = new TextBlock { Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
        count.Inlines.Add(new System.Windows.Documents.Run($"{p.SurpriseGames}") { FontSize = 30, FontWeight = FontWeights.Bold, Foreground = Res("Accent3Brush") });
        count.Inlines.Add(new System.Windows.Documents.Run($"  played  ·  {p.SurpriseWins} won") { FontSize = 14, Foreground = Res("MutedBrush") });
        host.Children.Add(count);
        if (Rewards.NextMilestone(p) is { } next)
        {
            host.Children.Add(new ProgressBar { Minimum = 0, Maximum = next.Target, Value = Math.Min(next.Target, next.Value(p)), Height = 6, Margin = new Thickness(0, 6, 0, 0), MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Left, Width = 420 });
            host.Children.Add(new TextBlock
            {
                Text = $"{next.Value(p)} / {next.Target} to {next.Title}: unlocks {Rewards.Unlocks(next)?.Name}", FontSize = 12, Foreground = Res("MutedBrush"), Margin = new Thickness(0, 4, 0, 0),
            });
        }
        host.Children.Add(new TextBlock
        {
            Text = $"A game counts when you play a surprise build (at least {Rewards.MinPlanItems} of its items) for {Rewards.MinMinutes:0}+ minutes. Turn surprise build mode on in the \"This game\" tab.",
            FontSize = 12, Foreground = Res("MutedBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
        });

        // Share image style: pick from what you've unlocked; a preview with your last game.
        host.Children.Add(new TextBlock { Text = "Share image style", FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 22, 0, 2) });
        host.Children.Add(new TextBlock { Text = "Used for every image you copy or save from History. Locked styles show how to get them.", FontSize = 12, Foreground = Res("MutedBrush"), TextWrapping = TextWrapping.Wrap });
        var style = Rewards.Effective(p);
        if (sample is not null)
        {
            try
            {
                host.Children.Add(new Border
                {
                    Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(6), ClipToBounds = true,
                    Child = new Image { Source = ShareCard.Render(sample, null, icons, style), Width = 480 },
                });
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException) { }
        }
        host.Children.Add(StyleRows(p, unlockedOnly: false, pick));

        // Achievements: done first, then the rest with their progress.
        host.Children.Add(new TextBlock { Text = "Achievements", FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 22, 0, 6) });
        foreach (var a in Rewards.Achievements.OrderBy(a => p.Unlocked.ContainsKey(a.Id) ? 0 : 1))
            host.Children.Add(AchievementRow(a, p));
    }

    private static FrameworkElement AchievementRow(Achievement a, RewardProgress p)
    {
        bool done = p.Unlocked.TryGetValue(a.Id, out var when);
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8), Opacity = done ? 1 : 0.6 };
        var mark = new Border
        {
            Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Top,
            Background = done ? Res("AccentBrush") : Res("Panel2"), BorderBrush = Res("LineBrush"), BorderThickness = new Thickness(done ? 0 : 1),
            Child = new TextBlock { Text = done ? "✓" : "🔒", FontSize = done ? 14 : 11, FontWeight = FontWeights.Bold, Foreground = done ? Res("Bg") : Res("MutedBrush"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        DockPanel.SetDock(mark, Dock.Left);
        row.Children.Add(mark);
        if (Rewards.Unlocks(a) is { } cosmetic)
        {
            var swatch = CardStyles.Swatch(cosmetic.Kind, cosmetic.Id);
            swatch.ToolTip = $"Unlocks: {cosmetic.Name} ({cosmetic.Kind.ToString().ToLowerInvariant()})";
            swatch.VerticalAlignment = VerticalAlignment.Top;
            DockPanel.SetDock(swatch, Dock.Right);
            row.Children.Add(swatch);
        }
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = a.Title, FontWeight = FontWeights.SemiBold });
        string detail = done ? $"{a.HowTo} · unlocked {when:d MMM yyyy}" : a.Target > 1 ? $"{a.HowTo} · {Math.Min(a.Value(p), a.Target)} / {a.Target}" : a.HowTo;
        text.Children.Add(new TextBlock { Text = detail + (Rewards.Unlocks(a) is { } c ? $" · unlocks {c.Name}" : ""), FontSize = 11, Foreground = Res("MutedBrush"), TextWrapping = TextWrapping.Wrap });
        row.Children.Add(text);
        return row;
    }

    /// <summary>One row of swatches per kind (background, frame, colour, badge); clicking an unlocked one picks it.</summary>
    public static Panel StyleRows(RewardProgress p, bool unlockedOnly, Action<CardStyle> pick)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        var style = Rewards.Effective(p);
        foreach (var kind in Enum.GetValues<CosmeticKind>())
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var label = new TextBlock
            {
                Text = kind switch { CosmeticKind.Background => "Background", CosmeticKind.Border => "Frame", CosmeticKind.Accent => "Colour", _ => "Badge" },
                Width = 86, VerticalAlignment = VerticalAlignment.Center, Foreground = Res("MutedBrush"), FontSize = 12,
            };
            DockPanel.SetDock(label, Dock.Left);
            row.Children.Add(label);
            var swatches = new WrapPanel();
            foreach (var c in Rewards.Cosmetics.Where(c => c.Kind == kind))
            {
                bool unlocked = Rewards.IsUnlocked(p, kind, c.Id);
                if (unlockedOnly && !unlocked) continue;
                bool chosen = style.Get(kind) == c.Id;
                var swatch = CardStyles.Swatch(kind, c.Id);
                var grid = new Grid { Children = { swatch } };
                if (!unlocked)
                {
                    swatch.Opacity = 0.35;
                    grid.Children.Add(new TextBlock { Text = "🔒", FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
                }
                var button = new Button
                {
                    Content = grid, Margin = new Thickness(0, 0, 6, 6), Style = (Style)Application.Current.FindResource("SwatchButton"),
                    BorderBrush = chosen ? Res("Accent3Brush") : Brushes.Transparent, IsEnabled = unlocked,
                    ToolTip = unlocked ? (chosen ? $"{c.Name} (in use)" : c.Name) : $"{c.Name}: {Rewards.UnlockedBy(kind, c.Id)?.HowTo ?? "locked"}",
                };
                System.Windows.Automation.AutomationProperties.SetName(button, $"{kind} {c.Name}{(unlocked ? "" : " (locked)")}");
                if (unlocked && !chosen)
                    button.Click += (_, _) => pick(style.With(kind, c.Id));
                swatches.Children.Add(button);
            }
            row.Children.Add(swatches);
            panel.Children.Add(row);
        }
        return panel;
    }

    /// <summary>A "Style" button that opens the unlocked styles in a small popup (next to the share buttons).</summary>
    public static Button StyleButton(Func<RewardProgress?> progress, Action<CardStyle> pick)
    {
        var button = new Button { Content = "Style ▾", Margin = new Thickness(8, 0, 0, 0), ToolTip = "The look of your share image (unlock more in Collection)" };
        var popup = new Popup { PlacementTarget = button, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true };
        button.Click += (_, _) =>
        {
            if (progress() is not { } p) return;
            void Fill() => popup.Child = new Border
            {
                Background = Res("Panel"), BorderBrush = Res("LineBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 4, 8, 4), Margin = new Thickness(0, 4, 0, 0),
                Child = StyleRows(p, unlockedOnly: true, s => { pick(s); Fill(); }),
            };
            Fill();
            popup.IsOpen = true;
        };
        return button;
    }
}
