using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using System;

namespace note_avalonia;

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
}
