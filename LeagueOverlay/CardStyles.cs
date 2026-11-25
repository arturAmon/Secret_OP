using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using LeagueOverlay.Core.History;

namespace LeagueOverlay;

/// <summary>
/// What the share image cosmetics look like (see <see cref="Rewards.Cosmetics"/>): backgrounds, frames drawn over the
/// card's edge, accent colours and badges. Also draws the small swatches the Collection tab shows.
/// </summary>
internal static class CardStyles
{
    private static readonly Color Bg1 = Color.FromRgb(0x0F, 0x11, 0x15), Bg2 = Color.FromRgb(0x1C, 0x21, 0x2D);

    public static Brush Background(string id)
    {
        Brush brush = id switch
        {
            "holo" => Holo(),
            "royal" => Glow(Color.FromRgb(0x14, 0x10, 0x05), Color.FromRgb(0x3A, 0x2C, 0x08), Color.FromArgb(0x50, 0xF5, 0xC8, 0x42)),
            "arcane" => Glow(Color.FromRgb(0x05, 0x0D, 0x22), Color.FromRgb(0x0C, 0x26, 0x5A), Color.FromArgb(0x60, 0x3A, 0x8D, 0xFF)),
            "crimson" => Glow(Color.FromRgb(0x16, 0x05, 0x08), Color.FromRgb(0x3D, 0x0B, 0x14), Color.FromArgb(0x50, 0xE0, 0x30, 0x48)),
            "shadow" => Glow(Color.FromRgb(0x0B, 0x07, 0x13), Color.FromRgb(0x26, 0x12, 0x3D), Color.FromArgb(0x50, 0x8E, 0x4D, 0xD8)),
            "mint" => Glow(Color.FromRgb(0x05, 0x13, 0x0E), Color.FromRgb(0x0D, 0x36, 0x27), Color.FromArgb(0x50, 0x3C, 0xE0, 0xA8)),
            _ => new LinearGradientBrush(Bg1, Bg2, 35),
        };
        brush.Freeze();
        return brush;
    }

    /// <summary>A diagonal gradient with a soft glow in the top right corner.</summary>
    private static Brush Glow(Color dark, Color light, Color glow)
    {
        var drawing = new DrawingGroup();
        var rect = new RectangleGeometry(new Rect(0, 0, 1, 1));
        drawing.Children.Add(new GeometryDrawing(new LinearGradientBrush(dark, light, 35), null, rect));
        drawing.Children.Add(new GeometryDrawing(new RadialGradientBrush(glow, Color.FromArgb(0, glow.R, glow.G, glow.B))
            { Center = new Point(0.85, 0.1), GradientOrigin = new Point(0.85, 0.1), RadiusX = 0.6, RadiusY = 0.9 }, null, rect));
        return new DrawingBrush(drawing) { Stretch = Stretch.Fill };
    }

    /// <summary>Holographic: the dark card with fine cyan/pink diagonal stripes and a sheen.</summary>
    private static Brush Holo()
    {
        var drawing = new DrawingGroup();
        var rect = new RectangleGeometry(new Rect(0, 0, 1200, 675));
        drawing.Children.Add(new GeometryDrawing(new LinearGradientBrush(Bg1, Bg2, 35), null, rect));
        var stripes = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute, SpreadMethod = GradientSpreadMethod.Repeat,
            StartPoint = new Point(0, 0), EndPoint = new Point(36, 36),
            GradientStops =
            {
                new(Color.FromArgb(0, 0x2C, 0xCD, 0xD5), 0), new(Color.FromArgb(0x26, 0x2C, 0xCD, 0xD5), 0.25),
                new(Color.FromArgb(0, 0x2C, 0xCD, 0xD5), 0.5), new(Color.FromArgb(0x22, 0xF1, 0x51, 0xDF), 0.75),
                new(Color.FromArgb(0, 0xF1, 0x51, 0xDF), 1),
            },
        };
        drawing.Children.Add(new GeometryDrawing(stripes, null, rect));
        drawing.Children.Add(new GeometryDrawing(new LinearGradientBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF), Color.FromArgb(0, 0xFF, 0xFF, 0xFF), new Point(0, 0), new Point(0.6, 1)), null, rect));
        return new DrawingBrush(drawing) { Stretch = Stretch.Fill };
    }

    public static Color AccentColor(string id) => id switch
    {
        "pink" => Theme.Pink,
        "cyan" => Theme.Blue,
        "violet" => Color.FromRgb(0xA9, 0x7C, 0xFF),
        _ => Theme.Yellow,
    };

    public static Brush Accent(string id)
    {
        var b = new SolidColorBrush(AccentColor(id));
        b.Freeze();
        return b;
    }

    /// <summary>How far a frame reaches into the card (the content keeps clear of it).</summary>
    public static Thickness Inset(string border) => border switch
    {
        "neon" => new Thickness(6),
        "gold" => new Thickness(12),
        "rivets" => new Thickness(16),
        "steel" => new Thickness(12),
        _ => new Thickness(0, 6, 0, 0),
    };

    /// <summary>The frame drawn over the edge of a card of this size.</summary>
    public static UIElement Frame(string border, double width, double height)
    {
        var grid = new Grid { Width = width, Height = height, IsHitTestVisible = false };
        switch (border)
        {
            case "neon":
                grid.Children.Add(new Border
                {
                    BorderBrush = Theme.IconGradient(45), BorderThickness = new Thickness(4), CornerRadius = new CornerRadius(6),
                    Effect = new DropShadowEffect { Color = Theme.Pink, BlurRadius = 18, ShadowDepth = 0, Opacity = 0.9 },
                });
                break;
            case "gold":
                grid.Children.Add(new Border { BorderBrush = Metal(Color.FromRgb(0x7A, 0x5A, 0x0E), Color.FromRgb(0xF6, 0xDA, 0x74)), BorderThickness = new Thickness(8) });
                grid.Children.Add(new Border { Margin = new Thickness(10), BorderBrush = new SolidColorBrush(Color.FromArgb(0xB0, 0xF6, 0xDA, 0x74)), BorderThickness = new Thickness(1.5) });
                break;
            case "rivets":
                grid.Children.Add(new Border { BorderBrush = Metal(Color.FromRgb(0x34, 0x3A, 0x45), Color.FromRgb(0x8A, 0x93, 0xA3)), BorderThickness = new Thickness(14) });
                grid.Children.Add(new Border { Margin = new Thickness(14), BorderBrush = new SolidColorBrush(Color.FromArgb(0xC0, 0x08, 0x09, 0x0C)), BorderThickness = new Thickness(2) });
                grid.Children.Add(Rivets(width, height, 7, 60));
                break;
            case "steel":
                grid.Children.Add(new Border { BorderBrush = Brushed(), BorderThickness = new Thickness(10) });
                grid.Children.Add(new Border { Margin = new Thickness(10), BorderBrush = new SolidColorBrush(Color.FromArgb(0x70, 0xD8, 0xDE, 0xE8)), BorderThickness = new Thickness(1) });
                break;
            default:
                grid.Children.Add(new Border { BorderBrush = Theme.IconGradient(), BorderThickness = new Thickness(0, 6, 0, 0) });
                break;
        }
        return grid;
    }

    /// <summary>A metal band: dark at the outside, bright in the middle.</summary>
    private static Brush Metal(Color dark, Color light) => new LinearGradientBrush
    {
        StartPoint = new Point(0, 0), EndPoint = new Point(1, 1),
        GradientStops = { new(dark, 0), new(light, 0.3), new(dark, 0.55), new(light, 0.8), new(dark, 1) },
    };

    private static Brush Brushed()
    {
        var stops = new GradientStopCollection();
        var rng = new Random(7);
        for (int i = 0; i <= 12; i++)
        {
            byte v = (byte)(0x40 + rng.Next(0, 0x38));
            stops.Add(new GradientStop(Color.FromRgb(v, (byte)(v + 4), (byte)(v + 10)), i / 12.0));
        }
        return new LinearGradientBrush(stops, 90);
    }

    /// <summary>Rivets along the frame, <paramref name="spacing"/> apart, centred <paramref name="edge"/> from the outside.</summary>
    private static Canvas Rivets(double width, double height, double edge, double spacing)
    {
        var canvas = new Canvas { Width = width, Height = height };
        void Rivet(double x, double y)
        {
            var e = new Ellipse
            {
                Width = 7, Height = 7, Stroke = new SolidColorBrush(Color.FromRgb(0x1A, 0x1D, 0x23)), StrokeThickness = 1,
                Fill = new RadialGradientBrush(Color.FromRgb(0xE4, 0xE8, 0xEF), Color.FromRgb(0x4A, 0x51, 0x5E))
                    { GradientOrigin = new Point(0.35, 0.3), Center = new Point(0.45, 0.45) },
            };
            Canvas.SetLeft(e, x - 3.5);
            Canvas.SetTop(e, y - 3.5);
            canvas.Children.Add(e);
        }
        int across = Math.Max(2, (int)Math.Round((width - 2 * edge) / spacing)), down = Math.Max(2, (int)Math.Round((height - 2 * edge) / spacing));
        for (int i = 0; i <= across; i++)
        {
            double x = edge + i * (width - 2 * edge) / across;
            Rivet(x, edge);
            Rivet(x, height - edge);
        }
        for (int i = 1; i < down; i++)
        {
            double y = edge + i * (height - 2 * edge) / down;
            Rivet(edge, y);
            Rivet(width - edge, y);
        }
        return canvas;
    }

    /// <summary>The badge shown in the share image's header, or null for none.</summary>
    public static UIElement? Badge(string id, Brush accent) => id switch
    {
        "agent" => new Border
        {
            BorderBrush = accent, BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 2, 10, 3),
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0),
            Child = new TextBlock { Text = "AGENT OF CHAOS", FontSize = 12, FontWeight = FontWeights.Bold, Foreground = accent },
        },
        "crown" => new TextBlock { Text = "♛ LIVING LEGEND", FontSize = 14, FontWeight = FontWeights.Bold, Foreground = accent, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) },
        _ => null,
    };

    /// <summary>A small preview of one cosmetic, for the Collection tab and the style picker.</summary>
    public static FrameworkElement Swatch(CosmeticKind kind, string id)
    {
        const double w = 240, h = 135;
        var card = new Grid { Width = w, Height = h };
        card.Children.Add(new Border { Background = kind == CosmeticKind.Background ? Background(id) : Background("default") });
        switch (kind)
        {
            case CosmeticKind.Border:
                card.Children.Add(Frame(id, w, h));
                break;
            case CosmeticKind.Accent:
                card.Children.Add(new Ellipse { Width = 70, Height = 70, Fill = Accent(id) });
                break;
            case CosmeticKind.Badge:
                card.Children.Add(new TextBlock
                {
                    Text = id switch { "agent" => "AGENT", "crown" => "♛", _ => "—" }, FontSize = id == "crown" ? 70 : 40, FontWeight = FontWeights.Bold,
                    Foreground = Theme.YellowBrush, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                });
                break;
        }
        return new Viewbox { Width = 64, Height = 36, Child = card };
    }
}
