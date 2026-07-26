using Avalonia;
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
        Loaded += (_, _) => this.AssignResizeCursors();
        _transition = new WindowTransitionController(this, this.FindControl<Border>("_popoutShell")!);
        _transition.PrepareOpen();
        Opened += (_, _) => _transition.PlayOpen();
        Closed += OnWindowClosed;
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

    // —— 边缘/四角拖拽缩放窗口 ——
    // 8 个透明手柄（4 边 + 4 角）共用一个 handler，靠 Tag 区分要缩放的哪条边/哪个角。
    private void OnResizeHandlePointerPressed(object? sender, PointerPressedEventArgs e) {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (sender is not Border handle || handle.Tag is not string tag) return;
        var edge = tag switch {
            "Top"         => WindowEdge.North,
            "Bottom"      => WindowEdge.South,
            "Left"        => WindowEdge.West,
            "Right"       => WindowEdge.East,
            "TopLeft"     => WindowEdge.NorthWest,
            "TopRight"    => WindowEdge.NorthEast,
            "BottomLeft"  => WindowEdge.SouthWest,
            "BottomRight" => WindowEdge.SouthEast,
            _             => WindowEdge.North
        };
        BeginResizeDrag(edge, e);
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

        if (button.Content is PathIcon pi) {
            var target = _isPinned ? -45 : 0;
            MotionAnimations.AnimateRotation(pi, this, target, animate: IsVisible);
        }
    }

    private void OnTimeTapped(object? sender, TappedEventArgs e) {
        _showFullTime = !_showFullTime;
        var full = this.FindControl<TextBlock>("_timeFullText")!;
        var shortText = this.FindControl<TextBlock>("_timeShortText")!;
        var fromFull = full.Opacity;
        var fromShort = shortText.Opacity;
        var toFull = _showFullTime ? 1 : 0;
        var toShort = _showFullTime ? 0 : 1;
        MotionAnimations.Start(full, this, MotionPreferences.StandardDuration, new CubicEaseOut(), progress => {
            full.Opacity = MotionAnimations.Lerp(fromFull, toFull, progress);
            shortText.Opacity = MotionAnimations.Lerp(fromShort, toShort, progress);
        });
        e.Handled = true;
    }

    private void OnWindowClosed(object? sender, EventArgs e) {
        _transition.Cancel();
        if (this.FindControl<Button>("_pinButton")?.Content is PathIcon pinIcon)
            MotionAnimations.Cancel(pinIcon);
        var full = this.FindControl<TextBlock>("_timeFullText");
        if (full != null) MotionAnimations.Cancel(full);
    }
}
