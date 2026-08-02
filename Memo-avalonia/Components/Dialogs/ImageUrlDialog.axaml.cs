using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Memo.Services;
using Memo.Utils;

namespace Memo.Components.Dialogs;

public partial class ImageUrlDialog : Window {
    public ImageUrlDialog() {
        InitializeComponent();
        Opened += (_, _) => Dispatcher.UIThread.Post(() => this.FindControl<TextBox>("_urlBox")!.Focus());
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e) {
        if (TitleBarDragHelper.CanStartDrag(this, e)) BeginMoveDrag(e);
    }

    private void OnInsertClick(object? sender, RoutedEventArgs e) => Submit();
    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private void OnUrlKeyDown(object? sender, KeyEventArgs e) {
        if (e.Key == Key.Enter) {
            Submit();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape) {
            Close(null);
            e.Handled = true;
        }
    }

    private void Submit() {
        var url = this.FindControl<TextBox>("_urlBox")!.Text?.Trim();
        if (!MarkdownImageStore.IsSafeRemoteImageUri(url)) {
            this.FindControl<TextBlock>("_errorText")!.Opacity = 1;
            return;
        }
        Close(url);
    }
}
