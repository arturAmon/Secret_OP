using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using LeagueOverlay.Core.Analysis;

namespace LeagueOverlay;

/// <summary>
/// Team item-gold difference over game time (your team minus the enemy's, the same numbers as the overlay):
/// above zero you're ahead (shaded in your team's colour), below zero the enemy is (their colour).
/// Underneath, a timeline per player:
/// item icons where each purchase happened and level numbers where each level-up happened.
/// Drawn directly with a DrawingContext and only redrawn when the history or the hover position changes.
/// </summary>
internal sealed class GoldGraph : FrameworkElement
{
    // Team colours: checked for colour-blind separation and contrast on the dark panel (#181B22).
    public static readonly Color MyTeamColor = Color.FromRgb(0x3D, 0x8E, 0xF0);
    public static readonly Color EnemyColor = Color.FromRgb(0xF0, 0x60, 0x3C);

    private static readonly Brush MyBrush = Frozen(new SolidColorBrush(MyTeamColor));
    private static readonly Brush EnemyBrush = Frozen(new SolidColorBrush(EnemyColor));
    private static readonly Brush TextBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xE6, 0xE8, 0xEC)));
    private static readonly Brush MutedBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x8B, 0x93, 0xA3)));
    private static readonly Brush AccentBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xC8, 0x9B, 0x3C)));
    private static readonly Brush TooltipBg = Frozen(new SolidColorBrush(Color.FromRgb(0x24, 0x28, 0x32)));
    private static readonly Brush RowBg = Frozen(new SolidColorBrush(Color.FromArgb(0x0C, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush FallbackIcon = Frozen(new SolidColorBrush(Color.FromRgb(0x2F, 0x35, 0x42)));
    private static readonly Pen GridPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0x26, 0x2B, 0x35))), 1));
    private static readonly Pen FaintGridPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF))), 1));
    private static readonly Pen AxisPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0x3A, 0x40, 0x4D))), 1));
    private static readonly Pen HoverPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0x80, 0xE6, 0xE8, 0xEC))), 1));
    private static readonly Pen MyPen = LinePen(MyBrush);
    private static readonly Pen EnemyPen = LinePen(EnemyBrush);
    private static readonly Brush MyFill = Frozen(new SolidColorBrush(Color.FromArgb(0x40, MyTeamColor.R, MyTeamColor.G, MyTeamColor.B)));
    private static readonly Brush EnemyFill = Frozen(new SolidColorBrush(Color.FromArgb(0x40, EnemyColor.R, EnemyColor.G, EnemyColor.B)));
    private static readonly Pen IconRing = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0x18, 0x1B, 0x22))), 2));
    private static readonly Typeface Font = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface BoldFont = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private const double LeftPad = 128, RightPad = 92, TopPad = 10, AxisHeight = 22;
    private const double SectionHeight = 18, RowHeight = 34, Icon = 20, MinChartHeight = 140;

    private HistorySnapshot _history = HistorySnapshot.Empty;
    private bool _showComponents;
    private Point? _mouse;
    private readonly List<(Rect Box, string Text)> _hotspots = new(); // item icons, for hover text

    public IconCache? Icons { get; set; }

    public HistorySnapshot History
    {
        get => _history;
        set { _history = value; InvalidateVisual(); }
    }

    public bool ShowComponents
    {
        get => _showComponents;
        set { _showComponents = value; InvalidateVisual(); }
    }

    public GoldGraph()
    {
        ClipToBounds = true;
        MouseMove += (_, e) => { _mouse = e.GetPosition(this); InvalidateVisual(); };
        MouseLeave += (_, _) => { _mouse = null; InvalidateVisual(); };
    }

    private double TimelineHeight => _history.Players.Count == 0 ? 0 : 2 * SectionHeight + _history.Players.Count * RowHeight + 4;

    protected override Size MeasureOverride(Size available) =>
        new(Math.Min(available.Width, 600), MinChartHeight + AxisHeight + TopPad + Math.Max(TimelineHeight, 2 * SectionHeight + 10 * RowHeight));

    protected override void OnRender(DrawingContext dc)
    {
        _hotspots.Clear();
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h)); // whole area receives mouse moves

        var gold = _history.Gold;
        if (gold.Count == 0)
        {
            DrawText(dc, "The graph fills in while a game is running.", 13, MutedBrush, new Point(w / 2, h / 2 - 10), center: true);
            return;
        }

        double plotLeft = LeftPad, plotRight = Math.Max(plotLeft + 50, w - RightPad);
        double chartTop = TopPad;
        double chartBottom = Math.Max(chartTop + MinChartHeight, h - TimelineHeight - AxisHeight);
        double t0 = _history.StartedLate ? _history.StartTime : 0;
        double t1 = Math.Max(t0 + 60, _history.EndTime);
        double X(double t) => plotLeft + (t - t0) / (t1 - t0) * (plotRight - plotLeft);
        double TimeAt(double x) => t0 + (x - plotLeft) / (plotRight - plotLeft) * (t1 - t0);

        // ---- y axis: gold difference, symmetric around zero so "ahead" and "behind" read at the same scale ----
        static int Diff(GoldSample g) => g.MyTeam - g.Enemy;
        int maxAbs = Math.Max(1000, gold.Max(g => Math.Abs(Diff(g))));
        double yStep = NiceStep(maxAbs / 2.0);
        double yMax = Math.Ceiling(maxAbs * 1.1 / yStep) * yStep;
        double zeroY = (chartTop + chartBottom) / 2;
        double Y(double v) => zeroY - v / yMax * (chartBottom - chartTop) / 2;
        for (double v = -yMax; v <= yMax + 1; v += yStep)
        {
            double y = Math.Round(Y(v)) + 0.5;
            if (v != 0) dc.DrawLine(GridPen, new Point(plotLeft, y), new Point(plotRight, y));
            DrawText(dc, v > 0 ? "+" + ShortGold(v) : v < 0 ? "-" + ShortGold(-v) : "0", 11, MutedBrush, new Point(plotLeft - 8, y - 8), alignRight: true);
        }
        DrawText(dc, "Your team ahead", 10, MyBrush, new Point(plotLeft + 6, chartTop + (_history.StartedLate ? 16 : 2)));
        DrawText(dc, "Enemy ahead", 10, EnemyBrush, new Point(plotLeft + 6, chartBottom - 15));

        // ---- x axis: minutes, gridlines carried down through the timeline ----
        double minutes = (t1 - t0) / 60;
        double xStepMin = minutes <= 6 ? 1 : minutes <= 15 ? 2 : minutes <= 40 ? 5 : 10;
        double timelineBottom = chartBottom + AxisHeight + TimelineHeight;
        for (double m = Math.Ceiling(t0 / 60 / xStepMin) * xStepMin; m * 60 <= t1 + 0.1; m += xStepMin)
        {
            double x = Math.Round(X(m * 60)) + 0.5;
            dc.DrawLine(GridPen, new Point(x, chartTop), new Point(x, chartBottom));
            dc.DrawLine(FaintGridPen, new Point(x, chartBottom + AxisHeight), new Point(x, timelineBottom));
            DrawText(dc, $"{m:0}:00", 11, MutedBrush, new Point(x, chartBottom + 4), center: true);
        }

        // Zero line on top of the gridlines, under the data.
        dc.DrawLine(AxisPen, new Point(plotLeft, Math.Round(zeroY) + 0.5), new Point(plotRight, Math.Round(zeroY) + 0.5));

        // ---- the difference (steps: gold only changes when something is bought) ----
        var line = StepGeometry(gold, Diff, X, Y, closeTo: null);
        var area = StepGeometry(gold, Diff, X, Y, closeTo: zeroY);
        var above = new RectangleGeometry(new Rect(plotLeft, chartTop - 2, plotRight - plotLeft + 2, zeroY - chartTop + 2));
        var below = new RectangleGeometry(new Rect(plotLeft, zeroY, plotRight - plotLeft + 2, chartBottom - zeroY + 2));
        foreach (var (clip, fill, pen) in new[] { (above, MyFill, MyPen), (below, EnemyFill, EnemyPen) })
        {
            dc.PushClip(clip);
            dc.DrawGeometry(fill, null, area);
            dc.DrawGeometry(null, pen, line);
            dc.Pop();
        }
        var last = gold[^1];
        DrawEndLabel(dc, plotRight, Y(Diff(last)), Diff(last));

        if (_history.StartedLate)
            DrawText(dc, $"Recording started at {Clock(_history.StartTime)} (app opened mid-game)", 11, MutedBrush, new Point(plotLeft + 6, chartTop + 2));

        // ---- per-player timeline ----
        DrawTimeline(dc, chartBottom + AxisHeight, plotLeft, plotRight, X);

        // ---- hover ----
        if (_mouse is { } m0 && m0.X >= plotLeft && m0.X <= plotRight && m0.Y <= timelineBottom)
        {
            var hit = _hotspots.LastOrDefault(s => s.Box.Contains(m0));
            if (hit.Text is not null)
            {
                DrawTooltip(dc, m0, [hit.Text], w, h);
            }
            else
            {
                double t = Math.Clamp(TimeAt(m0.X), t0, last.Time);
                var at = gold.LastOrDefault(g => g.Time <= t) ?? gold[0];
                int lead = Diff(at);
                dc.DrawLine(HoverPen, new Point(m0.X, chartTop), new Point(m0.X, timelineBottom));
                dc.DrawEllipse(lead >= 0 ? MyBrush : EnemyBrush, IconRing, new Point(m0.X, Y(lead)), 4.5, 4.5);
                DrawTooltip(dc, m0,
                [
                    Clock(t),
                    lead == 0 ? "Gold even" : lead > 0 ? $"Your team leads by {lead:N0} g" : $"Enemy leads by {-lead:N0} g",
                    $"Your team  {at.MyTeam:N0} g",
                    $"Enemy      {at.Enemy:N0} g",
                ], w, h);
            }
        }
    }

    /// <summary>The difference as a step line; with closeTo, closed down to that y (the zero line) for the shaded area.</summary>
    private static StreamGeometry StepGeometry(IReadOnlyList<GoldSample> gold, Func<GoldSample, int> value,
        Func<double, double> x, Func<double, double> y, double? closeTo)
    {
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            double x0 = x(gold[0].Time);
            if (closeTo is { } zero)
            {
                ctx.BeginFigure(new Point(x0, zero), true, true);
                ctx.LineTo(new Point(x0, y(value(gold[0]))), false, false);
            }
            else ctx.BeginFigure(new Point(x0, y(value(gold[0]))), false, false);
            for (int i = 1; i < gold.Count; i++)
            {
                double xi = x(gold[i].Time);
                ctx.LineTo(new Point(xi, y(value(gold[i - 1]))), closeTo is null, true);
                ctx.LineTo(new Point(xi, y(value(gold[i]))), closeTo is null, true);
            }
            if (closeTo is { } z) ctx.LineTo(new Point(x(gold[^1].Time), z), false, false);
        }
        geo.Freeze();
        return geo;
    }

    /// <summary>Direct label at the right end of the line: who leads and by how much.</summary>
    private void DrawEndLabel(DrawingContext dc, double plotRight, double y, int lead)
    {
        var brush = lead >= 0 ? MyBrush : EnemyBrush;
        dc.DrawEllipse(brush, null, new Point(plotRight + 8, y), 3.5, 3.5);
        DrawText(dc, lead > 0 ? "You lead" : lead < 0 ? "Enemy leads" : "Even", 11, MutedBrush, new Point(plotRight + 16, y - 15));
        DrawText(dc, ShortGold(Math.Abs(lead)), 12, TextBrush, new Point(plotRight + 16, y - 2), bold: true);
    }

    private void DrawTimeline(DrawingContext dc, double top, double plotLeft, double plotRight, Func<double, double> x)
    {
        var itemsByPlayer = _history.Items.Where(i => _showComponents || i.Completed).ToLookup(i => i.PlayerKey);
        var levelsByPlayer = _history.Levels.ToLookup(l => l.PlayerKey);
        double y = top;
        bool? section = null;

        foreach (var p in _history.Players)
        {
            if (section != p.MyTeam)
            {
                section = p.MyTeam;
                DrawText(dc, p.MyTeam ? "YOUR TEAM" : "ENEMY TEAM", 10, p.MyTeam ? MyBrush : EnemyBrush, new Point(8, y + 3), bold: true);
                DrawText(dc, "items bought  ·  level-ups", 10, MutedBrush, new Point(plotLeft, y + 3));
                y += SectionHeight;
            }

            var row = new Rect(0, y, ActualWidth, RowHeight - 2);
            dc.DrawRoundedRectangle(RowBg, null, row, 4, 4);

            // Who: team stripe, champion icon, name.
            dc.DrawRectangle(p.MyTeam ? MyBrush : EnemyBrush, null, new Rect(0, y, 3, RowHeight - 2));
            DrawIcon(dc, Icons?.Champion(p.ChampionKey), new Rect(10, y + 5, 22, 22), p.Name);
            DrawText(dc, p.IsMe ? $"{p.Name} (you)" : p.Name, 12, p.IsMe ? AccentBrush : TextBrush, new Point(38, y + 8),
                bold: p.IsMe, maxWidth: plotLeft - 44);

            // Items: icon centred on the purchase time, pushed right if it would cover the previous one.
            double nextFree = plotLeft - Icon;
            foreach (var e in itemsByPlayer[p.Key])
            {
                double left = Math.Max(x(e.Time) - Icon / 2, nextFree);
                if (left > plotRight + RightPad - Icon) break;
                var box = new Rect(left, y + 2, e.Completed ? Icon : Icon - 4, e.Completed ? Icon : Icon - 4);
                if (!e.Completed) box.Offset(2, 2);
                DrawIcon(dc, Icons?.Item(e.ItemId), box, e.Name);
                _hotspots.Add((box, $"{p.Name} bought {e.Name} at {Clock(e.Time)}"));
                nextFree = left + Icon + 1;
            }

            // Level-ups: the new level under its time; ult levels (6/11/16) highlighted.
            double nextText = 0;
            foreach (var l in levelsByPlayer[p.Key])
            {
                bool ult = l.Level is 6 or 11 or 16;
                var ft = Text(l.Level.ToString(CultureInfo.InvariantCulture), 9.5, ult ? AccentBrush : MutedBrush, ult);
                double left = Math.Max(x(l.Time) - ft.Width / 2, nextText);
                dc.DrawText(ft, new Point(left, y + RowHeight - 14));
                nextText = left + ft.Width + 2;
            }
            y += RowHeight;
        }
    }

    private static void DrawIcon(DrawingContext dc, ImageSource? image, Rect box, string name)
    {
        if (image is not null)
        {
            dc.PushClip(new RectangleGeometry(box, 3, 3));
            dc.DrawImage(image, box);
            dc.Pop();
        }
        else
        {
            dc.DrawRoundedRectangle(FallbackIcon, null, box, 3, 3);
        }
    }

    private void DrawTooltip(DrawingContext dc, Point at, IReadOnlyList<string> lines, double w, double h)
    {
        var texts = lines.Select((l, i) => Text(l, 12, i == 0 ? MutedBrush : TextBrush, i == 0)).ToList();
        double width = texts.Max(t => t.Width) + 20, height = texts.Sum(t => t.Height) + 14;
        double left = at.X + 14 + width > w ? at.X - 14 - width : at.X + 14;
        double top = Math.Clamp(at.Y - height / 2, 0, Math.Max(0, h - height));
        dc.DrawRoundedRectangle(TooltipBg, AxisPen, new Rect(left, top, width, height), 6, 6);
        double y = top + 7;
        foreach (var t in texts) { dc.DrawText(t, new Point(left + 10, y)); y += t.Height; }
    }

    private void DrawText(DrawingContext dc, string text, double size, Brush brush, Point at,
        bool center = false, bool alignRight = false, bool bold = false, double maxWidth = 0)
    {
        var ft = Text(text, size, brush, bold);
        if (maxWidth > 0) { ft.MaxTextWidth = maxWidth; ft.MaxLineCount = 1; ft.Trimming = TextTrimming.CharacterEllipsis; }
        double x = center ? at.X - ft.Width / 2 : alignRight ? at.X - ft.Width : at.X;
        dc.DrawText(ft, new Point(x, at.Y));
    }

    private FormattedText Text(string text, double size, Brush brush, bool bold = false) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, bold ? BoldFont : Font, size, brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private static double NiceStep(double raw)
    {
        double mag = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(raw, 1))));
        double n = raw / mag;
        return (n <= 1 ? 1 : n <= 2 ? 2 : n <= 5 ? 5 : 10) * mag;
    }

    private static string ShortGold(double v) => v >= 1000 ? $"{v / 1000:0.#}k" : $"{v:0}";
    private static string Clock(double seconds) => $"{(int)seconds / 60}:{(int)seconds % 60:00}";

    private static Pen LinePen(Brush b) => Frozen(new Pen(b, 2) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });

    private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }
}
