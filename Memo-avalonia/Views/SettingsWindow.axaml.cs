using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Memo.Components;
using Memo.Components.Dialogs;
using Memo.Models;
using Memo.UI;
using Memo.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Memo.Views;

public partial class SettingsWindow : Window
{
    private AppSettings _settings = AppSettings.CreateDefault();
    private Func<AppSettings, Task> _saveSettingsAsync = _ => Task.CompletedTask;
    private HotkeySetting? _capturingHotkey;
    private Button? _capturingButton;
    private Button? _conflictingButton;
    private readonly HashSet<string> _captureHeldKeys = new();
    private string? _captureMainKey;
    private readonly HotkeySetting _captureSnapshot = new();
    private WindowTransitionController? _transition;
    private bool _isClosingAfterTransition;
    private Task _saveChain = Task.CompletedTask;
    private readonly object _saveLock = new();

    internal enum HotkeyField { ToggleTopmost, Minimize, ShowWindow, QuickMemo }

    internal readonly record struct HotkeyConflict(HotkeyField Field, Button ConflictingButton);

    internal readonly record struct HotkeySettingsSnapshot(
        HotkeySetting? ToggleTopmost, Button ToggleTopmostButton,
        HotkeySetting? Minimize, Button MinimizeButton,
        HotkeySetting? ShowWindow, Button ShowWindowButton,
        HotkeySetting? QuickMemo, Button QuickMemoButton);

    public SettingsWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => this.AssignResizeCursors();
        _transition = new WindowTransitionController(this, this.FindControl<Border>("_settingsShell")!);
        _transition.PrepareOpen();
        Opened += (_, _) => _transition.PlayOpen();
        KeyDown += OnWindowKeyDown;
        KeyUp += OnWindowKeyUp;
        ApplySettingsToUi();

        var trayClickToggleInit = this.FindControl<LabeledToggleSwitch>("_trayClickToggle");
        if (trayClickToggleInit != null)
        {
            trayClickToggleInit.ValueChanged += v =>
            {
                _settings.TraySingleClickToShow = !v;
                AutoSave();
            };
        }

        var tt = this.FindControl<Button>("_toggleTopmostHotkeyButton")!;
        var mn = this.FindControl<Button>("_minimizeHotkeyButton")!;
        var sw = this.FindControl<Button>("_showWindowHotkeyButton")!;
        var qm = this.FindControl<Button>("_quickMemoHotkeyButton")!;
        tt.PointerPressed += OnHotkeyButtonPointerPressed;
        mn.PointerPressed += OnHotkeyButtonPointerPressed;
        sw.PointerPressed += OnHotkeyButtonPointerPressed;
        qm.PointerPressed += OnHotkeyButtonPointerPressed;
    }

    public SettingsWindow(AppSettings settings, Func<AppSettings, Task> saveSettingsAsync)
        : this()
    {
        _settings = settings.Clone();
        _saveSettingsAsync = saveSettingsAsync;
        ApplySettingsToUi();
    }

    private void ApplySettingsToUi()
    {
        var minimizeOption = this.FindControl<ToggleButton>("_minimizeToTrayOption")!;
        var closeOption = this.FindControl<ToggleButton>("_closeAppOption")!;
        minimizeOption.IsChecked = _settings.CloseButtonAction == CloseButtonAction.MinimizeToTray;
        closeOption.IsChecked = _settings.CloseButtonAction == CloseButtonAction.Close;

        var enabledCheckBox = this.FindControl<CheckBox>("_quickMemoEnabledCheckBox");
        if (enabledCheckBox != null)
        {
            enabledCheckBox.IsChecked = _settings.QuickMemoEnabled;
        }

        var duplicateCheckBox = this.FindControl<CheckBox>("_duplicateMemoCheckBox");
        if (duplicateCheckBox != null)
        {
            duplicateCheckBox.IsChecked = _settings.DuplicateMemoEnabled;
        }

        var showPopoutCheckBox = this.FindControl<CheckBox>("_quickMemoShowPopoutAfterAddCheckBox");
        if (showPopoutCheckBox != null)
        {
            showPopoutCheckBox.IsChecked = _settings.QuickMemoShowPopoutAfterAdd;
        }

        var trayClickToggle = this.FindControl<LabeledToggleSwitch>("_trayClickToggle");
        if (trayClickToggle != null)
        {
            trayClickToggle.Value = !_settings.TraySingleClickToShow;
        }

        UpdateHotkeyButtons();
        UpdateQuickMemoDependentUi();
    }

    // 自动保存：把当前 _settings 串行地落盘并生效（apply + 持久化），
    // 避免快速连续编辑产生并发写。失败时弹窗提示用户。
    private void AutoSave()
    {
        lock (_saveLock)
        {
            var fireAndForget = _saveChain.ContinueWith(t =>
            {
                try
                {
                    var clone = _settings.Clone();
                    // 热键注册必须在创建 NativeWindow 的 UI 线程上执行
                    Dispatcher.UIThread.InvokeAsync(() => _saveSettingsAsync(clone));
                }
                catch (Exception ex)
                {
                    Dispatcher.UIThread.Post(async () =>
                        await new ConfirmDialog("保存设置失败", $"无法保存设置：{ex.Message}").ShowDialog(this));
                }
            }, TaskScheduler.Default);
            _saveChain = fireAndForget;
        }
    }

    private void UpdateHotkeyButtons()
    {
        this.FindControl<Button>("_toggleTopmostHotkeyButton")!.Content = _settings.ToggleTopmostHotkey.ToString();
        this.FindControl<Button>("_minimizeHotkeyButton")!.Content = _settings.MinimizeHotkey.ToString();
        this.FindControl<Button>("_showWindowHotkeyButton")!.Content = _settings.ShowWindowHotkey.ToString();
        var quickMemoButton = this.FindControl<Button>("_quickMemoHotkeyButton");
        if (quickMemoButton != null)
        {
            quickMemoButton.Content = _settings.QuickMemoHotkey.ToString();
        }
    }

    private void UpdateQuickMemoDependentUi()
    {
        var button = this.FindControl<Button>("_quickMemoHotkeyButton");
        if (button != null)
        {
            button.IsEnabled = _settings.QuickMemoEnabled;
            button.Opacity = _settings.QuickMemoEnabled ? 1.0 : 0.45;
        }

        var row = this.FindControl<Grid>("_quickMemoShowPopoutAfterAddRow");
        if (row != null)
        {
            row.IsVisible = _settings.QuickMemoEnabled;
        }
    }

    private void StartCapture(HotkeySetting hotkey, Button button)
    {
        _capturingHotkey = hotkey;
        _capturingButton = button;
        _captureHeldKeys.Clear();
        _captureMainKey = null;
        button.Content = "按下快捷键...";
        ClearHotkeyValidation();
        ClearConflict();
        Focus();
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (_capturingHotkey == null || _capturingButton == null) return;

        e.Handled = true;
        if (e.Key == Key.Escape)
        {
            ClearHotkey(_capturingHotkey);
            ClearHotkeyValidation();
            ClearConflict();
            EndCapture();
            return;
        }

        var key = NormalizeKey(e.Key);
        if (key == null) return;

        // 修饰键只记录按下状态，不在此处立即确认，等主键松开后再整体保存与校验
        if (IsModifierKey(key))
        {
            _captureHeldKeys.Add(key);
            UpdateCapturePreview();
            return;
        }

        // 非修饰键作为主键：按下这一刻的快照即为用户想要的完整组合，
        // 修饰标志读取此时已按下的修饰键状态，松开后 ApplyCapture 再统一保存/冲突检测
        _captureMainKey = key;
        CaptureSnapshotFromModifiers(e.KeyModifiers, key);
        UpdateCapturePreview();
    }

    private void OnWindowKeyUp(object? sender, KeyEventArgs e)
    {
        if (_capturingHotkey == null || _capturingButton == null) return;

        var key = NormalizeKey(e.Key);
        if (key != null && IsModifierKey(key))
        {
            _captureHeldKeys.Remove(key);
            UpdateCapturePreview();
        }

        // 主键松开才真正确认；若从未按下主键（仅点了修饰键）则取消录入，恢复原值
        if (key != null && key == _captureMainKey)
        {
            ApplyCapture();
            return;
        }

        if (_captureMainKey == null && _captureHeldKeys.Count == 0)
        {
            ClearHotkeyValidation();
            ClearConflict();
            EndCapture();
        }
    }

    private void ApplyCapture()
    {
        if (_capturingHotkey == null || _capturingButton == null) return;

        var siblings = BuildSiblings(_capturingHotkey);
        if (!ValidateHotkey(_captureSnapshot, siblings, out var error))
        {
            ShowHotkeyValidation(error);
            if (FindConflict(_captureSnapshot, siblings) is { } c) MarkConflict(c.ConflictingButton);
            EndCapture();
            return;
        }

        _capturingHotkey.Key = _captureSnapshot.Key;
        _capturingHotkey.Ctrl = _captureSnapshot.Ctrl;
        _capturingHotkey.Alt = _captureSnapshot.Alt;
        _capturingHotkey.Shift = _captureSnapshot.Shift;
        _capturingHotkey.Win = _captureSnapshot.Win;
        ClearHotkeyValidation();
        ClearConflict();
        EndCapture();
        AutoSave();
    }

    private void EndCapture()
    {
        _capturingHotkey = null;
        _capturingButton = null;
        _captureHeldKeys.Clear();
        _captureMainKey = null;
        UpdateHotkeyButtons();
    }

    private static bool IsModifierKey(string key) =>
        key is "Ctrl" or "Alt" or "Shift" or "Win";

    private void CaptureSnapshotFromModifiers(KeyModifiers modifiers, string mainKey)
    {
        _captureSnapshot.Key = mainKey;
        _captureSnapshot.Ctrl = modifiers.HasFlag(KeyModifiers.Control) && mainKey != "Ctrl";
        _captureSnapshot.Alt = modifiers.HasFlag(KeyModifiers.Alt) && mainKey != "Alt";
        _captureSnapshot.Shift = modifiers.HasFlag(KeyModifiers.Shift) && mainKey != "Shift";
        _captureSnapshot.Win = modifiers.HasFlag(KeyModifiers.Meta) && mainKey != "Win";
    }

    private void UpdateCapturePreview()
    {
        if (_capturingButton == null) return;
        var parts = new List<string>();
        if (_captureHeldKeys.Contains("Ctrl")) parts.Add("Ctrl");
        if (_captureHeldKeys.Contains("Alt")) parts.Add("Alt");
        if (_captureHeldKeys.Contains("Shift")) parts.Add("Shift");
        if (_captureHeldKeys.Contains("Win")) parts.Add("Win");
        parts.Add(_captureMainKey ?? "?");
        _capturingButton.Content = string.Join(" + ", parts);
    }

    private HotkeySettingsSnapshot BuildSiblings(HotkeySetting? capturing)
    {
        var tt = this.FindControl<Button>("_toggleTopmostHotkeyButton")!;
        var mn = this.FindControl<Button>("_minimizeHotkeyButton")!;
        var sw = this.FindControl<Button>("_showWindowHotkeyButton")!;
        var qm = this.FindControl<Button>("_quickMemoHotkeyButton")!;
        return new HotkeySettingsSnapshot(
            capturing != _settings.ToggleTopmostHotkey ? _settings.ToggleTopmostHotkey : null, tt,
            capturing != _settings.MinimizeHotkey ? _settings.MinimizeHotkey : null, mn,
            capturing != _settings.ShowWindowHotkey ? _settings.ShowWindowHotkey : null, sw,
            (_settings.QuickMemoEnabled && capturing != _settings.QuickMemoHotkey) ? _settings.QuickMemoHotkey : null, qm);
    }

    private static void ClearHotkey(HotkeySetting hotkey)
    {
        hotkey.Key = string.Empty;
        hotkey.Ctrl = false;
        hotkey.Alt = false;
        hotkey.Shift = false;
        hotkey.Win = false;
    }

    private void ShowHotkeyValidation(string message)
    {
        var text = this.FindControl<TextBlock>("_hotkeyValidationText");
        if (text == null) return;

        text.Text = message;
        text.IsVisible = true;
    }

    private void ClearHotkeyValidation()
    {
        var text = this.FindControl<TextBlock>("_hotkeyValidationText");
        if (text == null) return;

        text.Text = string.Empty;
        text.IsVisible = false;
    }

    private void MarkConflict(Button button)
    {
        ClearConflict();
        _conflictingButton = button;
        button.Classes.Add("Conflict");
    }

    private void ClearConflict()
    {
        if (_conflictingButton != null)
        {
            _conflictingButton.Classes.Remove("Conflict");
            _conflictingButton = null;
        }
    }

    private static bool ValidateHotkey(HotkeySetting hotkey, HotkeySettingsSnapshot siblings, out string error)
    {
        var modifierCount = (hotkey.Ctrl ? 1 : 0)
            + (hotkey.Alt ? 1 : 0)
            + (hotkey.Shift ? 1 : 0)
            + (hotkey.Win ? 1 : 0);

        if (modifierCount == 0)
        {
            error = "快捷键不能只设置单个按键，请使用 Ctrl、Alt 或 Shift 加一个主键的两键及以上组合。";
            return false;
        }

        if (IsSystemReservedHotkey(hotkey))
        {
            error = "该组合是系统快捷键或容易被 Windows 保留，不能设置为应用快捷键。";
            return false;
        }

        var conflict = FindConflict(hotkey, siblings);
        if (conflict.HasValue)
        {
            error = $"该快捷键已被「{ActionName(conflict.Value.Field)}」功能占用，请选择其他组合。";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool IsSystemReservedHotkey(HotkeySetting hotkey)
    {
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

    private static string ActionName(HotkeyField f) => f switch
    {
        HotkeyField.ToggleTopmost => "置顶",
        HotkeyField.Minimize => "最小化",
        HotkeyField.ShowWindow => "显示软件",
        HotkeyField.QuickMemo => "快速添加（剪贴板）",
        _ => "其他功能",
    };

    private static bool HotkeySettingEquals(HotkeySetting a, HotkeySetting b) =>
        a.Ctrl == b.Ctrl && a.Alt == b.Alt && a.Shift == b.Shift && a.Win == b.Win
        && string.Equals(a.Key, b.Key, StringComparison.OrdinalIgnoreCase);

    private static HotkeyConflict? FindConflict(HotkeySetting candidate, HotkeySettingsSnapshot s)
    {
        if (candidate.IsEmpty) return null;
        if (s.ToggleTopmost is { } toggle && HotkeySettingEquals(candidate, toggle)) return new(HotkeyField.ToggleTopmost, s.ToggleTopmostButton);
        if (s.Minimize is { } minimize && HotkeySettingEquals(candidate, minimize)) return new(HotkeyField.Minimize, s.MinimizeButton);
        if (s.ShowWindow is { } showWindow && HotkeySettingEquals(candidate, showWindow)) return new(HotkeyField.ShowWindow, s.ShowWindowButton);
        if (s.QuickMemo is { } quickMemo && HotkeySettingEquals(candidate, quickMemo)) return new(HotkeyField.QuickMemo, s.QuickMemoButton);
        return null;
    }

    internal static bool FindFirstDuplicatePair(AppSettings settings, out string fieldA, out string fieldB)
    {
        var list = new List<(string Name, HotkeySetting H)> {
            ("置顶", settings.ToggleTopmostHotkey),
            ("最小化", settings.MinimizeHotkey),
            ("显示软件", settings.ShowWindowHotkey),
        };
        if (settings.QuickMemoEnabled) list.Add(("快速添加（剪贴板）", settings.QuickMemoHotkey));
        var present = list.Where(e => !e.H.IsEmpty).ToList();
        for (int i = 0; i < present.Count; i++)
            for (int j = i + 1; j < present.Count; j++)
                if (HotkeySettingEquals(present[i].H, present[j].H)) { fieldA = present[i].Name; fieldB = present[j].Name; return true; }
        fieldA = fieldB = string.Empty;
        return false;
    }

    private static string? NormalizeKey(Key key)
    {
        return key switch
        {
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

    private void OnMinimizeToTrayOptionClick(object? sender, RoutedEventArgs e)
    {
        _settings.CloseButtonAction = CloseButtonAction.MinimizeToTray;
        _settings.HasAskedCloseButtonAction = true;
        ApplySettingsToUi();
        AutoSave();
    }

    private void OnCloseAppOptionClick(object? sender, RoutedEventArgs e)
    {
        _settings.CloseButtonAction = CloseButtonAction.Close;
        _settings.HasAskedCloseButtonAction = true;
        ApplySettingsToUi();
        AutoSave();
    }

    private void OnToggleTopmostHotkeyClick(object? sender, RoutedEventArgs e)
    {
        StartCapture(_settings.ToggleTopmostHotkey, (Button)sender!);
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    // —— 边缘/四角拖拽缩放窗口 ——
    // 8 个透明手柄（4 边 + 4 角）共用一个 handler，靠 Tag 区分要缩放的哪条边/哪个角。
    private void OnResizeHandlePointerPressed(object? sender, PointerPressedEventArgs e) {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (sender is not Border handle || handle.Tag is not string tag) return;
        var edge = tag switch {
            "Top"         => WindowEdge.North,
            "Bottom"      => WindowEdge.South,
            "Left"        => WindowEdge.West,
            "Right"       => WindowEdge.East,
            "TopLeft"     => WindowEdge.NorthWest,
            "TopRight"    => WindowEdge.NorthEast,
            "BottomLeft"  => WindowEdge.SouthWest,
            "BottomRight" => WindowEdge.SouthEast,
            _             => WindowEdge.North
        };
        BeginResizeDrag(edge, e);
    }

    private void OnMinimizeHotkeyClick(object? sender, RoutedEventArgs e)
    {
        StartCapture(_settings.MinimizeHotkey, (Button)sender!);
    }

    private void OnShowWindowHotkeyClick(object? sender, RoutedEventArgs e)
    {
        StartCapture(_settings.ShowWindowHotkey, (Button)sender!);
    }

    private void OnQuickMemoHotkeyClick(object? sender, RoutedEventArgs e)
    {
        StartCapture(_settings.QuickMemoHotkey, (Button)sender!);
    }

    private void OnHotkeyButtonPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_capturingHotkey == null) return;
        if (!e.GetCurrentPoint(this).Properties.IsRightButtonPressed) return;

        _capturingButton!.Content = _capturingHotkey.ToString();
        ClearHotkeyValidation();
        ClearConflict();
        e.Handled = true;
        EndCapture();
    }

    private void OnQuickMemoEnabledChecked(object? sender, RoutedEventArgs e)
    {
        _settings.QuickMemoEnabled = true;
        UpdateQuickMemoDependentUi();
        AutoSave();
    }

    private void OnQuickMemoEnabledUnchecked(object? sender, RoutedEventArgs e)
    {
        _settings.QuickMemoEnabled = false;
        UpdateQuickMemoDependentUi();
        AutoSave();
    }

    private void OnQuickMemoShowPopoutAfterAddChecked(object? sender, RoutedEventArgs e)
    {
        _settings.QuickMemoShowPopoutAfterAdd = true;
        AutoSave();
    }

    private void OnQuickMemoShowPopoutAfterAddUnchecked(object? sender, RoutedEventArgs e)
    {
        _settings.QuickMemoShowPopoutAfterAdd = false;
        AutoSave();
    }

    private async void OnResetClick(object? sender, RoutedEventArgs e)
    {
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
        _settings.TraySingleClickToShow = defaults.TraySingleClickToShow;
        _settings.QuickMemoShowPopoutAfterAdd = defaults.QuickMemoShowPopoutAfterAdd;
        ApplySettingsToUi();
        AutoSave();
    }

    private void OnTutorialClick(object? sender, RoutedEventArgs e) {
        // 作为独立非模态窗口打开，与备忘录弹出窗同构，不阻塞主窗体 / 设置面板。
        var app = (App)Avalonia.Application.Current!;
        app.OpenTutorial(_settings);
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        CloseWithTransition();
    }

    private void OnDuplicateMemoChecked(object? sender, RoutedEventArgs e)
    {
        _settings.DuplicateMemoEnabled = true;
        AutoSave();
    }

    private void OnDuplicateMemoUnchecked(object? sender, RoutedEventArgs e)
    {
        _settings.DuplicateMemoEnabled = false;
        AutoSave();
    }

    private void CloseWithTransition()
    {
        if (_isClosingAfterTransition) return;
        _isClosingAfterTransition = true;
        if (_transition == null)
        {
            Close();
            return;
        }
        _transition.CloseAfterTransition(() => Close());
    }
}
