using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using note_avalonia.Models;
using System;
using System.Threading.Tasks;

namespace note_avalonia;

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
        UpdateHotkeyButtons();
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
    }

    private void StartCapture(HotkeySetting hotkey, Button button) {
        _capturingHotkey = hotkey;
        _capturingButton = button;
        button.Content = "按下快捷键...";
        Focus();
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e) {
        if (_capturingHotkey == null || _capturingButton == null) return;

        e.Handled = true;
        if (e.Key == Key.Escape) {
            ClearHotkey(_capturingHotkey);
            EndCapture();
            return;
        }

        var key = NormalizeKey(e.Key);
        if (key == null || IsModifierKey(e.Key)) return;

        _capturingHotkey.Key = key;
        _capturingHotkey.Ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        _capturingHotkey.Alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        _capturingHotkey.Shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        _capturingHotkey.Win = e.KeyModifiers.HasFlag(KeyModifiers.Meta);
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

    private static bool IsModifierKey(Key key) =>
        key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    private static string? NormalizeKey(Key key) {
        return key switch {
            >= Key.A and <= Key.Z => key.ToString(),
            >= Key.D0 and <= Key.D9 => key.ToString()[1..],
            >= Key.NumPad0 and <= Key.NumPad9 => key.ToString().Replace("NumPad", "NumPad"),
            >= Key.F1 and <= Key.F24 => key.ToString(),
            Key.Space => "Space",
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

    private async void OnResetClick(object? sender, RoutedEventArgs e) {
        var confirm = new ConfirmWindow("重置设置", "确定要恢复默认设置吗？");
        var result = await confirm.ShowDialog<bool>(this);
        if (!result) return;

        var defaults = AppSettings.CreateDefault();
        _settings.CloseButtonAction = defaults.CloseButtonAction;
        _settings.HasAskedCloseButtonAction = defaults.HasAskedCloseButtonAction;
        _settings.ToggleTopmostHotkey = defaults.ToggleTopmostHotkey.Clone();
        _settings.MinimizeHotkey = defaults.MinimizeHotkey.Clone();
        _settings.ShowWindowHotkey = defaults.ShowWindowHotkey.Clone();
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
