using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace Glowline.Services;

/// <summary>
/// Keyboard shortcuts that work even when Glowline isn't focused.
/// They use Ctrl+Alt+Shift to avoid clashing with other apps and with AltGr
/// (which equals Ctrl+Alt on Spanish and many other keyboard layouts).
/// </summary>
public sealed class GlobalHotkeyService : IDisposable
{
    public sealed record Hotkey(int Id, Key KeyCode, string LabelKey, Action Action)
    {
        public string Gesture => "Ctrl+Alt+Shift+" + KeyName(KeyCode);

        static string KeyName(Key key) => key switch
        {
            Key.Left => "←",
            Key.Right => "→",
            _ => key.ToString(),
        };
    }

    const int WmHotkey = 0x0312;
    const uint ModAlt = 0x0001;
    const uint ModControl = 0x0002;
    const uint ModShift = 0x0004;
    const uint ModNoRepeat = 0x4000;

    readonly List<Hotkey> _hotkeys = new();
    readonly HashSet<int> _registered = new();
    HwndSource? _source;

    public IReadOnlyList<Hotkey> Hotkeys => _hotkeys;

    public bool IsRegistered(Hotkey hotkey) => _registered.Contains(hotkey.Id);

    public GlobalHotkeyService()
    {
        _hotkeys.Add(new Hotkey(1, Key.L, "HotkeyOpen", () => App.ShowPrimaryWindow()));
        _hotkeys.Add(new Hotkey(2, Key.M, "HotkeyMini", MiniLyricsWindow.Toggle));
        _hotkeys.Add(new Hotkey(3, Key.Left, "HotkeyEarlier", () => App.Lyrics.AdjustGlobalOffset(-250)));
        _hotkeys.Add(new Hotkey(4, Key.Right, "HotkeyLater", () => App.Lyrics.AdjustGlobalOffset(250)));
        _hotkeys.Add(new Hotkey(5, Key.S, "HotkeyShare", () => App.Primary?.ShareLine()));
    }

    /// <summary>Registers or releases the shortcuts according to the setting.</summary>
    public void Apply(bool enabled)
    {
        if (enabled) Register();
        else Unregister();
    }

    void Register()
    {
        if (_source is null)
        {
            // Message-only window: receives WM_HOTKEY without ever being shown.
            var parameters = new HwndSourceParameters("GlowlineHotkeys")
            {
                ParentWindow = new IntPtr(-3), // HWND_MESSAGE
                WindowStyle = 0,
            };
            _source = new HwndSource(parameters);
            _source.AddHook(WndProc);
        }

        foreach (var hotkey in _hotkeys)
        {
            if (_registered.Contains(hotkey.Id)) continue;
            var vk = (uint)KeyInterop.VirtualKeyFromKey(hotkey.KeyCode);
            if (RegisterHotKey(_source.Handle, hotkey.Id, ModControl | ModAlt | ModShift | ModNoRepeat, vk))
            {
                _registered.Add(hotkey.Id);
            }
            else
            {
                // Another app already uses this combination: shown in Settings.
                Log.Write($"hotkey {hotkey.Gesture}: unavailable ({Marshal.GetLastWin32Error()})");
            }
        }
    }

    void Unregister()
    {
        if (_source is null) return;
        foreach (var id in _registered) UnregisterHotKey(_source.Handle, id);
        _registered.Clear();
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotkey) return IntPtr.Zero;
        var id = wParam.ToInt32();
        foreach (var hotkey in _hotkeys)
        {
            if (hotkey.Id != id) continue;
            handled = true;
            try
            {
                hotkey.Action();
            }
            catch (Exception ex)
            {
                Log.Write($"hotkey {hotkey.Gesture}: {ex.Message}");
            }
            break;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        _source?.RemoveHook(WndProc);
        _source?.Dispose();
        _source = null;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
