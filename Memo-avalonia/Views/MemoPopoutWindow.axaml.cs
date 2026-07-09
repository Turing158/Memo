using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Memo.Models;
using Memo.UI;
using Memo.Utils;
using System;

namespace Memo.Views;

public partial class MemoPopoutWindow : Window {
    private readonly WindowTransitionController _transition;
    private Action<MemoItem, string>? _saveMemo;
    private MemoItem? _memo;
    private bool _isClosingAfterTransition;
    private bool _isPinned;
    private bool _isEditing;
    private bool _showFullTime;

    /// <summary>当前窗体关联的备忘录项。</summary>
    public MemoItem Memo => _memo!;

    public MemoPopoutWindow() {
        InitializeComponent();
        _transition = new WindowTransitionController(this, this.FindControl<Border>("_popoutShell")!);
        _transition.PrepareOpen();
        Opened += (_, _) => _transition.PlayOpen();
        SetupPinRotationTransition();
    }

    public MemoPopoutWindow(MemoItem memo, PixelPoint position, Action<MemoItem, string> saveMemo)
        : this() {
        _memo = memo;
        _saveMemo = saveMemo;
        DataContext = memo;
        UpdateTitle(memo);
        Position = position;
    }

    public bool IsPinned => _isPinned;

    public void TogglePinned() => SetPinned(!_isPinned);

    public void SetPinned(bool isPinned) {
        _isPinned = isPinned;
        Topmost = _isPinned;
        UpdatePinButtonVisual();
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e) {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) {
            BeginMoveDrag(e);
        }
    }

    private void OnPinToggle(object? sender, RoutedEventArgs e) => TogglePinned();

    private void OnCloseClick(object? sender, RoutedEventArgs e) {
        if (_isEditing)
            EndEdit(commit: true);
        CloseWithTransition();
    }

    private void OnContentDoubleTapped(object? sender, TappedEventArgs e) {
        if (_memo == null || _isEditing) return;

        var contentText = this.FindControl<TextBlock>("_contentText")!;
        BeginEdit(GetCaretIndexFromPoint(contentText, e.GetPosition(contentText)));
        e.Handled = true;
    }

    private void BeginEdit(int caretIndex) {
        if (_memo == null) return;

        _isEditing = true;

        var viewer = this.FindControl<ScrollViewer>("_contentViewer")!;
        var editor = this.FindControl<TextBox>("_editor")!;
        var noteSurface = this.FindControl<Border>("_noteSurface")!;
        editor.Text = _memo.Content;
        viewer.IsVisible = false;
        editor.IsVisible = true;
        noteSurface.Classes.Add("editing");

        Dispatcher.UIThread.Post(() => {
            editor.Focus();
            editor.CaretIndex = Math.Clamp(caretIndex, 0, editor.Text?.Length ?? 0);
        }, DispatcherPriority.Render);
    }

    private void OnEditorKeyDown(object? sender, KeyEventArgs e) {
        if (e.Key == Key.Escape) {
            EndEdit(commit: true);
            e.Handled = true;
        }
    }

    private void EndEdit(bool commit) {
        if (!_isEditing) return;

        var viewer = this.FindControl<ScrollViewer>("_contentViewer")!;
        var editor = this.FindControl<TextBox>("_editor")!;
        var noteSurface = this.FindControl<Border>("_noteSurface")!;

        if (commit && _memo != null && _saveMemo != null) {
            var content = editor.Text ?? string.Empty;
            _saveMemo(_memo, content);
            UpdateTitle(_memo);
        }

        editor.IsVisible = false;
        viewer.IsVisible = true;
        noteSurface.Classes.Remove("editing");
        _isEditing = false;
    }

    private void UpdateTitle(MemoItem memo) {
        var title = string.IsNullOrWhiteSpace(memo.Title) ? "备忘录" : memo.Title;
        Title = title;
        this.FindControl<TextBlock>("_titleText")!.Text = title;
    }

    private static int GetCaretIndexFromPoint(TextBlock textBlock, Point point) {
        var text = textBlock.Text ?? string.Empty;
        if (text.Length == 0) return 0;

        var layout = textBlock.TextLayout;
        var hit = layout.HitTestPoint(point);
        return Math.Clamp(hit.TextPosition, 0, text.Length);
    }

    private void CloseWithTransition() {
        if (_isClosingAfterTransition) return;
        _isClosingAfterTransition = true;
        _transition.CloseAfterTransition(() => Close());
    }

    private void UpdatePinButtonVisual() {
        var button = this.FindControl<Button>("_pinButton");
        if (button == null) return;

        button.Classes.Set("PinActive", _isPinned);

        if (button.Content is PathIcon pi && pi.RenderTransform is RotateTransform rt) {
            rt.Angle = _isPinned ? -45 : 0;
        }
    }

    private void SetupPinRotationTransition() {
        var button = this.FindControl<Button>("_pinButton");
        if (button?.Content is PathIcon pi && pi.RenderTransform is RotateTransform rt) {
            rt.Transitions = new Transitions {
                new DoubleTransition {
                    Property = RotateTransform.AngleProperty,
                    Duration = TimeSpan.FromMilliseconds(250),
                    Easing = new CubicEaseOut(),
                }
            };
        }
    }

    private void OnTimeTapped(object? sender, TappedEventArgs e) {
        _showFullTime = !_showFullTime;
        this.FindControl<TextBlock>("_timeFullText")!.Opacity = _showFullTime ? 1 : 0;
        this.FindControl<TextBlock>("_timeShortText")!.Opacity = _showFullTime ? 0 : 1;
        e.Handled = true;
    }
}
