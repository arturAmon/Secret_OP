using System.Windows.Input;

namespace LeagueOverlay;

/// <summary>A global key combination such as "Ctrl+Shift+O", stored as text in settings.json.</summary>
public readonly record struct Hotkey(ModifierKeys Modifiers, Key Key)
{
    public uint NativeModifiers =>
        (Modifiers.HasFlag(ModifierKeys.Alt) ? 0x1u : 0) |
        (Modifiers.HasFlag(ModifierKeys.Control) ? 0x2u : 0) |
        (Modifiers.HasFlag(ModifierKeys.Shift) ? 0x4u : 0) |
        (Modifiers.HasFlag(ModifierKeys.Windows) ? 0x8u : 0);

    public uint VirtualKey => (uint)KeyInterop.VirtualKeyFromKey(Key);

    public bool IsFunctionKey => Key is >= Key.F1 and <= Key.F24;

    /// <summary>
    /// Plain keys (or Shift+key) would fire while typing in game chat, so a hotkey needs Ctrl or Alt,
    /// unless it's a function key.
    /// </summary>
    public bool IsSafe => Modifiers.HasFlag(ModifierKeys.Control) || Modifiers.HasFlag(ModifierKeys.Alt) || IsFunctionKey;

    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(Key switch
        {
            >= Key.D0 and <= Key.D9 => ((int)(Key - Key.D0)).ToString(),
            >= Key.NumPad0 and <= Key.NumPad9 => "Num" + (int)(Key - Key.NumPad0),
            _ => Key.ToString(),
        });
        return string.Join("+", parts);
    }

    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var mods = ModifierKeys.None;
        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;
        foreach (var p in parts[..^1])
        {
            switch (p.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= ModifierKeys.Control; break;
                case "alt": mods |= ModifierKeys.Alt; break;
                case "shift": mods |= ModifierKeys.Shift; break;
                case "win" or "windows": mods |= ModifierKeys.Windows; break;
                default: return false;
            }
        }
        var last = parts[^1];
        Key key;
        if (last.Length == 1 && char.IsDigit(last[0])) key = Key.D0 + (last[0] - '0');
        else if (last.StartsWith("Num", StringComparison.OrdinalIgnoreCase) && last.Length == 4 && char.IsDigit(last[3])) key = Key.NumPad0 + (last[3] - '0');
        else if (!Enum.TryParse(last, ignoreCase: true, out key)) return false;
        if (key == Key.None) return false;
        hotkey = new Hotkey(mods, key);
        return true;
    }

    public static Hotkey Parse(string? text, Hotkey fallback) => TryParse(text, out var h) ? h : fallback;

    /// <summary>True if no other program has claimed this combination (checked by registering it briefly).</summary>
    public bool IsAvailable()
    {
        const int probeId = 0xBFFF;
        if (!Native.RegisterHotKey(0, probeId, NativeModifiers | Native.MOD_NOREPEAT, VirtualKey)) return false;
        Native.UnregisterHotKey(0, probeId);
        return true;
    }
}
