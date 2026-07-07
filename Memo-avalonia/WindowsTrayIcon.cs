using System;
using System.Drawing;
using System.Windows.Forms;

namespace note_avalonia;

internal sealed class WindowsTrayIcon : IDisposable {
    private readonly NotifyIcon _notifyIcon;
    private readonly Action _showMenu;
    private readonly Action _showWindow;

    public WindowsTrayIcon(string iconPath, Action showMenu, Action showWindow) {
        _showMenu = showMenu;
        _showWindow = showWindow;
        _notifyIcon = new NotifyIcon {
            Icon = new Icon(iconPath),
            Text = "备忘录",
            Visible = true,
        };
        _notifyIcon.MouseUp += OnMouseUp;
        _notifyIcon.MouseDoubleClick += OnMouseDoubleClick;
    }

    private void OnMouseUp(object? sender, MouseEventArgs e) {
        if (e.Button == MouseButtons.Right) {
            _showMenu();
        }
    }

    private void OnMouseDoubleClick(object? sender, MouseEventArgs e) {
        if (e.Button == MouseButtons.Left) {
            _showWindow();
        }
    }

    public void Dispose() {
        _notifyIcon.MouseUp -= OnMouseUp;
        _notifyIcon.MouseDoubleClick -= OnMouseDoubleClick;
        _notifyIcon.Visible = false;
        _notifyIcon.Icon?.Dispose();
        _notifyIcon.Dispose();
    }
}
