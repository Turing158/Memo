using Memo.Models;
using Memo.Views;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Memo.Platform.Windows;

public sealed class GlobalHotkeyService : IDisposable {
    private const int WmHotkey = 0x0312;
    private readonly HotkeyWindow _window = new();
    private readonly Dictionary<int, Action> _actions = new();
    private int _nextId = 1;

    public GlobalHotkeyService() {
        _window.HotkeyPressed += id => {
            if (_actions.TryGetValue(id, out var action)) action();
        };
    }

    public void Apply(AppSettings settings, MainWindow mainWindow) {
        UnregisterAll();
        Register(settings.ToggleTopmostHotkey, () => mainWindow.TogglePinned());
        Register(settings.MinimizeHotkey, () => mainWindow.HideToTrayWithTransition());
        Register(settings.ShowWindowHotkey, () => mainWindow.ShowWithTransition());
    }

    private void Register(HotkeySetting hotkey, Action action) {
        if (hotkey.IsEmpty || !TryMapKey(hotkey.Key, out var key)) return;

        var modifiers = 0u;
        if (hotkey.Alt) modifiers |= 0x0001;
        if (hotkey.Ctrl) modifiers |= 0x0002;
        if (hotkey.Shift) modifiers |= 0x0004;
        if (hotkey.Win) modifiers |= 0x0008;

        var id = _nextId++;
        if (RegisterHotKey(_window.Handle, id, modifiers, (uint)key)) {
            _actions[id] = action;
        }
    }

    private static bool TryMapKey(string value, out Keys key) {
        key = Keys.None;
        if (string.IsNullOrWhiteSpace(value)) return false;

        if (value.Length == 1 && char.IsDigit(value[0])) {
            key = Keys.D0 + (value[0] - '0');
            return true;
        }

        return Enum.TryParse(value, ignoreCase: true, out key) && key != Keys.None;
    }

    private void UnregisterAll() {
        foreach (var id in _actions.Keys)
        {
            UnregisterHotKey(_window.Handle, id);
        }
        _actions.Clear();
    }

    public void Dispose() {
        UnregisterAll();
        _window.DestroyHandle();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private sealed class HotkeyWindow : NativeWindow {
        public event Action<int>? HotkeyPressed;

        public HotkeyWindow() {
            CreateHandle(new CreateParams());
        }

        protected override void WndProc(ref Message m) {
            if (m.Msg == WmHotkey) {
                HotkeyPressed?.Invoke(m.WParam.ToInt32());
                return;
            }
            base.WndProc(ref m);
        }
    }
}
