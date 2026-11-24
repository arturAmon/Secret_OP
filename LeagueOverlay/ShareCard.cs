using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LeagueOverlay.Core.History;

namespace LeagueOverlay;

/// <summary>
/// A picture of one game to send to friends: result, your rival duel (or your own stats when there's no rival),
/// and the titles you earned. 1200×675 (a 16:9 card that chat apps preview well), rendered at 2× for sharpness.
/// </summary>
internal static class ShareCard
{
    public const int Width = 1200, Height = 675;

    private static readonly Color Bg1 = Color.FromRgb(0x0F, 0x11, 0x15), Bg2 = Color.FromRgb(0x1C, 0x21, 0x2D);
    private static readonly Brush Text = Frozen(0xE6, 0xE8, 0xEC), Muted = Frozen(0x8B, 0x93, 0xA3), Accent = Theme.YellowBrush;
    private static readonly Brush Gold = Theme.YellowBrush, Green = Frozen(0x4C, 0xAF, 0x7A), Red = Frozen(0xE0, 0x5D, 0x5D);
    private static readonly Brush Card = Frozen(0x1F, 0x23, 0x2C), Line = Frozen(0x2A, 0x2F, 0x3A), IconBg = Frozen(0x2F, 0x35, 0x42);
    private static readonly Brush Mine = Theme.BlueBrush, Theirs = Theme.PinkBrush; // you and your rival

    // The rows that tell the story best; the rest stay in the app.
    private static readonly string[] DuelRows =
    [
        "Kills", "Deaths", "Assists", "KDA", "Kill participation", "CS", "Gold earned", "Item gold",
        "Damage to champions", "Turret plates", "Vision score", "Wards placed",
    ];

    public static BitmapSource Render(GameRecord g, RivalDuel? duel, IconCache icons)
    {
        var root = new Border
        {
            Width = Width, Height = Height, Padding = new Thickness(44, 36, 44, 32),
            Background = new LinearGradientBrush(Bg1, Bg2, 35),
            BorderBrush = Theme.IconGradient(), BorderThickness = new Thickness(0, 6, 0, 0),
            Child = Content(g, duel, icons),
        };
        TextOptions.SetTextFormattingMode(root, TextFormattingMode.Ideal);
        root.Measure(new Size(Width, Height));
        root.Arrange(new Rect(0, 0, Width, Height));
        root.UpdateLayout();

        var bitmap = new RenderTargetBitmap(Width * 2, Height * 2, 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(root);
        bitmap.Freeze();
        return bitmap;
    }

    public static void SavePng(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    public static string SuggestedFileName(GameRecord g, RivalDuel? duel) =>
        $"League-{g.EndedAt:yyyy-MM-dd-HHmm}-{Safe(g.ChampionName)}" + (duel is null ? "" : $"-vs-{Safe(RiotIds.GameName(duel.Rival.Key))}") + ".png";

    private static string Safe(string s) => string.Concat(s.Where(char.IsLetterOrDigit));

    private static UIElement Content(GameRecord g, RivalDuel? duel, IconCache icons)
    {
        var awards = Awards.For(g);
        var layout = new DockPanel { LastChildFill = true };

        // Header: champion, result, score.
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 22) };
        var brand = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
        brand.Children.Add(new TextBlock { Text = "LEAGUE OVERLAY", FontSize = 13, FontWeight = FontWeights.Bold, Foreground = Muted, HorizontalAlignment = HorizontalAlignment.Right });
        var score = new TextBlock { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
        score.Inlines.Add(new Run("Score ") { FontSize = 18, Foreground = Muted });
        score.Inlines.Add(new Run($"{Awards.Score(awards):N0}") { FontSize = 40, FontWeight = FontWeights.Bold, Foreground = Gold });
        brand.Children.Add(score);
        DockPanel.SetDock(brand, Dock.Right);
        header.Children.Add(brand);

        var champIcon = Icon(icons.Champion(g.ChampionKey), 84);
        DockPanel.SetDock(champIcon, Dock.Left);
        header.Children.Add(champIcon);
        var title = new StackPanel { Margin = new Thickness(18, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var name = new TextBlock { FontSize = 40, FontWeight = FontWeights.Bold, Foreground = Text };
        name.Inlines.Add(new Run(g.ChampionName));
        if (g.Me is { } me) name.Inlines.Add(new Run($"  {RiotIds.GameName(me.Key)}") { FontSize = 22, FontWeight = FontWeights.Normal, Foreground = Muted });
        title.Children.Add(name);
        var line = new TextBlock { FontSize = 20 };
        line.Inlines.Add(new Run(HistoryView.ResultText(g.Result)) { FontWeight = FontWeights.Bold, Foreground = g.Result == "Win" ? Green : g.Result == "Lose" ? Red : Muted });
        line.Inlines.Add(new Run($"  ·  {(int)g.Duration / 60}:{(int)g.Duration % 60:00}  ·  {g.EndedAt:ddd d MMM yyyy}") { Foreground = Muted });
        title.Children.Add(line);
        header.Children.Add(title);
        DockPanel.SetDock(header, Dock.Top);
        layout.Children.Add(header);

        // Footer: titles earned.
        var titles = new WrapPanel { Margin = new Thickness(0, 18, 0, 0) };
        foreach (var a in awards.Take(4))
        {
            titles.Children.Add(new Border
            {
                BorderBrush = Accent, BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(8),
                Background = Theme.Tint(Theme.Yellow, 0x1E),
                Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(0, 0, 10, 0),
                Child = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = a.Title, FontSize = 19, FontWeight = FontWeights.Bold, Foreground = Gold },
                        new TextBlock { Text = a.Reason, FontSize = 13, Foreground = Muted },
                    },
                },
            });
        }
        DockPanel.SetDock(titles, Dock.Bottom);
        layout.Children.Add(titles);

        layout.Children.Add(duel is null ? SoloStats(g, icons) : DuelTable(duel, icons));
        return layout;
    }

    private static UIElement DuelTable(RivalDuel d, IconCache icons)
    {
        var rows = DuelRows.Select(label => d.Stats.FirstOrDefault(s => s.Label == label)).OfType<DuelStat>()
            .Where(s => s.Mine is not null || s.Theirs is not null).Take(10).ToList();

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Column heads: you, the tally, the rival.
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Put(grid, Player(d.Me, "You", Mine, icons, HorizontalAlignment.Left), 0, 0);
        var tally = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        tally.Inlines.Add(new Run($"{d.MyWins}") { FontSize = 44, FontWeight = FontWeights.Bold, Foreground = d.MyWins >= d.RivalWins ? Gold : Text });
        tally.Inlines.Add(new Run("  –  ") { FontSize = 30, Foreground = Muted });
        tally.Inlines.Add(new Run($"{d.RivalWins}") { FontSize = 44, FontWeight = FontWeights.Bold, Foreground = d.RivalWins > d.MyWins ? Gold : Text });
        Put(grid, tally, 0, 1);
        Put(grid, Player(d.Rival, RiotIds.GameName(d.Rival.Key), Theirs, icons, HorizontalAlignment.Right), 0, 2);

        for (int i = 0; i < rows.Count; i++)
        {
            var s = rows[i];
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });
            int r = i + 1;
            if (i % 2 == 0)
            {
                var stripe = new Border { Background = Card, CornerRadius = new CornerRadius(5) };
                Grid.SetColumnSpan(stripe, 3);
                Put(grid, stripe, r, 0);
            }
            Put(grid, Value(s.Show(s.Mine), s.Winner > 0, HorizontalAlignment.Left), r, 0);
            Put(grid, new TextBlock { Text = s.Label, FontSize = 16, Foreground = Muted, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }, r, 1);
            Put(grid, Value(s.Show(s.Theirs), s.Winner < 0, HorizontalAlignment.Right), r, 2);
        }
        return grid;
    }

    private static UIElement Player(RecordedPlayer p, string label, Brush color, IconCache icons, HorizontalAlignment side)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = side, Margin = new Thickness(12, 0, 12, 10) };
        var icon = Icon(icons.Champion(p.ChampionKey), 52);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0) };
        text.Children.Add(new TextBlock { Text = label, FontSize = 24, FontWeight = FontWeights.Bold, Foreground = color, HorizontalAlignment = side });
        text.Children.Add(new TextBlock { Text = p.Name, FontSize = 15, Foreground = Muted, HorizontalAlignment = side });
        if (side == HorizontalAlignment.Left) { panel.Children.Add(icon); panel.Children.Add(text); }
        else { panel.Children.Add(text); panel.Children.Add(icon); }
        return panel;
    }

    private static UIElement Value(string text, bool winner, HorizontalAlignment side) => new TextBlock
    {
        Text = winner ? (side == HorizontalAlignment.Left ? $"{text}  ◀" : $"▶  {text}") : text,
        FontSize = 18, FontWeight = winner ? FontWeights.Bold : FontWeights.Normal, Foreground = winner ? Gold : Text,
        HorizontalAlignment = side, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 18, 0),
    };

    /// <summary>No rival in this game: your own numbers.</summary>
    private static UIElement SoloStats(GameRecord g, IconCache icons)
    {
        var grid = new UniformGrid { Columns = 4, VerticalAlignment = VerticalAlignment.Top };
        if (g.Me is not { } me) return grid;
        var post = g.PostGame?.Find(me.Key);
        double minutes = Math.Max(1, g.Duration / 60);
        int k = post?.Kills ?? me.Kills, d = post?.Deaths ?? me.Deaths, a = post?.Assists ?? me.Assists, cs = post?.Cs ?? me.CreepScore;
        var tiles = new List<(string, string)>
        {
            ("KDA", $"{k} / {d} / {a}"),
            ("CS", $"{cs} ({cs / minutes:0.0}/min)"),
            (post is null ? "Item gold" : "Gold earned", $"{post?.GoldEarned ?? me.ItemGold:N0}"),
            ("Vision score", $"{post?.VisionScore ?? me.VisionScore:0}"),
        };
        if (post is not null)
        {
            tiles.Add(("Kill participation", $"{post.KillParticipation:0%}"));
            tiles.Add(("Damage to champions", $"{post.ChampionDamage:N0}"));
            tiles.Add(("Turret plates", $"{post.TurretPlates}"));
            tiles.Add(("Wards placed", $"{post.WardsPlaced}"));
        }
        foreach (var (label, value) in tiles)
        {
            grid.Children.Add(new Border
            {
                Background = Card, CornerRadius = new CornerRadius(8), Padding = new Thickness(16, 12, 16, 12), Margin = new Thickness(0, 0, 12, 12),
                Child = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = label, FontSize = 15, Foreground = Muted },
                        new TextBlock { Text = value, FontSize = 28, FontWeight = FontWeights.Bold, Foreground = Text },
                    },
                },
            });
        }
        return grid;
    }

    private static Border Icon(ImageSource? image, double size) => new()
    {
        Width = size, Height = size, CornerRadius = new CornerRadius(size / 8), Background = IconBg, ClipToBounds = true,
        BorderBrush = Line, BorderThickness = new Thickness(1),
        Child = new Image { Source = image, Width = size, Height = size },
    };

    private static void Put(Grid grid, UIElement e, int row, int column)
    {
        Grid.SetRow(e, row);
        Grid.SetColumn(e, column);
        grid.Children.Add(e);
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
