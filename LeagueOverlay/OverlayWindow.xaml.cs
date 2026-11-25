using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using LeagueOverlay.Core.Analysis;
using LeagueOverlay.Core.Data;
using LeagueOverlay.Core.Lcu;

namespace LeagueOverlay;

/// <summary>
/// Transparent, always-on-top, click-through panel. Global hotkeys (configurable in Settings):
/// show/hide, move mode (drag to reposition; position is saved), quit.
/// </summary>
public partial class OverlayWindow : Window
{
    private static readonly Brush Green = Frozen(0x4C, 0xAF, 0x7A);
    private static readonly Brush Red = Frozen(0xE0, 0x5D, 0x5D);
    private static readonly Brush Muted = Frozen(0x8B, 0x93, 0xA3);
    private static readonly Brush Text = Frozen(0xE6, 0xE8, 0xEC);
    private static readonly Brush Accent = Theme.YellowBrush;
    private static readonly Brush CellBorder = Frozen(0x2A, 0x2F, 0x3A);
    private static readonly Brush IconBackground = Frozen(0x2F, 0x35, 0x42);
    private static readonly Brush ReminderBack = Theme.Tint(Theme.Yellow, 0x33);
    private static readonly Brush UrgentBack = Theme.Tint(Color.FromRgb(0xE0, 0x5D, 0x5D), 0x40);
    private static readonly Brush PanelBorder = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));

    private readonly Settings _settings;
    private readonly IconCache _icons;
    private OverlayState? _lastState;
    private bool _preview;
    private readonly Dictionary<Lane, (Border Cell, TextBlock Label, TextBlock Value, TextBlock Detail)> _laneCells = new();
    private nint _hwnd;
    private KeyboardHook? _keys;
    private bool _moveMode, _userHidden, _inGame;
    private ChampSelectView? _champSelect;
    private PickAdvice? _picks;
    private bool ShowingChampSelect => _champSelect is not null || _picks is { Picks.Count: > 0 };
    private RuneCatalog _runes = RuneCatalog.Empty;
    private Func<int, string?>? _itemName;

    public event Action? QuitRequested;
    /// <summary>Raised when in-game, hidden or move-mode state changes (for the control window's status).</summary>
    public event Action? OverlayStateChanged;

    public bool InGame => _inGame;
    public bool InChampSelect => ShowingChampSelect;
    public bool UserHidden => _userHidden;
    public bool MoveMode => _moveMode;

    public OverlayWindow(Settings settings, IconCache icons)
    {
        _settings = settings;
        _icons = icons;
        InitializeComponent();
        ApplyAppearance();
        // Icons arrive after the first draw; redraw the current state once they're in.
        _icons.IconLoaded += () =>
        {
            if (_lastState is not null) { RenderItems(_lastState.Items); RenderStacks(_lastState.Stacks); }
            else if (ShowingChampSelect) RenderChampSelect(_champSelect, _picks);
        };
        BuildLaneCells();
        PlaceWindow();
        MouseLeftButtonDown += (_, _) => { if (_moveMode) DragMove(); };
        Render(null);
    }

    /// <summary>Creates the native window (for hotkeys) without showing it.</summary>
    public void Initialize() => new WindowInteropHelper(this).EnsureHandle();

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        ApplyWindowStyles();
    }

    protected override void OnClosed(EventArgs e)
    {
        _keys?.Dispose();
        base.OnClosed(e);
    }

    /// <summary>
    /// (Re)binds the hotkeys from settings. Returns the ones another program has also registered
    /// (both would react to the key, so the user should pick another).
    /// </summary>
    public IReadOnlyList<string> ApplyHotkeys()
    {
        _keys ??= new KeyboardHook();
        _keys.Enabled = false; // so the availability probe below doesn't race with a key press
        var bindings = new[]
        {
            (_settings.ToggleOverlay, "Show / hide", (Action)(() => { _userHidden = !_userHidden; UpdateVisibility(); OverlayStateChanged?.Invoke(); })),
            (_settings.MoveOverlay, "Move overlay", () => SetMoveMode(!_moveMode)),
            (_settings.Quit, "Stop app", () => QuitRequested?.Invoke()),
        };
        _keys.SetBindings(bindings.Select(b => (b.Item1, b.Item3)));
        _keys.Enabled = true;
        return bindings.Where(b => !b.Item1.IsAvailable()).Select(b => $"{b.Item2} ({b.Item1})").ToList();
    }

    /// <summary>Stops reacting to the hotkeys, e.g. while the Settings window is capturing new ones.</summary>
    public void SuspendHotkeys()
    {
        if (_keys is not null) _keys.Enabled = false;
    }

    private void ApplyWindowStyles()
    {
        long ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE);
        ex |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW;
        if (_moveMode) ex &= ~(Native.WS_EX_TRANSPARENT | Native.WS_EX_NOACTIVATE);
        else ex |= Native.WS_EX_TRANSPARENT | Native.WS_EX_NOACTIVATE;
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, (nint)ex);
    }

    public void SetMoveMode(bool on)
    {
        if (on == _moveMode) return;
        _moveMode = on;
        ApplyWindowStyles();
        HintText.Text = $"Move mode: drag to reposition · press {_settings.MoveOverlay} again to lock";
        HintText.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        Panel.BorderBrush = on ? Accent : PanelBorder;
        Panel.BorderThickness = new Thickness(on ? 2 : 1);
        Cursor = on ? Cursors.SizeAll : null;
        if (!on)
        {
            _settings.Left = Left;
            _settings.Top = Top;
            _settings.Save();
        }
        UpdateVisibility();
        OverlayStateChanged?.Invoke();
    }

    /// <summary>Applies the background and overlay opacity from settings (also used for the live preview in Settings).</summary>
    public void ApplyAppearance(double? backgroundOpacity = null, double? overlayOpacity = null)
    {
        byte alpha = (byte)Math.Round(Math.Clamp(backgroundOpacity ?? _settings.BackgroundOpacity, 0, 1) * 255);
        var bg = new SolidColorBrush(Color.FromArgb(alpha, 0x0F, 0x11, 0x15));
        bg.Freeze();
        Panel.Background = bg;
        Opacity = Math.Clamp(overlayOpacity ?? _settings.OverlayOpacity, Settings.MinOverlayOpacity, 1);
    }

    /// <summary>Shows the overlay even outside a game, so opacity changes in Settings can be seen.</summary>
    public void SetPreview(bool on)
    {
        _preview = on;
        if (!on) ApplyAppearance();
        UpdateVisibility();
    }

    private void UpdateVisibility()
    {
        bool champSelect = !_inGame && ShowingChampSelect;
        ChampSelectPanel.Visibility = champSelect ? Visibility.Visible : Visibility.Collapsed;
        GamePanel.Visibility = champSelect ? Visibility.Collapsed : Visibility.Visible;
        bool show = _moveMode || _preview || ((_inGame || champSelect) && !_userHidden);
        if (show && !IsVisible) { Show(); KeepOnTop(); }
        else if (!show && IsVisible) Hide();
    }

    /// <summary>Re-asserts topmost without activating (borderless games can end up above us otherwise).</summary>
    public void KeepOnTop()
    {
        if (_hwnd != 0 && IsVisible)
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    private void PlaceWindow()
    {
        var work = SystemParameters.WorkArea;
        double left = _settings.Left ?? work.Right - 360;
        double top = _settings.Top ?? work.Top + 80;
        // Reset if the saved spot is off every screen (e.g. a monitor was unplugged).
        bool onScreen = left >= SystemParameters.VirtualScreenLeft - 50 && top >= SystemParameters.VirtualScreenTop - 50 &&
                        left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 50 &&
                        top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 50;
        Left = onScreen ? left : work.Right - 360;
        Top = onScreen ? top : work.Top + 80;
    }

    private void BuildLaneCells()
    {
        foreach (var lane in Enum.GetValues<Lane>())
        {
            var label = new TextBlock { Text = LaneLabel(lane), FontSize = 10, Foreground = Muted, HorizontalAlignment = HorizontalAlignment.Center };
            var value = new TextBlock { FontSize = 15, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center };
            var detail = new TextBlock { FontSize = 10, Foreground = Muted, HorizontalAlignment = HorizontalAlignment.Center };
            var cell = new Border
            {
                Margin = new Thickness(2), Padding = new Thickness(2, 3, 2, 3), CornerRadius = new CornerRadius(5),
                BorderThickness = new Thickness(1), BorderBrush = CellBorder,
                Child = new StackPanel { Children = { label, value, detail } },
            };
            LaneGrid.Children.Add(cell);
            _laneCells[lane] = (cell, label, value, detail);
        }
    }

    /// <summary>Draws a new state. Null = no game running.</summary>
    public void Render(OverlayState? state)
    {
        bool wasInGame = _inGame;
        _inGame = state is not null;
        _lastState = state;
        if (wasInGame != _inGame) OverlayStateChanged?.Invoke();
        if (state is null)
        {
            GoldText.Text = "No game running";
            GoldText.Foreground = Muted;
            GoldSub.Text = "";
            LaneGrid.Visibility = Visibility.Collapsed;
            ItemsSection.Visibility = Visibility.Collapsed;
            UpdateVisibility();
            return;
        }

        RenderGold(state.Gold);
        RenderStacks(state.Stacks);
        RenderLanes(state.Lanes, state.MyLane);
        RenderItems(state.Items);
        UpdateVisibility();
    }

    /// <summary>Data for drawing rune pages and item names in champion select.</summary>
    public void SetStaticData(StaticData data)
    {
        _runes = data.Runes;
        _itemName = id => data.Items.Get(id)?.Name;
    }

    /// <summary>Champion select: the suggested runes over the League client. Null = not in champion select.</summary>
    public void RenderChampSelect(ChampSelectView? view, PickAdvice? picks = null)
    {
        bool was = ShowingChampSelect;
        _champSelect = view;
        _picks = view is { Locked: true } ? null : picks;
        if (was != ShowingChampSelect) OverlayStateChanged?.Invoke();
        ChampSelectPanel.Children.Clear();
        if (_picks is { Picks.Count: > 0 } advice)
        {
            // Champion suggestions until you lock in.
            ChampSelectPanel.Children.Add(new TextBlock
            {
                Text = advice.Mode == PickMode.Surprise ? "SURPRISE PICK" : $"SUGGESTED PICKS · {advice.Role}".ToUpperInvariant(),
                FontSize = 10, FontWeight = FontWeights.Bold, Foreground = advice.Mode == PickMode.Surprise ? Theme.PinkBrush : Accent, Margin = new Thickness(0, 0, 0, 4),
            });
            foreach (var pick in advice.Picks)
            {
                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 4) };
                var icon = new Border
                {
                    Width = 30, Height = 30, CornerRadius = new CornerRadius(4), ClipToBounds = true, Margin = new Thickness(0, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Top, Background = IconBackground, Child = new Image { Source = _icons.Champion(pick.ChampionId) },
                };
                DockPanel.SetDock(icon, Dock.Left);
                row.Children.Add(icon);
                var text = new StackPanel();
                text.Children.Add(new TextBlock { Text = pick.Name, FontWeight = FontWeights.SemiBold });
                text.Children.Add(new TextBlock
                {
                    Text = string.Join(" · ", pick.Reasons.Take(2)), FontSize = 10.5, TextWrapping = TextWrapping.Wrap,
                    Foreground = pick.Reasons.FirstOrDefault()?.StartsWith("Careful") == true ? Red : Muted,
                });
                row.Children.Add(text);
                ChampSelectPanel.Children.Add(row);
            }
            if (view is not null) ChampSelectPanel.Children.Add(new Border { Height = 1, Background = CellBorder, Margin = new Thickness(0, 6, 0, 8) });
        }
        if (view is not null)
        {
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var champIcon = new Border
            {
                Width = 30, Height = 30, CornerRadius = new CornerRadius(4), ClipToBounds = true, Margin = new Thickness(0, 0, 8, 0),
                Background = IconBackground, Child = new Image { Source = _icons.Champion(view.ChampionId) },
            };
            DockPanel.SetDock(champIcon, Dock.Left);
            head.Children.Add(champIcon);
            var title = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            title.Children.Add(new TextBlock
            {
                Text = view.Surprise ? $"SURPRISE · {(view.Locked ? view.Source : view.ChampionName)}".ToUpperInvariant() : $"RUNES · {view.ChampionName}{(view.Position is { } p ? " " + p : "")}".ToUpperInvariant(),
                FontSize = 10, FontWeight = FontWeights.Bold, Foreground = view.Surprise ? Theme.PinkBrush : Accent,
            });
            title.Children.Add(new TextBlock
            {
                Text = (view.Surprise ? (view.Locked ? "Runes from surprise mode: they fit the build" : "Lock in to roll your surprise build")
                    : view.RuneOrigin == RuneOrigin.Standard ? $"Standard {view.RuneSource.ToLowerInvariant()} page (no collected games yet)" : view.Source)
                    + (view.Locked ? "" : " · hovering"),
                FontSize = 11, Foreground = Muted, TextWrapping = TextWrapping.Wrap,
            });
            head.Children.Add(title);
            ChampSelectPanel.Children.Add(head);

            if (view.Runes is { } page && _runes.IsLoaded)
                ChampSelectPanel.Children.Add(RunePanel.Page(page, _runes, _icons, Muted));
            else if (!view.Surprise || view.Locked)
                ChampSelectPanel.Children.Add(new TextBlock
                {
                    Text = "No rune data for this champion yet. Rune suggestions come from collected high-ranked games (Riot API key in Settings) or an imported builds file.",
                    FontSize = 11, Foreground = Muted, TextWrapping = TextWrapping.Wrap,
                });

            if (view.Starter.Count > 0)
            {
                ChampSelectPanel.Children.Add(new TextBlock { Text = "START", FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Muted, Margin = new Thickness(0, 10, 0, 4) });
                var start = new WrapPanel();
                foreach (var g in view.Starter.GroupBy(id => id))
                {
                    var cell = new Grid { Margin = new Thickness(0, 0, 4, 0) };
                    cell.Children.Add(new Border
                    {
                        Width = 26, Height = 26, CornerRadius = new CornerRadius(3), ClipToBounds = true, Background = IconBackground,
                        ToolTip = _itemName?.Invoke(g.Key), Child = new Image { Source = _icons.Item(g.Key) },
                    });
                    if (g.Count() > 1)
                        cell.Children.Add(new TextBlock
                        {
                            Text = g.Count().ToString(), FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Text,
                            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 2, 0),
                        });
                    start.Children.Add(cell);
                }
                ChampSelectPanel.Children.Add(start);
            }

            if (view.Items.Count > 0)
            {
                ChampSelectPanel.Children.Add(new TextBlock { Text = view.Surprise ? "BUILD" : "USUAL FIRST ITEMS", FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Muted, Margin = new Thickness(0, 10, 0, 4) });
                var items = new WrapPanel();
                foreach (int id in view.Items)
                    items.Children.Add(new Border
                    {
                        Width = 26, Height = 26, CornerRadius = new CornerRadius(3), ClipToBounds = true, Margin = new Thickness(0, 0, 4, 0),
                        Background = IconBackground, ToolTip = _itemName?.Invoke(id), Child = new Image { Source = _icons.Item(id) },
                    });
                ChampSelectPanel.Children.Add(items);
            }
            ChampSelectPanel.Children.Add(new TextBlock
            {
                Text = view.Locked ? "The build is in the in-game shop (Secret OP item set). Apply the rune page from the Secret OP window."
                    : "Apply the rune page from the Secret OP window.", FontSize = 10.5, Foreground = Muted, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap,
            });
        }
        UpdateVisibility();
        if (ShowingChampSelect) KeepOnTop();
    }

    private void RenderGold(GoldSummary gold)
    {
        int lead = gold.EnemyLead;
        (GoldText.Text, GoldText.Foreground) = lead switch
        {
            > 0 => ($"Enemy +{lead:N0} g", Red),
            < 0 => ($"Your team +{-lead:N0} g", Green),
            _ => ("Gold even", Text),
        };
        GoldSub.Text = $"items {gold.MyTeamGold:N0} vs {gold.EnemyTeamGold:N0}";
    }

    private void RenderLanes(LaneSummary? lanes, Lane? myLane)
    {
        LaneGrid.Visibility = lanes is null ? Visibility.Collapsed : Visibility.Visible;
        if (lanes is null) return;

        foreach (var (lane, (cell, label, value, detail)) in _laneCells)
        {
            var lead = lanes.Get(lane);
            bool highlight = lanes.Highlight == lane;
            label.Text = lane == myLane ? $"{LaneLabel(lane)} • YOU" : LaneLabel(lane);
            label.Foreground = lane == myLane ? Text : Muted;

            if (lead is null)
            {
                value.Text = "—";
                value.Foreground = Muted;
                detail.Text = "";
            }
            else
            {
                value.Text = Short(lead.Score);
                value.Foreground = lead.Score > 0 ? Green : lead.Score < 0 ? Red : Text;
                detail.Text = $"{Short(lead.GoldDiff)}g {Signed(lead.LevelDiff)}lv";
            }

            cell.BorderBrush = highlight ? Accent : CellBorder;
            cell.BorderThickness = new Thickness(highlight ? 2 : 1);
            cell.Background = highlight ? Theme.Tint(Theme.Yellow, 0x26) : null;
        }
    }

    private void RenderStacks(ValueList<StackInfo>? stacks)
    {
        StacksPanel.Children.Clear();
        StacksPanel.Visibility = stacks is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        if (stacks is null) return;
        foreach (var s in stacks)
        {
            var title = new TextBlock { FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Accent, Text = s.Title.ToUpperInvariant() };
            var value = new TextBlock { TextWrapping = TextWrapping.Wrap };
            value.Inlines.Add(new System.Windows.Documents.Run(s.Value) { FontWeight = FontWeights.SemiBold, FontSize = 14 });
            value.Inlines.Add(new System.Windows.Documents.Run("  " + s.Detail) { Foreground = Muted, FontSize = 11 });
            var meaning = new TextBlock { Text = s.Meaning, FontSize = 10.5, Foreground = Muted, TextWrapping = TextWrapping.Wrap };
            var text = new StackPanel { Children = { title, value, meaning } };
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 4) };
            if (s.ItemId is { } id)
            {
                var icon = new Border
                {
                    Width = 26, Height = 26, CornerRadius = new CornerRadius(3), ClipToBounds = true, Margin = new Thickness(0, 2, 8, 0),
                    Background = IconBackground, VerticalAlignment = VerticalAlignment.Top, Child = new Image { Source = _icons.Item(id) },
                };
                DockPanel.SetDock(icon, Dock.Left);
                row.Children.Add(icon);
            }
            row.Children.Add(text);
            StacksPanel.Children.Add(row);
        }
    }

    private void RenderItems(ItemAdvice advice)
    {
        ItemsSection.Visibility = advice.IsEmpty ? Visibility.Collapsed : Visibility.Visible;
        if (advice.IsEmpty) return;

        ReminderText.Text = advice.Reminder ?? "";
        // Warnings (nothing bought at the start) in red, normal reminders in the accent colour.
        ReminderText.Foreground = advice.ReminderUrgent ? Red : Accent;
        ReminderBox.BorderBrush = advice.ReminderUrgent ? Red : Accent;
        ReminderBox.Background = advice.ReminderUrgent ? UrgentBack : ReminderBack;
        ReminderBox.Visibility = advice.Reminder is null ? Visibility.Collapsed : Visibility.Visible;
        ReminderIcon.Source = advice.ReminderItemId is { } rid ? _icons.Item(rid) : null;
        CorePanel.Visibility = advice.Core is null ? Visibility.Collapsed : Visibility.Visible;
        CoreIcon.Source = advice.Core is { } core ? _icons.Item(core.ItemId) : null;

        CoreName.Text = advice.Core?.Name ?? "—";
        CoreCost.Text = advice.Core is { } c ? $"{c.RemainingCost:N0} g left" : "";
        CoreReason.Text = advice.Core?.Reason ?? "";

        SituationalPanel.Children.Clear();
        foreach (var s in advice.Situational)
        {
            var line = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            line.Inlines.Add(new System.Windows.Documents.Run(s.Name) { FontWeight = FontWeights.SemiBold });
            line.Inlines.Add(new System.Windows.Documents.Run("  " + s.Reason) { Foreground = Muted, FontSize = 11 });
            var icon = new Border
            {
                Width = 22, Height = 22, CornerRadius = new CornerRadius(3), ClipToBounds = true, Margin = new Thickness(0, 0, 8, 0),
                Background = IconBackground, VerticalAlignment = VerticalAlignment.Top,
                Child = new Image { Source = _icons.Item(s.ItemId) },
            };
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            row.Children.Add(line);
            SituationalPanel.Children.Add(row);
        }

        BuyNowText.Text = advice.BuyNow ?? "";
        BuyNowText.Visibility = advice.BuyNow is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private static string LaneLabel(Lane lane) => lane switch
    {
        Lane.Top => "TOP", Lane.Jungle => "JG", Lane.Mid => "MID", _ => "BOT",
    };

    private static string Signed(int v) => v > 0 ? $"+{v}" : v.ToString();

    private static string Short(int v)
    {
        string sign = v > 0 ? "+" : v < 0 ? "-" : "";
        int a = Math.Abs(v);
        return a >= 1000 ? $"{sign}{a / 1000.0:0.0}k" : $"{sign}{a}";
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
