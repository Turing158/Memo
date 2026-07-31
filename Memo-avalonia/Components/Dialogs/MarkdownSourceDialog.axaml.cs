using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Memo.Components.Dialogs;

public partial class MarkdownSourceDialog : Window {
    public MarkdownSourceDialog() : this(string.Empty) { }
    public MarkdownSourceDialog(string markdown) {
        InitializeComponent();
        _sourceBox.Text = markdown;
        Opened += (_, _) => Dispatcher.UIThread.Post(() => _sourceBox.Focus());
    }
    private void OnConfirm(object? sender, RoutedEventArgs e) => Close(_sourceBox.Text ?? string.Empty);
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
    private void OnKeyDown(object? sender, KeyEventArgs e) {
        if (e.Key == Key.Escape) { Close(null); e.Handled = true; }
        else if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control)) { Close(_sourceBox.Text ?? string.Empty); e.Handled = true; }
    }
}
