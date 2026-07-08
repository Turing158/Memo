using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Memo.Models;
using Memo.UI;

namespace Memo.Components.Dialogs;

public partial class CloseActionDialog : Window{
    private WindowTransitionController? _transition;
    private bool _isClosingAfterTransition;
    private CloseButtonAction _selectedAction = CloseButtonAction.MinimizeToTray;

    public CloseActionDialog() {
        InitializeComponent();
        _transition = new WindowTransitionController(this, this.FindControl<Border>("_dialogShell")!);
        _transition.PrepareOpen();
        Opened += (_, _) => _transition.PlayOpen();
        ApplySelection();
    }

    private void OnMinimizeToTrayOptionClick(object? sender, RoutedEventArgs e) {
        _selectedAction = CloseButtonAction.MinimizeToTray;
        ApplySelection();
    }

    private void OnCloseAppOptionClick(object? sender, RoutedEventArgs e) {
        _selectedAction = CloseButtonAction.Close;
        ApplySelection();
    }

    private void ApplySelection() {
        this.FindControl<ToggleButton>("_minimizeToTrayOption")!.IsChecked = _selectedAction == CloseButtonAction.MinimizeToTray;
        this.FindControl<ToggleButton>("_closeAppOption")!.IsChecked = _selectedAction == CloseButtonAction.Close;
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e) {
        CloseWithTransition(_selectedAction);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) {
        CloseWithTransition(null);
    }

    private void CloseWithTransition(CloseButtonAction? result) {
        if (_isClosingAfterTransition) return;
        _isClosingAfterTransition = true;

        if (_transition == null) {
            Close(result);
            return;
        }

        _transition.CloseAfterTransition(() => Close(result));
    }
}
