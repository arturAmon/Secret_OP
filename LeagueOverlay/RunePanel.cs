using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LeagueOverlay.Core.Builds;
using LeagueOverlay.Core.Data;

namespace LeagueOverlay;

/// <summary>Draws a rune page: keystone with names, then the other runes and the stat shards as icons. Used in the overlay, the app window and History.</summary>
internal static class RunePanel
{
    private static readonly Brush IconBack = Theme.Tint(Color.FromRgb(0x1A, 0x1D, 0x24), 0xFF);

    public static FrameworkElement Page(RuneSetup page, RuneCatalog runes, IconCache icons, Brush muted, bool showNames = true)
    {
        var root = new StackPanel();
        var keystone = runes.Get(page.Keystone);

        var head = new DockPanel();
        var big = Icon(runes, icons, page.Keystone, 36);
        big.Margin = new Thickness(0, 0, 10, 0);
        DockPanel.SetDock(big, Dock.Left);
        head.Children.Add(big);
        var title = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(new TextBlock { Text = keystone?.Name ?? runes.Name(page.Keystone), FontWeight = FontWeights.SemiBold, FontSize = 14 });
        title.Children.Add(new TextBlock { Text = $"{runes.Name(page.Primary)} + {runes.Name(page.Secondary)}", FontSize = 11, Foreground = muted });
        head.Children.Add(title);
        root.Children.Add(head);

        var row = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        for (int i = 1; i < page.Perks.Count; i++)
        {
            bool shard = i >= 6;
            var icon = Icon(runes, icons, page.Perks[i], shard ? 16 : 24);
            icon.VerticalAlignment = VerticalAlignment.Center;
            icon.Margin = new Thickness(i is 4 or 6 ? 10 : 0, 0, 4, 0); // gap before the secondary tree and before the shards
            row.Children.Add(icon);
        }
        root.Children.Add(row);

        if (showNames)
        {
            var names = page.Perks.Skip(1).Take(5).Select(runes.Name);
            root.Children.Add(new TextBlock
            {
                Text = string.Join(" · ", names), FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0),
            });
        }
        return root;
    }

    /// <summary>A round rune icon with its name and short description as tooltip.</summary>
    public static Border Icon(RuneCatalog runes, IconCache icons, int id, double size)
    {
        var info = runes.Get(id);
        return new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Background = IconBack, ClipToBounds = true,
            ToolTip = info is null ? $"Rune {id}" : info.ShortDesc.Length > 0 ? $"{info.Name}\n{info.ShortDesc}" : info.Name,
            Child = new Image { Source = info is null ? null : icons.Rune(info.Icon), Margin = new Thickness(size > 30 ? 1 : 2) },
        };
    }
}
