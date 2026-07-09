using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Memo.Components;
using Memo.Components.Dialogs;
using Memo.Models;
using Memo.UI;
using System;
using System.Threading.Tasks;

namespace Memo.Views;

public partial class SettingsWindow : Window {
    private AppSettings _settings = AppSettings.CreateDefault();
    private Func<AppSettings, Task> _saveSettingsAsync = _ => Task.CompletedTask;
    private HotkeySetting? _capturingHotkey;
    private Button? _capturingButton;
    private WindowTransitionController? _transition;
    private bool _isClosingAfterTransition;

    public SettingsWindow() {
        InitializeComponent();
        _transition = new WindowTransitionController(this, this.FindControl<Border>("_settingsShell")!);
        _transition.PrepareOpen();
        Opened += (_, _) => _transition.PlayOpen();
        KeyDown += OnWindowKeyDown;
        ApplySettingsToUi();

        var trayClickToggleInit = this.FindControl<LabeledToggleSwitch>("_trayClickToggle");
        if (trayClickToggleInit != null) {
            trayClickToggleInit.ValueChanged += v => _settings.TraySingleClickToShow = !v;
        }
    }

    public SettingsWindow(AppSettings settings, Func<AppSettings, Task> saveSettingsAsync)
        : this() {
        _settings = settings.Clone();
        _saveSettingsAsync = saveSettingsAsync;
        ApplySettingsToUi();
    }

    private void ApplySettingsToUi() {
        var minimizeOption = this.FindControl<ToggleButton>("_minimizeToTrayOption")!;
        var closeOption = this.FindControl<ToggleButton>("_closeAppOption")!;
        minimizeOption.IsChecked = _settings.CloseButtonAction == CloseButtonAction.MinimizeToTray;
        closeOption.IsChecked = _settings.CloseButtonAction == CloseButtonAction.Close;

        var enabledCheckBox = this.FindControl<CheckBox>("_quickMemoEnabledCheckBox");
        if (enabledCheckBox != null) {
            enabledCheckBox.IsChecked = _settings.QuickMemoEnabled;
        }

        var duplicateCheckBox = this.FindControl<CheckBox>("_duplicateMemoCheckBox");
        if (duplicateCheckBox != null) {
            duplicateCheckBox.IsChecked = _settings.DuplicateMemoEnabled;
        }

        var trayClickToggle = this.FindControl<LabeledToggleSwitch>("_trayClickToggle");
        if (trayClickToggle != null) {
            trayClickToggle.Value = !_settings.TraySingleClickToShow;
        }

        UpdateHotkeyButtons();
        UpdateQuickMemoButtonState();
    }

    private void UpdateSettingsFromUi() {
        var closeOption = this.FindControl<ToggleButton>("_closeAppOption")!;
        _settings.CloseButtonAction = closeOption.IsChecked == true
            ? CloseButtonAction.Close
            : CloseButtonAction.MinimizeToTray;
        _settings.HasAskedCloseButtonAction = true;
    }

    private void UpdateHotkeyButtons() {
        this.FindControl<Button>("_toggleTopmostHotkeyButton")!.Content = _settings.ToggleTopmostHotkey.ToString();
        this.FindControl<Button>("_minimizeHotkeyButton")!.Content = _settings.MinimizeHotkey.ToString();
        this.FindControl<Button>("_showWindowHotkeyButton")!.Content = _settings.ShowWindowHotkey.ToString();
        var quickMemoButton = this.FindControl<Button>("_quickMemoHotkeyButton");
        if (quickMemoButton != null) {
            quickMemoButton.Content = _settings.QuickMemoHotkey.ToString();
        }
    }

    private void UpdateQuickMemoButtonState() {
        var button = this.FindControl<Button>("_quickMemoHotkeyButton");
        if (button == null) return;

        button.IsEnabled = _settings.QuickMemoEnabled;
        button.Opacity = _settings.QuickMemoEnabled ? 1.0 : 0.45;
    }

    private void StartCapture(HotkeySetting hotkey, Button button) {
        _capturingHotkey = hotkey;
        _capturingButton = button;
        button.Content = "按下快捷键...";
        ClearHotkeyValidation();
        Focus();
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e) {
        if (_capturingHotkey == null || _capturingButton == null) return;

        e.Handled = true;
        if (e.Key == Key.Escape) {
            ClearHotkey(_capturingHotkey);
            ClearHotkeyValidation();
            EndCapture();
            return;
        }

        var key = NormalizeKey(e.Key);
        if (key == null) return;

        var candidate = new HotkeySetting {
            Key = key,
            Ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) && key != "Ctrl",
            Alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt) && key != "Alt",
            Shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift) && key != "Shift",
            Win = e.KeyModifiers.HasFlag(KeyModifiers.Meta) && key != "Win",
        };

        if (!ValidateHotkey(candidate, out var error)) {
            ShowHotkeyValidation(error);
            EndCapture();
            return;
        }

        _capturingHotkey.Key = candidate.Key;
        _capturingHotkey.Ctrl = candidate.Ctrl;
        _capturingHotkey.Alt = candidate.Alt;
        _capturingHotkey.Shift = candidate.Shift;
        _capturingHotkey.Win = candidate.Win;
        ClearHotkeyValidation();
        EndCapture();
    }

    private void EndCapture() {
        _capturingHotkey = null;
        _capturingButton = null;
        UpdateHotkeyButtons();
    }

    private static void ClearHotkey(HotkeySetting hotkey) {
        hotkey.Key = string.Empty;
        hotkey.Ctrl = false;
        hotkey.Alt = false;
        hotkey.Shift = false;
        hotkey.Win = false;
    }

    private void ShowHotkeyValidation(string message) {
        var text = this.FindControl<TextBlock>("_hotkeyValidationText");
        if (text == null) return;

        text.Text = message;
        text.IsVisible = true;
    }

    private void ClearHotkeyValidation() {
        var text = this.FindControl<TextBlock>("_hotkeyValidationText");
        if (text == null) return;

        text.Text = string.Empty;
        text.IsVisible = false;
    }

    private static bool ValidateHotkey(HotkeySetting hotkey, out string error) {
        var modifierCount = (hotkey.Ctrl ? 1 : 0)
            + (hotkey.Alt ? 1 : 0)
            + (hotkey.Shift ? 1 : 0)
            + (hotkey.Win ? 1 : 0);

        if (modifierCount == 0) {
            error = "快捷键不能只设置单个按键，请使用 Ctrl、Alt 或 Shift 加一个主键的两键及以上组合。";
            return false;
        }

        if (IsSystemReservedHotkey(hotkey)) {
            error = "该组合是系统快捷键或容易被 Windows 保留，不能设置为应用快捷键。";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool IsSystemReservedHotkey(HotkeySetting hotkey) {
        var key = hotkey.Key;

        if (hotkey.Win) return true;
        if (key is "Ctrl" or "Alt" or "Shift" or "Win") return true;
        if (hotkey.Ctrl && hotkey.Alt && key == "Delete") return true;
        if (hotkey.Alt && key is "Tab" or "F4" or "Space" or "Esc") return true;
        if (hotkey.Ctrl && key is "Esc" or "Tab") return true;
        if (hotkey.Ctrl && hotkey.Shift && key == "Esc") return true;
        if (hotkey.Ctrl && key is "C" or "V" or "X" or "Z" or "Y" or "A" or "S" or "P" or "F" or "N" or "O" or "W") return true;

        return false;
    }

    private static string? NormalizeKey(Key key) {
        return key switch {
            >= Key.A and <= Key.Z => key.ToString(),
            >= Key.D0 and <= Key.D9 => key.ToString()[1..],
            >= Key.NumPad0 and <= Key.NumPad9 => key.ToString().Replace("NumPad", "NumPad"),
            >= Key.F1 and <= Key.F24 => key.ToString(),
            Key.LeftCtrl or Key.RightCtrl => "Ctrl",
            Key.LeftAlt or Key.RightAlt => "Alt",
            Key.LeftShift or Key.RightShift => "Shift",
            Key.LWin or Key.RWin => "Win",
            Key.Tab => "Tab",
            Key.CapsLock => "CapsLock",
            Key.Space => "Space",
            Key.OemTilde => "`",
            Key.OemMinus => "-",
            Key.OemPlus => "=",
            Key.OemOpenBrackets => "[",
            Key.OemCloseBrackets => "]",
            Key.OemPipe => "\\",
            Key.OemSemicolon => ";",
            Key.OemQuotes => "'",
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.OemQuestion => "/",
            Key.OemBackslash => "\\",
            Key.Add => "NumPad+",
            Key.Subtract => "NumPad-",
            Key.Multiply => "NumPad*",
            Key.Divide => "NumPad/",
            Key.Decimal => "NumPad.",
            Key.Insert => "Insert",
            Key.Delete => "Delete",
            Key.Home => "Home",
            Key.End => "End",
            Key.PageUp => "PageUp",
            Key.PageDown => "PageDown",
            Key.Up => "Up",
            Key.Down => "Down",
            Key.Left => "Left",
            Key.Right => "Right",
            _ => null,
        };
    }

    private void OnMinimizeToTrayOptionClick(object? sender, RoutedEventArgs e) {
        _settings.CloseButtonAction = CloseButtonAction.MinimizeToTray;
        _settings.HasAskedCloseButtonAction = true;
        ApplySettingsToUi();
    }

    private void OnCloseAppOptionClick(object? sender, RoutedEventArgs e) {
        _settings.CloseButtonAction = CloseButtonAction.Close;
        _settings.HasAskedCloseButtonAction = true;
        ApplySettingsToUi();
    }

    private void OnToggleTopmostHotkeyClick(object? sender, RoutedEventArgs e) {
        StartCapture(_settings.ToggleTopmostHotkey, (Button)sender!);
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e) {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) {
            BeginMoveDrag(e);
        }
    }

    private void OnMinimizeHotkeyClick(object? sender, RoutedEventArgs e) {
        StartCapture(_settings.MinimizeHotkey, (Button)sender!);
    }

    private void OnShowWindowHotkeyClick(object? sender, RoutedEventArgs e) {
        StartCapture(_settings.ShowWindowHotkey, (Button)sender!);
    }

    private void OnQuickMemoHotkeyClick(object? sender, RoutedEventArgs e) {
        StartCapture(_settings.QuickMemoHotkey, (Button)sender!);
    }

    private void OnQuickMemoEnabledChecked(object? sender, RoutedEventArgs e) {
        _settings.QuickMemoEnabled = true;
        UpdateQuickMemoButtonState();
    }

    private void OnQuickMemoEnabledUnchecked(object? sender, RoutedEventArgs e) {
        _settings.QuickMemoEnabled = false;
        UpdateQuickMemoButtonState();
    }

    private async void OnResetClick(object? sender, RoutedEventArgs e) {
        var confirm = new ConfirmDialog("重置设置", "确定要恢复默认设置吗？");
        var result = await confirm.ShowDialog<bool>(this);
        if (!result) return;

        var defaults = AppSettings.CreateDefault();
        _settings.CloseButtonAction = defaults.CloseButtonAction;
        _settings.HasAskedCloseButtonAction = defaults.HasAskedCloseButtonAction;
        _settings.ToggleTopmostHotkey = defaults.ToggleTopmostHotkey.Clone();
        _settings.MinimizeHotkey = defaults.MinimizeHotkey.Clone();
        _settings.ShowWindowHotkey = defaults.ShowWindowHotkey.Clone();
        _settings.QuickMemoHotkey = defaults.QuickMemoHotkey.Clone();
        _settings.QuickMemoEnabled = defaults.QuickMemoEnabled;
        _settings.DuplicateMemoEnabled = defaults.DuplicateMemoEnabled;
        ApplySettingsToUi();
    }

    private async void OnSaveClick(object? sender, RoutedEventArgs e) {
        UpdateSettingsFromUi();
        await _saveSettingsAsync(_settings.Clone());
        CloseWithTransition();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) {
        CloseWithTransition();
    }

    private void OnDuplicateMemoChecked(object? sender, RoutedEventArgs e) {
        _settings.DuplicateMemoEnabled = true;
    }

    private void OnDuplicateMemoUnchecked(object? sender, RoutedEventArgs e) {
        _settings.DuplicateMemoEnabled = false;
    }

    private void CloseWithTransition() {
        if (_isClosingAfterTransition) return;
        _isClosingAfterTransition = true;
        if (_transition == null) {
            Close();
            return;
        }
        _transition.CloseAfterTransition(() => Close());
    }
}
