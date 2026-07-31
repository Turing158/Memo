using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Memo.Components.Dialogs;
public sealed record LinkEditValue(string Label, string Url);
public partial class LinkEditDialog : Window {
    public LinkEditDialog() : this(string.Empty, "https://") { }
    public LinkEditDialog(string label, string url) { InitializeComponent(); _labelBox.Text = label; _urlBox.Text = url; Opened += (_, _) => Dispatcher.UIThread.Post(() => _labelBox.Focus()); }
    private void OnConfirm(object? s, RoutedEventArgs e) { var label = _labelBox.Text?.Trim(); var url = _urlBox.Text?.Trim(); if (!string.IsNullOrWhiteSpace(label) && !string.IsNullOrWhiteSpace(url)) Close(new LinkEditValue(label, url)); }
    private void OnCancel(object? s, RoutedEventArgs e) => Close(null);
    private void OnKeyDown(object? s, KeyEventArgs e) { if (e.Key == Key.Enter) { OnConfirm(s, e); e.Handled = true; } else if (e.Key == Key.Escape) { Close(null); e.Handled = true; } }
}
