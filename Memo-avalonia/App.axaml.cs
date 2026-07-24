using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Memo.Components.Dialogs;
using Memo.Models;
using Memo.Platform.Windows;
using Memo.Services;
using Memo.Views;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Memo;

public partial class App : Application{
    private WindowsTrayIcon? _trayIcon;
    private GlobalHotkeyService? _hotkeyService;
    private readonly JsonSettingsStorage _settingsStorage = new();
    private readonly List<MemoPopoutWindow> _memoPopouts = new();
    private readonly List<TutorialWindow> _tutorialWindows = new();
    private AppSettings _settings = AppSettings.CreateDefault();
    private Window? _latestMemoWindow;
    private MainWindow? _mainWindow;
    private string? _lastClipboardText;

    public override void Initialize() {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted() {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) {
            var mainWindow = new MainWindow();
            _mainWindow = mainWindow;
            _latestMemoWindow = mainWindow;
            desktop.MainWindow = mainWindow;

            mainWindow.Activated += (_, _) => _latestMemoWindow = mainWindow;
            mainWindow.MemoPopoutRequested += OpenMemoPopout;

            void ExitApplication() {
                _hotkeyService?.Dispose();
                _trayIcon?.Dispose();
                desktop.Shutdown();
            }

            async Task SaveSettingsAsync(AppSettings settings) {
                _settings = settings;
                mainWindow.ApplySettings(_settings);
                _hotkeyService?.Apply(_settings, mainWindow, ToggleLatestTopmostTarget, AddQuickMemoFromClipboard);
                if (_trayIcon != null) _trayIcon.TraySingleClickToShow = _settings.TraySingleClickToShow;
                await _settingsStorage.SaveAsync(_settings);
            }

            void OpenSettings() {
                var settingsWindow = new SettingsWindow(_settings, SaveSettingsAsync);
                settingsWindow.Topmost = mainWindow.Topmost;
                settingsWindow.ShowDialog(mainWindow);
            }

            async Task<CloseButtonAction?> AskCloseButtonActionAsync() {
                var dialog = new CloseActionDialog();
                var action = await dialog.ShowDialog<CloseButtonAction?>(mainWindow);
                if (action == null) return null;

                _settings.CloseButtonAction = action.Value;
                _settings.HasAskedCloseButtonAction = true;
                mainWindow.ApplySettings(_settings);
                await _settingsStorage.SaveAsync(_settings);
                return action.Value;
            }

            mainWindow.ConfigureAppActions(_settings, OpenSettings, ExitApplication, AskCloseButtonActionAsync);

            var trayMenu = new TrayMenuWindow(mainWindow, ExitApplication);
            _trayIcon = new WindowsTrayIcon(
                () => trayMenu.ShowNearTray(mainWindow),
                () => RestoreWindow(mainWindow));
            if (_trayIcon != null) _trayIcon.TraySingleClickToShow = _settings.TraySingleClickToShow;

            _hotkeyService = new GlobalHotkeyService();
            _hotkeyService.Apply(_settings, mainWindow, ToggleLatestTopmostTarget, AddQuickMemoFromClipboard);
            _hotkeyService.RestoreRequested += () => Dispatcher.UIThread.Post(() => RestoreWindow(mainWindow));

            // 启动程序必须立即显示主界面，不受上次隐藏状态或设置加载影响。
            mainWindow.ShowInTaskbar = false;
            mainWindow.ShowOnStartup();

            _ = LoadSettingsAfterStartupAsync(mainWindow);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task LoadSettingsAfterStartupAsync(MainWindow mainWindow) {
        var settings = await _settingsStorage.LoadAsync();
        await Dispatcher.UIThread.InvokeAsync(() => {
            _settings = settings;
            mainWindow.ApplySettings(_settings);
            _hotkeyService?.Apply(_settings, mainWindow, ToggleLatestTopmostTarget, AddQuickMemoFromClipboard);
            if (_trayIcon != null) _trayIcon.TraySingleClickToShow = _settings.TraySingleClickToShow;
        });
    }

    private void OpenMemoPopout(MemoItem memo, PixelPoint position) {
        if (_mainWindow == null) return;

        // 重复便签关闭：查找已有相同备忘录的窗体，移动到鼠标位置
        if (!_settings.DuplicateMemoEnabled) {
            var existing = _memoPopouts.FirstOrDefault(p => p.Memo.Id == memo.Id);
            if (existing != null) {
                AnimateWindowPosition(existing, ClampPopoutPosition(position));
                _latestMemoWindow = existing;
                existing.Activate();
                return;
            }
        }

        var clamped = ClampPopoutPosition(position);
        var popout = new MemoPopoutWindow(memo, clamped,
            (item, content) => _mainWindow.MemoViewModel.UpdateItemAndSave(item.Id, content));
        memo.PopoutRefCount++;
        _memoPopouts.Add(popout);
        _latestMemoWindow = popout;

        popout.Activated += (_, _) => _latestMemoWindow = popout;
        popout.Closed += (_, _) => {
            memo.PopoutRefCount--;
            _memoPopouts.Remove(popout);
            if (ReferenceEquals(_latestMemoWindow, popout)) {
                _latestMemoWindow = (Window?)_memoPopouts.LastOrDefault() ?? _mainWindow;
            }
        };

        popout.Show();
        popout.Activate();
    }

    private PixelPoint ClampPopoutPosition(PixelPoint pointerPosition) {
        const int fallbackWidth = 360;
        const int fallbackHeight = 280;
        const int titleBarHeight = 38;
        const int margin = 12;

        var requested = new PixelPoint(
            pointerPosition.X - (fallbackWidth / 2),
            pointerPosition.Y - titleBarHeight);

        var screen = _mainWindow?.Screens.ScreenFromPoint(pointerPosition)
            ?? _mainWindow?.Screens.Primary;
        if (screen == null) return requested;

        var area = screen.WorkingArea;
        var minX = area.X + margin;
        var minY = area.Y + margin;
        var maxX = area.Right - fallbackWidth - margin;
        var maxY = area.Bottom - fallbackHeight - margin;

        if (maxX < minX) maxX = minX;
        if (maxY < minY) maxY = minY;

        return new PixelPoint(
            Avalonia.Utilities.MathUtilities.Clamp(requested.X, minX, maxX),
            Avalonia.Utilities.MathUtilities.Clamp(requested.Y, minY, maxY));
    }

    /// <summary>
    /// 将窗口从当前位置平滑移动到目标位置（~180ms cubic ease-out）。
    /// </summary>
    private static void AnimateWindowPosition(Window window, PixelPoint target) {
        var from = window.Position;
        // 距离太近无需动画
        if (from == target) return;

        const int durationMs = 180;
        const int frameIntervalMs = 16;
        var startTime = Environment.TickCount;

        var timer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(frameIntervalMs),
            DispatcherPriority.Render,
            (_, _) => { });

        timer.Tick += (_, _) => {
            var elapsed = Environment.TickCount - startTime;
            if (elapsed <= 0) elapsed = 0;
            var t = elapsed / (double)durationMs;

            if (t >= 1.0) {
                window.Position = target;
                timer.Stop();
                return;
            }

            // cubic ease-out
            var eased = 1.0 - Math.Pow(1.0 - t, 3);

            window.Position = new PixelPoint(
                (int)(from.X + (target.X - from.X) * eased),
                (int)(from.Y + (target.Y - from.Y) * eased));
        };

        timer.Start();
    }

    /// <summary>
    /// 打开一个独立的非模态教程窗口，与备忘录弹出窗同构。
    /// 以主窗体所在屏幕工作区为基准居中，不阻塞主窗体 / 设置面板。
    /// </summary>
    internal void OpenTutorial(AppSettings settings) {
        if (_mainWindow == null) return;

        var tutorial = new TutorialWindow(settings);
        _tutorialWindows.Add(tutorial);
        _latestMemoWindow = tutorial;

        tutorial.WindowStartupLocation = WindowStartupLocation.Manual;
        tutorial.Position = CenterInMainScreen(tutorial.Width, tutorial.Height);

        tutorial.Activated += (_, _) => _latestMemoWindow = tutorial;
        tutorial.Closed += (_, _) => {
            _tutorialWindows.Remove(tutorial);
            if (ReferenceEquals(_latestMemoWindow, tutorial)) {
                _latestMemoWindow = (Window?)_tutorialWindows.LastOrDefault()
                    ?? (Window?)_memoPopouts.LastOrDefault()
                    ?? _mainWindow;
            }
        };

        tutorial.Show();
    }

    /// <summary>
    /// 以主窗体所在屏幕工作区为基准，计算给定尺寸窗口的居中位置（并做边界 clamp）。
    /// </summary>
    private PixelPoint CenterInMainScreen(double width, double height) {
        const int margin = 12;
        var w = (int)width;
        var h = (int)height;

        var screen = _mainWindow?.Screens.ScreenFromWindow(_mainWindow)
            ?? _mainWindow?.Screens.Primary;
        var area = screen?.WorkingArea ?? new PixelRect(0, 0, 1280, 720);

        var requestedX = area.X + (area.Width - w) / 2.0;
        var requestedY = area.Y + (area.Height - h) / 2.0;

        var minX = area.X + margin;
        var minY = area.Y + margin;
        var maxX = area.Right - w - margin;
        var maxY = area.Bottom - h - margin;
        if (maxX < minX) maxX = minX;
        if (maxY < minY) maxY = minY;

        return new PixelPoint(
            Avalonia.Utilities.MathUtilities.Clamp((int)requestedX, minX, maxX),
            Avalonia.Utilities.MathUtilities.Clamp((int)requestedY, minY, maxY));
    }

    private void ToggleLatestTopmostTarget() {
        _memoPopouts.RemoveAll(w => !w.IsVisible);
        _tutorialWindows.RemoveAll(w => !w.IsVisible);
        if (_latestMemoWindow is MemoPopoutWindow latestPopout && !_memoPopouts.Contains(latestPopout)) {
            _latestMemoWindow = (Window?)_memoPopouts.LastOrDefault()
                ?? (Window?)_tutorialWindows.LastOrDefault()
                ?? _mainWindow;
        }
        if (_latestMemoWindow is TutorialWindow latestTutorial && !_tutorialWindows.Contains(latestTutorial)) {
            _latestMemoWindow = (Window?)_tutorialWindows.LastOrDefault()
                ?? (Window?)_memoPopouts.LastOrDefault()
                ?? _mainWindow;
        }

        var target = _latestMemoWindow;
        if (target is MainWindow mainWindow) {
            mainWindow.TogglePinned();
            return;
        }
        if (target is MemoPopoutWindow popout && _memoPopouts.Contains(popout)) {
            popout.TogglePinned();
            return;
        }
        if (target is TutorialWindow tutorial && _tutorialWindows.Contains(tutorial)) {
            tutorial.TogglePinned();
            return;
        }

        if (_memoPopouts.LastOrDefault() is { } p) {
            p.TogglePinned();
            _latestMemoWindow = p;
            return;
        }
        if (_tutorialWindows.LastOrDefault() is { } t) {
            t.TogglePinned();
            _latestMemoWindow = t;
            return;
        }

        _mainWindow?.TogglePinned();
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT {
        public int X;
        public int Y;
    }

    /// <summary>
    /// 从剪贴板读取文本并快速添加为备忘录。
    /// 若剪贴板内容与上次添加的相同则跳过，避免重复添加。
    /// </summary>
    private void AddQuickMemoFromClipboard() {
        _ = AddQuickMemoFromClipboardAsync();
    }

    private async Task AddQuickMemoFromClipboardAsync() {
        if (_mainWindow?.Clipboard == null) return;

        // 若启用了「自动显示便签」，在读取剪贴板之前预先获取鼠标位置
        PixelPoint? cursorPosition = null;
        if (_settings.QuickMemoEnabled && _settings.QuickMemoShowPopoutAfterAdd) {
            if (GetCursorPos(out var pt)) {
                cursorPosition = new PixelPoint(pt.X, pt.Y);
            }
        }

        var text = await _mainWindow.Clipboard.GetTextAsync();
        if (string.IsNullOrWhiteSpace(text)) return;

        // 剪贴板内容与上次相同：不再重复添加，但若启用了自动弹窗仍要处理窗口
        if (text == _lastClipboardText) {
            if (cursorPosition.HasValue
                && _settings.QuickMemoEnabled && _settings.QuickMemoShowPopoutAfterAdd) {
                var existingMemo = _mainWindow.MemoViewModel.FindByContent(text.Trim());
                if (existingMemo != null) {
                    var existing = _memoPopouts.FirstOrDefault(p => p.Memo.Id == existingMemo.Id);
                    if (existing != null) {
                        AnimateWindowPosition(existing, ClampPopoutPosition(cursorPosition.Value));
                        _latestMemoWindow = existing;
                        existing.Activate();
                    } else {
                        OpenMemoPopout(existingMemo, cursorPosition.Value);
                    }
                }
            }
            return;
        }

        _lastClipboardText = text;
        var memo = _mainWindow.MemoViewModel.AddOrPromoteItem(text.Trim());

        if (memo != null && cursorPosition.HasValue
            && _settings.QuickMemoEnabled && _settings.QuickMemoShowPopoutAfterAdd) {
            // 若该备忘录已有弹出窗口，直接将其移动到鼠标位置（无论 DuplicateMemoEnabled 如何设置）
            var existing = _memoPopouts.FirstOrDefault(p => p.Memo.Id == memo.Id);
            if (existing != null) {
                AnimateWindowPosition(existing, ClampPopoutPosition(cursorPosition.Value));
                _latestMemoWindow = existing;
                existing.Activate();
            } else {
                OpenMemoPopout(memo, cursorPosition.Value);
            }
        }
    }

    /// <summary>
    /// 从系统托盘恢复/显示主窗口。
    /// </summary>
    internal static void RestoreWindow(Window mainWindow) {
        if (mainWindow is MainWindow window) {
            window.ShowWithTransition();
            return;
        }

        if (mainWindow.WindowState == WindowState.Minimized) {
            mainWindow.Show();
            mainWindow.WindowState = WindowState.Normal;
        }
        mainWindow.ShowInTaskbar = false;
        mainWindow.Activate();
    }

    /// <summary>
    /// 隐藏指定窗口到系统托盘。
    /// </summary>
    internal static void HideWindow(Window mainWindow) {
        if (mainWindow is MainWindow window) {
            window.HideToTrayWithTransition();
            return;
        }

        mainWindow.WindowState = WindowState.Minimized;
        mainWindow.ShowInTaskbar = false;
    }

    /// <summary>
    /// 隐藏主窗口到系统托盘（点击关闭按钮时调用）。
    /// </summary>
    public static void HideToTray() {
        if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) {
            var window = desktop.MainWindow;
            if (window != null) {
                HideWindow(window);
            }
        }
    }
}
