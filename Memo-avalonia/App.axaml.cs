using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using note_avalonia.Models;
using note_avalonia.Services;
using System.Threading.Tasks;

namespace note_avalonia;

public partial class App : Application{
    private WindowsTrayIcon? _trayIcon;
    private GlobalHotkeyService? _hotkeyService;
    private readonly JsonSettingsStorage _settingsStorage = new();
    private AppSettings _settings = AppSettings.CreateDefault();

    public override void Initialize() {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted() {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) {
            var mainWindow = new MainWindow();
            desktop.MainWindow = mainWindow;

            void ExitApplication() {
                _hotkeyService?.Dispose();
                _trayIcon?.Dispose();
                desktop.Shutdown();
            }

            async Task SaveSettingsAsync(AppSettings settings) {
                _settings = settings;
                mainWindow.ApplySettings(_settings);
                _hotkeyService?.Apply(_settings, mainWindow);
                await _settingsStorage.SaveAsync(_settings);
            }

            void OpenSettings() {
                var settingsWindow = new SettingsWindow(_settings, SaveSettingsAsync);
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
                "Assets/appicon.ico",
                () => trayMenu.ShowNearTray(mainWindow),
                () => RestoreWindow(mainWindow));

            _hotkeyService = new GlobalHotkeyService();
            _hotkeyService.Apply(_settings, mainWindow);

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
            _hotkeyService?.Apply(_settings, mainWindow);
        });
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
