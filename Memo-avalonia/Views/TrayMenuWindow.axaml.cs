using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using System;
using System.Runtime.InteropServices;

namespace Memo.Views;

public partial class TrayMenuWindow : Window {
    private MainWindow? _mainWindow;
    private Action? _exitApplication;

    public TrayMenuWindow() {
        InitializeComponent();
    }

    public TrayMenuWindow(MainWindow mainWindow, Action exitApplication)
        : this()
    {
        _mainWindow = mainWindow;
        _exitApplication = exitApplication;

        Deactivated += (_, _) => Hide();
        _mainWindow.PropertyChanged += (_, e) =>
        {
            if (e.Property == Window.TopmostProperty)
            {
                UpdatePinStatus();
            }
        };

        UpdatePinStatus();
    }

    public void ShowNearTray(Window owner) {
        if (_mainWindow == null) return;

        PositionNearTray(owner);
        Topmost = true;
        Show();
        Activate();
        BringAboveTrayFlyout();
        Dispatcher.UIThread.Post(BringAboveTrayFlyout, DispatcherPriority.Render);
    }

    private void PositionNearTray(Window owner) {
        var screen = owner.Screens.ScreenFromWindow(owner) ?? owner.Screens.Primary;
        if (screen == null) return;

        var area = screen.WorkingArea;
        const int margin = 10;
        var x = area.X + area.Width - (int)Width - margin;
        var y = area.Y + area.Height - (int)Height - margin;
        Position = new PixelPoint(x, y);
    }

    private void OnOpenClick(object? sender, RoutedEventArgs e) {
        if (_mainWindow == null) return;

        Hide();
        App.RestoreWindow(_mainWindow);
    }

    private void OnNewMemoClick(object? sender, RoutedEventArgs e) {
        if (_mainWindow == null) return;

        Hide();
        App.RestoreWindow(_mainWindow);
        _mainWindow.FocusInputForNewMemo();
    }

    private void OnPinClick(object? sender, RoutedEventArgs e) {
        if (_mainWindow == null) return;

        _mainWindow.TogglePinned();
        UpdatePinStatus();
    }

    private void OnExitClick(object? sender, RoutedEventArgs e) {
        Hide();
        _exitApplication?.Invoke();
    }

    private void UpdatePinStatus() {
        if (_mainWindow == null) return;

        var statusText = this.FindControl<TextBlock>("_pinStatusText");
        var statusBadge = this.FindControl<Border>("_pinStatusBadge");
        if (statusText == null || statusBadge == null) return;

        if (_mainWindow.IsPinned) {
            statusText.Text = "开启";
            statusText.Foreground = BrushResource("AccentPrimaryBrush");
            statusBadge.Background = BrushResource("AccentSubtleBrush");
        }
        else {
            statusText.Text = "关闭";
            statusText.Foreground = BrushResource("TextTertiaryBrush");
            statusBadge.Background = BrushResource("BgTertiaryBrush");
        }
    }

    private IBrush? BrushResource(string key) {
        return Application.Current?.TryGetResource(key, ActualThemeVariant, out var value) == true
            ? value as IBrush
            : null;
    }

    private void BringAboveTrayFlyout() {
        Topmost = false;
        Topmost = true;

        var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero) return;

        SetWindowPos(handle, HwndTopmost, 0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
    }

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private static readonly IntPtr HwndTopmost = new(-1);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);
}
