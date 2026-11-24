using System.Windows.Media;

namespace LeagueOverlay;

/// <summary>
/// Colours from the app icon, used as accents on the dark UI (the same values are in App.xaml for XAML).
/// Team colours are deeper versions of the icon's blue and pink: the exact icon colours are too bright for large
/// chart areas on a dark background. They were checked for colour-blind separation and contrast on the dark panels.
/// Green and red stay reserved for "ahead" and "behind".
/// </summary>
public static class Theme
{
    public static readonly Color Yellow = Color.FromRgb(0xF7, 0xFD, 0x0C); // main accent
    public static readonly Color Blue = Color.FromRgb(0x2C, 0xCD, 0xD5);   // links, navigation, "you"
    public static readonly Color Pink = Color.FromRgb(0xF1, 0x51, 0xDF);   // rival, selection

    public static readonly Color MyTeam = Color.FromRgb(0x1C, 0xA6, 0xAE);  // charts: your team
    public static readonly Color Enemy = Color.FromRgb(0xB0, 0x2F, 0xA0);   // charts: enemy team

    public static readonly Brush YellowBrush = Frozen(Yellow), BlueBrush = Frozen(Blue), PinkBrush = Frozen(Pink);
    public static readonly Brush MyTeamBrush = Frozen(MyTeam), EnemyBrush = Frozen(Enemy);

    /// <summary>The colour at the given opacity (0-255), e.g. for a soft highlight behind text.</summary>
    public static Brush Tint(Color c, byte alpha) => Frozen(Color.FromArgb(alpha, c.R, c.G, c.B));

    /// <summary>Blue to pink, like the icon's background.</summary>
    public static Brush IconGradient(double angle = 0)
    {
        var brush = new LinearGradientBrush(Blue, Pink, angle);
        brush.Freeze();
        return brush;
    }

    private static Brush Frozen(Color c)
    {
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        return brush;
    }
}
