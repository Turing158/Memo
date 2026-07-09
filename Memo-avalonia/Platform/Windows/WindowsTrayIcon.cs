using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace Memo.Platform.Windows;

internal sealed class WindowsTrayIcon : IDisposable {
    private readonly NotifyIcon _notifyIcon;
    private readonly Action _showMenu;
    private readonly Action _showWindow;
    private bool _traySingleClickToShow;

    public WindowsTrayIcon(Action showMenu, Action showWindow) {
        _showMenu = showMenu;
        _showWindow = showWindow;
        _notifyIcon = new NotifyIcon {
            Icon = LoadIconFromEmbeddedResource(),
            Text = "备忘录",
            Visible = true,
        };
        _notifyIcon.MouseUp += OnMouseUp;
        _notifyIcon.MouseClick += OnMouseClick;
        _notifyIcon.MouseDoubleClick += OnMouseDoubleClick;
    }

    /// <summary>设置托盘图标是否单击显示主界面（false 则使用双击，与旧版行为一致）。</summary>
    public bool TraySingleClickToShow {
        get => _traySingleClickToShow;
        set {
            if (_traySingleClickToShow == value) return;
            _traySingleClickToShow = value;
        }
    }

    private static Icon LoadIconFromEmbeddedResource() {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith("appicon.ico", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Embedded resource 'appicon.ico' not found in assembly.");

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Could not load embedded resource '{resourceName}'.");

        var bytes = new byte[stream.Length];
        stream.Read(bytes, 0, bytes.Length);
        // MemoryStream wraps a managed byte array; it outlives the Icon and needs no disposal.
        return new Icon(new MemoryStream(bytes));
    }

    private void OnMouseUp(object? sender, MouseEventArgs e) {
        if (e.Button == MouseButtons.Right) {
            _showMenu();
        }
    }

    private void OnMouseClick(object? sender, MouseEventArgs e) {
        // 单击显示模式下，左键单击即显示主界面
        if (_traySingleClickToShow && e.Button == MouseButtons.Left) {
            _showWindow();
        }
    }

    private void OnMouseDoubleClick(object? sender, MouseEventArgs e) {
        // 双击显示模式（默认）下，左键双击才显示主界面
        if (!_traySingleClickToShow && e.Button == MouseButtons.Left) {
            _showWindow();
        }
    }

    public void Dispose() {
        _notifyIcon.MouseUp -= OnMouseUp;
        _notifyIcon.MouseClick -= OnMouseClick;
        _notifyIcon.MouseDoubleClick -= OnMouseDoubleClick;
        _notifyIcon.Visible = false;
        _notifyIcon.Icon?.Dispose();
        _notifyIcon.Dispose();
    }
}
