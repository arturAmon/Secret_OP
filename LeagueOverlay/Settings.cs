using LeagueOverlay.Core.Analysis;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LeagueOverlay;

/// <summary>Champion suggestions in champion select (see Settings.ChampionPickMode).</summary>
public enum PickModeSetting { Off, Best, Surprise }

/// <summary>User settings, stored in %LOCALAPPDATA%\LeagueOverlay\settings.json (editable by hand).</summary>
public sealed class Settings
{
    /// <summary>Overlay position in device-independent pixels; null = default (top right).</summary>
    public double? Left { get; set; }
    public double? Top { get; set; }

    /// <summary>Gold value of one level when comparing lanes.</summary>
    public int LevelWeight { get; set; } = 350;

    /// <summary>Seconds between reads of the game client while in a game / while waiting for one.</summary>
    public double PollSeconds { get; set; } = 2;
    public double IdlePollSeconds { get; set; } = 5;

    /// <summary>Opacity of the overlay's dark background (0 = none, 1 = solid). Text and icons stay as they are.</summary>
    public double BackgroundOpacity { get; set; } = DefaultBackgroundOpacity;
    /// <summary>Opacity of the whole overlay, text and icons included.</summary>
    public double OverlayOpacity { get; set; } = 1;
    public const double DefaultBackgroundOpacity = 0.92, MinOverlayOpacity = 0.2;

    public const string DefaultToggleHotkey = "Ctrl+Shift+O";

    /// <summary>Global hotkeys, e.g. "Ctrl+Shift+O". Changed from the Settings window.</summary>
    public string ToggleOverlayHotkey { get; set; } = DefaultToggleHotkey;

    [JsonIgnore] public Hotkey ToggleOverlay => Hotkey.Parse(ToggleOverlayHotkey, Hotkey.Parse(DefaultToggleHotkey, default));

    /// <summary>Surprise build mode: unexpected builds instead of the usual suggestions.</summary>
    public bool SurpriseMode { get; set; }

    /// <summary>Look for a newer version on GitHub at startup and every few hours.</summary>
    public bool CheckForUpdates { get; set; } = true;

    // Popular builds shared through GitHub (see SharedBuilds): download them / upload yours (only with gh push access).
    public bool SharedBuildsDownload { get; set; } = true;
    public bool SharedBuildsUpload { get; set; } = true;
    /// <summary>The sha256 of the shared file last imported or uploaded, so the same file isn't imported again.</summary>
    public string? SharedBuildsDigest { get; set; }
    public DateTime? SharedBuildsDownloadedAt { get; set; }
    public int SharedBuildsDownloadedGames { get; set; }
    public DateTime? SharedBuildsUploadedAt { get; set; }
    /// <summary>How many games the saved builds had at the last upload (the next one waits for enough new ones).</summary>
    public int SharedBuildsUploadedCount { get; set; }

    /// <summary>Champion suggestions in champion select: "Off", "Best" (fits the team) or "Surprise" (low mastery, random).</summary>
    public string ChampionPickMode { get; set; } = "Best";
    [JsonIgnore] public PickModeSetting PickMode => Enum.TryParse<PickModeSetting>(ChampionPickMode, out var m) ? m : PickModeSetting.Best;

    /// <summary>"Surprise me" champion picks: include champions you play a lot / every champion equally likely /
    /// only champions played in your role / skip clearly losing lane matchups.</summary>
    public bool SurprisePickAnyMastery { get; set; }
    public bool SurprisePickEven { get; set; }
    public bool SurprisePickOnlyMyRole { get; set; }
    public bool SurprisePickAvoidLosingLanes { get; set; } = true;
    [JsonIgnore] public SurprisePickOptions SurprisePicks => new(SurprisePickAnyMastery, SurprisePickEven, SurprisePickOnlyMyRole, SurprisePickAvoidLosingLanes);

    /// <summary>Champion + surprise style combinations not to suggest again ("jinx:ap"), see SurpriseBuilder.BlockKey.</summary>
    public List<string> SurpriseBlocked { get; set; } = new();

    /// <summary>When applying a rune page and the client has no free page, replace the current page.</summary>
    public bool RunesReplaceCurrent { get; set; }

    /// <summary>Your own Riot ID ("Name#Tag"), for pulling your recent games. Filled in automatically from the first game the app sees.</summary>
    public string? MyRiotId { get; set; }

    /// <summary>Riot ID ("Name#Tag") of the friend you compare stats with in games you play together.</summary>
    public string? Rival { get; set; }

    /// <summary>Riot server region for popular builds (platform id, e.g. "EUW1").</summary>
    public string Region { get; set; } = "EUW1";

    /// <summary>
    /// The Riot API key, encrypted for this Windows account (DPAPI), so a copied settings file doesn't leak it
    /// and every person who gets the app uses their own key.
    /// </summary>
    public string? ApiKeyProtected { get; set; }

    /// <summary>For development: RIOT_API_KEY overrides the saved key without storing it.</summary>
    public static string? KeyFromEnvironment => Environment.GetEnvironmentVariable("RIOT_API_KEY") is { Length: > 0 } k ? k.Trim() : null;

    [JsonIgnore]
    public string? ApiKey
    {
        get
        {
            if (KeyFromEnvironment is { } env) return env;
            if (string.IsNullOrEmpty(ApiKeyProtected)) return null;
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(ApiKeyProtected), Entropy, DataProtectionScope.CurrentUser)); }
            catch (Exception e) when (e is CryptographicException or FormatException) { return null; } // saved by another Windows user
        }
        set => ApiKeyProtected = string.IsNullOrWhiteSpace(value)
            ? null
            : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value.Trim()), Entropy, DataProtectionScope.CurrentUser));
    }

    private static readonly byte[] Entropy = "LeagueOverlay.RiotApiKey"u8.ToArray();

    public static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LeagueOverlay", "settings.json");

    // Relaxed escaping keeps "Ctrl+Shift+O" readable in the file instead of "Ctrl+Shift+O".
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static Settings Load()
    {
        Settings s;
        try { s = File.Exists(FilePath) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { s = new(); }

        s.PollSeconds = Math.Clamp(s.PollSeconds, 0.5, 30);
        s.IdlePollSeconds = Math.Clamp(s.IdlePollSeconds, 1, 60);
        s.LevelWeight = Math.Clamp(s.LevelWeight, 0, 2000);
        s.BackgroundOpacity = Math.Clamp(s.BackgroundOpacity, 0, 1);
        s.OverlayOpacity = Math.Clamp(s.OverlayOpacity, MinOverlayOpacity, 1);
        if (!File.Exists(FilePath)) s.Save(); // create it so it's easy to find and edit
        return s;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* non-fatal */ }
    }
}
