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
using System.Threading.Tasks;

namespace Memo;

public partial class App : Application{
    private WindowsTrayIcon? _trayIcon;
    private GlobalHotkeyService? _hotkeyService;
    private readonly JsonSettingsStorage _settingsStorage = new();
    private readonly List<MemoPopoutWindow> _memoPopouts = new();
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

    private void ToggleLatestTopmostTarget() {
        _memoPopouts.RemoveAll(w => !w.IsVisible);
        if (_latestMemoWindow is MemoPopoutWindow latestPopout && !_memoPopouts.Contains(latestPopout)) {
            _latestMemoWindow = _memoPopouts.LastOrDefault() ?? (Window?)_mainWindow;
        }

        if (_memoPopouts.Count == 0) {
            _mainWindow?.TogglePinned();
            return;
        }

        var target = _latestMemoWindow;
        if (target is MemoPopoutWindow popout && _memoPopouts.Contains(popout)) {
            popout.TogglePinned();
            return;
        }

        if (target is MainWindow mainWindow) {
            mainWindow.TogglePinned();
            return;
        }

        var fallback = _memoPopouts.LastOrDefault();
        if (fallback != null) {
            fallback.TogglePinned();
            _latestMemoWindow = fallback;
            return;
        }

        _mainWindow?.TogglePinned();
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

        var text = await _mainWindow.Clipboard.GetTextAsync();
        if (string.IsNullOrWhiteSpace(text)) return;
        if (text == _lastClipboardText) return;

        _lastClipboardText = text;
        _mainWindow.MemoViewModel.AddOrPromoteItem(text.Trim());
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
