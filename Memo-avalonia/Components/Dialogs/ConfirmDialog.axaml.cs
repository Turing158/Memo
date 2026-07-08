using Avalonia.Controls;
using Avalonia.Interactivity;

using Memo.UI;

namespace Memo.Components.Dialogs;

public partial class ConfirmDialog : Window{
    private WindowTransitionController? _transition;
    private bool _isClosingAfterTransition;

    public ConfirmDialog() {
        InitializeComponent();
        _transition = new WindowTransitionController(this, this.FindControl<Border>("_confirmShell")!);
        _transition.PrepareOpen();
        Opened += (_, _) => _transition.PlayOpen();
    }

    public ConfirmDialog(string title, string message)
        : this() {
        this.FindControl<TextBlock>("_titleText")!.Text = title;
        this.FindControl<TextBlock>("_messageText")!.Text = message;
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e) => CloseWithTransition(true);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => CloseWithTransition(false);

    private void CloseWithTransition(bool result) {
        if (_isClosingAfterTransition) return;
        _isClosingAfterTransition = true;
        if (_transition == null) {
            Close(result);
            return;
        }
        _transition.CloseAfterTransition(() => Close(result));
    }
}
