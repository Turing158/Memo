using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Memo.Models;
using Memo.UI;
using Memo.Utils;
using System;

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
        Closed += (_, _) => _transition?.Cancel();
        var selector = this.FindControl<SegmentedSelector>("_closeActionSelector")!;
        selector.Options = new[] {
            new SegmentedSelectorOption(nameof(CloseButtonAction.MinimizeToTray), "最小化托盘"),
            new SegmentedSelectorOption(nameof(CloseButtonAction.Close), "关闭"),
        };
        selector.SelectedKey = _selectedAction.ToString();
        selector.SelectionChanged += OnSelectionChanged;
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e) {
        if (TitleBarDragHelper.CanStartDrag(this, e)) BeginMoveDrag(e);
    }

    private void OnSelectionChanged(object? sender, SegmentedSelectionChangedEventArgs e) {
        if (Enum.TryParse<CloseButtonAction>(e.NewKey, out var action)) _selectedAction = action;
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
