using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;
using LeagueOverlay.Core.Builds;

namespace LeagueOverlay;

/// <summary>
/// Edit hotkeys, lane level weight, refresh rate and the Riot API key / region for popular builds.
/// Hotkeys are captured by pressing them in a box.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly Settings _settings;
    private readonly Dictionary<TextBox, Hotkey> _keys = new();
    private readonly Action<double, double>? _preview;
    private readonly Func<string, string>? _exportBuilds;
    private readonly Func<string, Task<string>>? _importBuilds;

    /// <summary>True after Save if the API key or region changed (popular-build collection must restart).</summary>
    public bool BuildSettingsChanged { get; private set; }

    /// <summary>True after Save if your own Riot ID changed.</summary>
    public bool MyRiotIdChanged { get; private set; }

    /// <summary>True after Save if the rival changed.</summary>
    public bool RivalChanged { get; private set; }

    /// <param name="preview">Called with (background, whole overlay) opacity while the sliders move.</param>
    public SettingsWindow(Settings settings, Action<double, double>? preview = null,
        Func<string, string>? exportBuilds = null, Func<string, Task<string>>? importBuilds = null)
    {
        _settings = settings;
        _exportBuilds = exportBuilds;
        _importBuilds = importBuilds;
        InitializeComponent();
        SetOpacitySliders(settings.BackgroundOpacity, settings.OverlayOpacity);
        _preview = preview; // set after the sliders so loading them doesn't count as a change
        SourceInitialized += (_, _) => Native.UseDarkTitleBar(this);

        Load(settings.ToggleOverlay, settings.MoveOverlay, settings.Quit, settings.LevelWeight, settings.PollSeconds);

        if (LeagueOverlay.Core.History.RiotIds.Split(settings.MyRiotId) is { } myId)
        {
            MyNameBox.Text = myId.Name;
            MyTagBox.Text = myId.Tag;
        }
        if (LeagueOverlay.Core.History.RiotIds.Split(settings.Rival) is { } rivalId)
        {
            RivalNameBox.Text = rivalId.Name;
            RivalTagBox.Text = rivalId.Tag;
        }
        RegionBox.ItemsSource = RiotRegion.All;
        RegionBox.SelectedItem = RiotRegion.Find(settings.Region);
        KeyBox.Password = settings.ApiKey ?? "";
        if (Settings.KeyFromEnvironment is not null)
        {
            KeyBox.IsEnabled = false;
            KeyResult.Text = "Using the key from the RIOT_API_KEY environment variable.";
        }
        foreach (var box in new[] { ToggleBox, MoveBox, QuitBox })
        {
            box.PreviewKeyDown += CaptureKey;
            box.GotKeyboardFocus += (_, _) => box.Text = "Press new keys…";
            box.LostKeyboardFocus += (_, _) => box.Text = _keys[box].ToString();
        }
    }

    private void SetOpacitySliders(double background, double overlay)
    {
        BackgroundSlider.Value = Math.Round(background * 100);
        OverlaySlider.Value = Math.Round(overlay * 100);
        UpdateOpacityLabels();
    }

    private void UpdateOpacityLabels()
    {
        BackgroundValue.Text = $"{BackgroundSlider.Value:0}%";
        OverlayValue.Text = $"{OverlaySlider.Value:0}%";
    }

    private void Opacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BackgroundValue is null || OverlayValue is null) return; // still loading
        UpdateOpacityLabels();
        _preview?.Invoke(BackgroundSlider.Value / 100, OverlaySlider.Value / 100);
    }

    private void Load(Hotkey toggle, Hotkey move, Hotkey quit, int levelWeight, double pollSeconds)
    {
        _keys[ToggleBox] = toggle;
        _keys[MoveBox] = move;
        _keys[QuitBox] = quit;
        foreach (var (box, key) in _keys) box.Text = box.IsKeyboardFocused ? "Press new keys…" : key.ToString();
        LevelWeightBox.Text = levelWeight.ToString(CultureInfo.CurrentCulture);
        PollBox.Text = pollSeconds.ToString(CultureInfo.CurrentCulture);
        ShowError(null);
    }

    private void CaptureKey(object sender, KeyEventArgs e)
    {
        var box = (TextBox)sender;
        var key = e.Key == Key.System ? e.SystemKey : e.Key; // Alt combinations arrive as Key.System
        if (key == Key.Tab && Keyboard.Modifiers == ModifierKeys.None) return; // let Tab move between fields
        e.Handled = true;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt
            or Key.LWin or Key.RWin or Key.ImeProcessed or Key.DeadCharProcessed or Key.None)
            return; // wait for the actual key

        var hotkey = new Hotkey(Keyboard.Modifiers, key);
        _keys[box] = hotkey;
        box.Text = hotkey.ToString();
        ShowError(ValidateKeys(checkAvailability: false));
    }

    private string? ValidateKeys(bool checkAvailability)
    {
        var names = new Dictionary<TextBox, string> { [ToggleBox] = "Show / hide", [MoveBox] = "Move overlay", [QuitBox] = "Stop app" };
        foreach (var (box, key) in _keys)
            if (!key.IsSafe) return $"{names[box]}: {key} would fire while typing. Add Ctrl or Alt, or use F1–F24.";
        var duplicate = _keys.GroupBy(k => k.Value).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null) return $"{duplicate.Key} is used for more than one action.";
        if (checkAvailability)
        {
            // Our own hotkeys are released while this window is open, so a failure means another program owns it.
            foreach (var (box, key) in _keys)
                if (!key.IsAvailable()) return $"{names[box]}: {key} is already used by another program.";
        }
        return null;
    }

    private static bool TryParseNumber(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var error = ValidateKeys(checkAvailability: true);
        if (error is null && (!TryParseNumber(LevelWeightBox.Text, out var weight) || weight is < 0 or > 2000))
            error = "Gold value of one level must be a number from 0 to 2000.";
        if (error is null && (!TryParseNumber(PollBox.Text, out var poll) || poll is < 0.5 or > 30))
            error = "Refresh interval must be between 0.5 and 30 seconds.";
        string rivalName = RivalNameBox.Text.Trim(), rivalTag = RivalTagBox.Text.Trim().TrimStart('#').Trim();
        if (error is null && (rivalName.Length == 0) != (rivalTag.Length == 0))
            error = rivalName.Length == 0 ? "Rival: fill in their name as well as the tag." : "Rival: fill in their tag too (the part after # in their Riot ID, e.g. EUW).";
        string rival = rivalName.Length == 0 ? "" : $"{rivalName}#{rivalTag}";
        string myName = MyNameBox.Text.Trim(), myTag = MyTagBox.Text.Trim().TrimStart('#').Trim();
        if (error is null && (myName.Length == 0) != (myTag.Length == 0))
            error = "Your Riot ID: fill in both the name and the tag.";
        if (error is not null) { ShowError(error); return; }

        TryParseNumber(LevelWeightBox.Text, out weight);
        TryParseNumber(PollBox.Text, out poll);
        _settings.ToggleOverlayHotkey = _keys[ToggleBox].ToString();
        _settings.MoveOverlayHotkey = _keys[MoveBox].ToString();
        _settings.QuitHotkey = _keys[QuitBox].ToString();
        _settings.LevelWeight = (int)Math.Round(weight);
        _settings.PollSeconds = poll;
        RivalChanged = !LeagueOverlay.Core.History.RiotIds.Same(rival, _settings.Rival ?? "") && !(rival.Length == 0 && _settings.Rival is null);
        _settings.Rival = rival.Length == 0 ? null : rival;
        string? me = myName.Length == 0 ? null : $"{myName}#{myTag}";
        MyRiotIdChanged = !LeagueOverlay.Core.History.RiotIds.Same(me ?? "", _settings.MyRiotId ?? "");
        _settings.MyRiotId = me;
        _settings.BackgroundOpacity = BackgroundSlider.Value / 100;
        _settings.OverlayOpacity = OverlaySlider.Value / 100;

        var region = (RegionBox.SelectedItem as RiotRegion ?? RiotRegion.Default).Platform;
        string? key = string.IsNullOrWhiteSpace(KeyBox.Password) ? null : KeyBox.Password.Trim();
        BuildSettingsChanged = region != _settings.Region || (Settings.KeyFromEnvironment is null && key != _settings.ApiKey);
        _settings.Region = region;
        if (Settings.KeyFromEnvironment is null) _settings.ApiKey = key;
        _settings.Save();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void ExportBuilds_Click(object sender, RoutedEventArgs e)
    {
        if (_exportBuilds is null) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"LeagueOverlay-builds-{DateTime.Now:yyyy-MM-dd}{LeagueOverlay.Core.Builds.BuildShare.Extension}",
            Filter = $"League Overlay builds|*{LeagueOverlay.Core.Builds.BuildShare.Extension}",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog(this) != true) return;
        BuildsShareResult.Text = _exportBuilds(dialog.FileName);
    }

    private async void ImportBuilds_Click(object sender, RoutedEventArgs e)
    {
        if (_importBuilds is null) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = $"League Overlay builds|*{LeagueOverlay.Core.Builds.BuildShare.Extension}",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "\\Downloads",
        };
        if (dialog.ShowDialog(this) != true) return;
        ImportBuildsButton.IsEnabled = ExportBuildsButton.IsEnabled = false;
        BuildsShareResult.Text = "Importing…";
        try { BuildsShareResult.Text = await _importBuilds(dialog.FileName); }
        finally { ImportBuildsButton.IsEnabled = ExportBuildsButton.IsEnabled = true; }
    }

    private void MyName_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (LeagueOverlay.Core.History.RiotIds.Split(MyNameBox.Text) is not { } id) return;
        MyNameBox.Text = id.Name;
        MyTagBox.Text = id.Tag;
        MyNameBox.CaretIndex = MyNameBox.Text.Length;
    }

    /// <summary>A full "Name#Tag" pasted into the name box is split into the two boxes.</summary>
    private void RivalName_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (LeagueOverlay.Core.History.RiotIds.Split(RivalNameBox.Text) is not { } id) return;
        RivalNameBox.Text = id.Name;
        RivalTagBox.Text = id.Tag;
        RivalNameBox.CaretIndex = RivalNameBox.Text.Length;
    }

    private async void TestKey_Click(object sender, RoutedEventArgs e)
    {
        var key = KeyBox.Password.Trim();
        if (key.Length == 0) { KeyResult.Text = "Paste a key first."; return; }
        var region = RegionBox.SelectedItem as RiotRegion ?? RiotRegion.Default;
        TestKeyButton.IsEnabled = false;
        KeyResult.Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush");
        KeyResult.Text = "Checking…";
        bool ok = false;
        try
        {
            using var client = new RiotClient(key, region);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var _ = await client.PlatformStatusAsync(cts.Token);
            ok = true;
            KeyResult.Text = $"Key works for {region.Label}.";
        }
        catch (RiotKeyException) { KeyResult.Text = "Riot rejected this key. Development keys expire after 24 hours: generate a new one."; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { KeyResult.Text = "Couldn't reach Riot. Check your connection and try again."; }
        finally
        {
            TestKeyButton.IsEnabled = true;
            KeyResult.Foreground = (System.Windows.Media.Brush)FindResource(ok ? "OkBrush" : "ErrBrush");
        }
    }

    private void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        Load(Hotkey.Parse(Settings.DefaultToggleHotkey, default), Hotkey.Parse(Settings.DefaultMoveHotkey, default),
             Hotkey.Parse(Settings.DefaultQuitHotkey, default), 350, 2);
        SetOpacitySliders(Settings.DefaultBackgroundOpacity, 1); // the slider events update the preview
    }

    private void ShowError(string? message)
    {
        ErrorText.Text = message ?? "";
        ErrorText.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }
}
