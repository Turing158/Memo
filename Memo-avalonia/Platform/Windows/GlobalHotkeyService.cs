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

    public event Action? RestoreRequested;

    public GlobalHotkeyService() {
        _window.HotkeyPressed += id => {
            if (_actions.TryGetValue(id, out var action)) action();
        };
        _window.RestoreRequested += () => RestoreRequested?.Invoke();
    }

    public void Apply(AppSettings settings, MainWindow mainWindow, Action toggleTopmostTarget, Action quickMemoFromClipboard) {
        UnregisterAll();
        TryRegister(settings.ToggleTopmostHotkey, toggleTopmostTarget);
        TryRegister(settings.MinimizeHotkey, () => mainWindow.HideToTrayWithTransition());
        TryRegister(settings.ShowWindowHotkey, () => mainWindow.ShowWithTransition());
        if (settings.QuickMemoEnabled) {
            TryRegister(settings.QuickMemoHotkey, quickMemoFromClipboard);
        }
    }

    // 单个按键注册：失败（被系统/其它程序占用）时静默跳过，不影响其它按键。
    private void TryRegister(HotkeySetting hotkey, Action action) {
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

        if (TryMapNamedKey(value, out key)) return true;

        return Enum.TryParse(value, ignoreCase: true, out key) && key != Keys.None;
    }

    private static bool TryMapNamedKey(string value, out Keys key) {
        key = value switch {
            "Ctrl" => Keys.ControlKey,
            "Alt" => Keys.Menu,
            "Shift" => Keys.ShiftKey,
            "Win" => Keys.LWin,
            "Tab" => Keys.Tab,
            "CapsLock" => Keys.CapsLock,
            "Space" => Keys.Space,
            "`" => Keys.Oemtilde,
            "-" => Keys.OemMinus,
            "=" => Keys.Oemplus,
            "[" => Keys.OemOpenBrackets,
            "]" => Keys.OemCloseBrackets,
            "\\" => Keys.OemPipe,
            ";" => Keys.OemSemicolon,
            "'" => Keys.OemQuotes,
            "," => Keys.Oemcomma,
            "." => Keys.OemPeriod,
            "/" => Keys.OemQuestion,
            "NumPad+" => Keys.Add,
            "NumPad-" => Keys.Subtract,
            "NumPad*" => Keys.Multiply,
            "NumPad/" => Keys.Divide,
            "NumPad." => Keys.Decimal,
            _ => Keys.None,
        };
        return key != Keys.None;
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
        public event Action? RestoreRequested;

        private readonly uint _restoreMessageId;

        public HotkeyWindow() {
            _restoreMessageId = SingleInstance.GetRestoreMessageId();
            CreateHandle(new CreateParams());
        }

        protected override void WndProc(ref Message m) {
            if (m.Msg == WmHotkey) {
                HotkeyPressed?.Invoke(m.WParam.ToInt32());
                return;
            }
            if (_restoreMessageId != 0 && m.Msg == _restoreMessageId) {
                RestoreRequested?.Invoke();
                return;
            }
            base.WndProc(ref m);
        }
    }
}
