using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Threading;

namespace LeagueOverlay;

/// <summary>
/// Global hotkeys through a low-level keyboard hook (WH_KEYBOARD_LL).
/// RegisterHotKey doesn't fire while League has focus: games that read the keyboard as raw input with
/// RIDEV_NOHOTKEYS switch off registered hotkeys. A low-level hook sees each key before the game does.
/// Keys are only observed, never swallowed, so the game still gets them.
/// </summary>
internal sealed unsafe class KeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    private const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public nuint dwExtraInfo; }

    private static KeyboardHook? _instance; // the hook callback is static (UnmanagedCallersOnly)

    private readonly Dispatcher _dispatcher;
    private readonly List<(Hotkey Key, Action Action)> _bindings = new();
    private nint _hook;
    private uint _heldVk; // the key that last fired, until released, so auto-repeat doesn't fire it again

    /// <summary>Off while the Settings window is capturing new keys.</summary>
    public bool Enabled { get; set; } = true;

    public KeyboardHook()
    {
        _dispatcher = Dispatcher.CurrentDispatcher; // the hook runs on this thread's message loop
        _instance = this;
        _hook = Native.SetWindowsHookEx(WH_KEYBOARD_LL, &HookProc, Native.GetModuleHandle(null), 0);
        if (_hook == 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not install the keyboard hook");
    }

    public void SetBindings(IEnumerable<(Hotkey Key, Action Action)> bindings)
    {
        _bindings.Clear();
        _bindings.AddRange(bindings);
    }

    public void Dispose()
    {
        if (_hook != 0) Native.UnhookWindowsHookEx(_hook);
        _hook = 0;
        if (_instance == this) _instance = null;
    }

    [UnmanagedCallersOnly]
    private static nint HookProc(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && _instance is { } hook)
        {
            try { hook.OnKey((int)wParam, ((KBDLLHOOKSTRUCT*)lParam)->vkCode); }
            catch { /* never let an exception cross into Windows */ }
        }
        return Native.CallNextHookEx(0, code, wParam, lParam);
    }

    // Must return quickly: Windows drops hooks that keep it waiting. Actions run later on the dispatcher.
    private void OnKey(int message, uint vk)
    {
        if (message is WM_KEYUP or WM_SYSKEYUP)
        {
            if (vk == _heldVk) _heldVk = 0;
            return;
        }
        if (message is not (WM_KEYDOWN or WM_SYSKEYDOWN) || !Enabled || vk == _heldVk) return;

        var mods = ModifierKeys.None;
        if (Down(VK_CONTROL)) mods |= ModifierKeys.Control;
        if (Down(VK_MENU)) mods |= ModifierKeys.Alt;
        if (Down(VK_SHIFT)) mods |= ModifierKeys.Shift;
        if (Down(VK_LWIN) || Down(VK_RWIN)) mods |= ModifierKeys.Windows;

        foreach (var (key, action) in _bindings)
        {
            if (key.VirtualKey != vk || key.Modifiers != mods) continue;
            _heldVk = vk;
            _dispatcher.BeginInvoke(action);
            return;
        }
    }

    private static bool Down(int vk) => (Native.GetAsyncKeyState(vk) & 0x8000) != 0;
}
