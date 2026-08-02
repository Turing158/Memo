using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Collections;
// AvaloniaList<T> lives in Avalonia.Collections and is the concrete type
// for StrokeDashArray (an AvaloniaList<double>).
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Memo.Models;
using Memo.UI;
using Memo.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Memo.Behaviors;

/// <summary>
/// 长按拖拽重排管理器。
///
/// 核心设计：拖拽期间不修改集合（被拖项 Opacity=0 保持布局空间），
/// 悬浮项用 Popup 渲染在弹出层（ZIndex 高于窗口内容），
/// 占位框仍绘制在悬浮层 Canvas 上，
/// 相邻项让位与边缘自动滚动共用 TopLevel 渲染帧时钟。
///
/// 教训（来自 drag_debug.log 的前次失败尝试）：绝不在拖拽期间从集合移除项。
/// </summary>
public sealed class DragReorderManager : IDisposable {
    // ── 时间 / 阈值常量 ──
    private const double LongPressMs = 500;
    private const double MoveThreshold = 8;
    private const double EdgeThreshold = 40;
    private const double MaxScrollSpeedPerSecond = 750;
    private const double PlaceholderOpacity = 0.35;

    private readonly ItemsControl _items;
    private readonly ScrollViewer _scroller;
    private readonly Canvas _layer;
    private readonly MainViewModel _vm;
    private readonly Action<MemoItem, PixelPoint>? _requestPopout;

    // ── 拖拽状态 ──
    private bool _isDragging;
    private MemoItem? _dragItem;
    private Control? _dragContainer;
    private int _dragIndex;
    private int _insertIndex;
    private Point _grabOffset;
    private Point _downPos;
    private IPointer? _pressedPointer;
    private IPointer? _capturedPointer;

    // ── 卡片尺寸（拖拽开始时测量，用于占位框和让位动画） ──
    private double _cardContentHeight;
    private double _cardBottomGap;

    // ── 视觉元素 ──
    private Popup? _floatingPopup;
    private Border? _floatingOuter;
    private Border? _floatingCard;
    private TextBlock? _floatingTitle;
    private TextBlock? _floatingSubtitle;
    private TextBlock? _floatingTime;
    private ScaleTransform? _floatingScale;
    private Size _popupSize;
    private Control? _placeholder;

    // ── 让位动画状态（手动插值，兼容 Avalonia 11） ──
    private readonly List<SlideState> _slides = new();
    private readonly HashSet<Control> _slideTargets = new();
    private readonly List<Control> _dragContainers = new();
    private readonly Dictionary<Control, double> _containerLayoutYs = new();

    // ── 计时器 ──
    private readonly DispatcherTimer _longPressTimer;
    private ScrollContext? _scrollContext;
    private TopLevel? _frameTopLevel;
    private bool _frameRequested;
    private TimeSpan? _lastFrameTimestamp;
    private Point _latestPopupPointerInLayer;
    private bool _popupPositionDirty;
    private bool _isAttached;
    private bool _disposed;

    private static double SlideDurationMilliseconds =>
        MotionPreferences.Effective(TimeSpan.FromMilliseconds(180)).TotalMilliseconds;

    public DragReorderManager(ItemsControl items, ScrollViewer scroller, Canvas layer, MainViewModel vm, Action<MemoItem, PixelPoint>? requestPopout = null) {
        _items = items;
        _scroller = scroller;
        _layer = layer;
        _vm = vm;
        _requestPopout = requestPopout;

        _longPressTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(LongPressMs),
            DispatcherPriority.Normal,
            (_, _) => OnLongPressElapsed());

    }

    public bool IsDragging => _isDragging;

    // ═══════════════════════════════════════════════
    //  挂载事件
    // ═══════════════════════════════════════════════
    public void Attach() {
        if (_isAttached || _disposed) return;
        _isAttached = true;
        _items.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed,
            RoutingStrategies.Bubble, handledEventsToo: true);
        _items.AddHandler(InputElement.PointerMovedEvent, OnPointerMoved,
            RoutingStrategies.Bubble);
        _items.AddHandler(InputElement.PointerReleasedEvent, OnPointerReleased,
            RoutingStrategies.Bubble, handledEventsToo: true);
        _items.AddHandler(InputElement.PointerCaptureLostEvent, OnPointerCaptureLost,
            RoutingStrategies.Bubble, handledEventsToo: true);
    }

    public void Detach() {
        if (!_isAttached) return;
        _isAttached = false;
        _items.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
        _items.RemoveHandler(InputElement.PointerMovedEvent, OnPointerMoved);
        _items.RemoveHandler(InputElement.PointerReleasedEvent, OnPointerReleased);
        _items.RemoveHandler(InputElement.PointerCaptureLostEvent, OnPointerCaptureLost);
        _longPressTimer.Stop();
        _scrollContext = null;
        CleanupDragState();
    }

    public void Dispose() {
        if (_disposed) return;
        Detach();
        _disposed = true;
        _frameRequested = false;
        if (_floatingPopup != null) {
            _floatingPopup.Close();
            _floatingPopup.Child = null;
        }
        _floatingPopup = null;
        _floatingOuter = null;
        _floatingCard = null;
        _floatingTitle = null;
        _floatingSubtitle = null;
        _floatingTime = null;
        _floatingScale = null;
        _frameTopLevel = null;
    }

    // ═══════════════════════════════════════════════
    //  PointerPressed
    // ═══════════════════════════════════════════════
    private void OnPointerPressed(object? sender, PointerPressedEventArgs e) {
        if (_isDragging) return;
        if (!e.GetCurrentPoint(_items).Properties.IsLeftButtonPressed) return;

        var source = e.Source;
        if (SourceIsDeleteButton(source)) return;

        var item = FindItemFromSource(source);
        if (item == null) return;

        var container = FindContainerForItem(item);
        if (container == null) return;

        var pos = e.GetPosition(_items);

        _dragItem = item;
        _dragContainer = container;
        _dragIndex = _vm.Memos.IndexOf(item);
        _insertIndex = _dragIndex;
        _downPos = pos;
        _grabOffset = e.GetPosition(container);
        _pressedPointer = e.Pointer;

        _longPressTimer.Start();
    }

    // ═══════════════════════════════════════════════
    //  PointerMoved
    // ═══════════════════════════════════════════════
    private void OnPointerMoved(object? sender, PointerEventArgs e) {
        if (_dragItem == null) return;

        var pos = e.GetPosition(_items);

        if (!_isDragging) {
            if (Math.Abs(pos.X - _downPos.X) > MoveThreshold ||
                Math.Abs(pos.Y - _downPos.Y) > MoveThreshold) {
                CancelLongPress();
            }
            return;
        }

        UpdateFloatingPosition(e);
        var newIndex = ComputeInsertIndex(pos);

        if (newIndex != _insertIndex) {
            _insertIndex = newIndex;
            UpdatePlaceholderPosition();
            BeginNeighborSlides();
        }

        UpdateEdgeScroll(pos);
    }

    // ═══════════════════════════════════════════════
    //  PointerReleased
    // ═══════════════════════════════════════════════
    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e) {
        if (_dragItem == null) return;

        if (!_isDragging) {
            CancelLongPress();
            return;
        }

        var releasePoint = GetScreenPoint(e);
        EndDrag(releasePoint, IsOutsideMainWindow(releasePoint));
    }

    private void OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) {
        if (!_isDragging) {
            CancelLongPress();
            return;
        }

        EndDrag(null, requestPopout: false);
    }

    // ═══════════════════════════════════════════════
    //  长按到时 → 进入拖拽态
    // ═══════════════════════════════════════════════
    private void OnLongPressElapsed() {
        _longPressTimer.Stop();
        if (_dragItem == null || _dragContainer == null) return;

        _isDragging = true;
        CaptureDragPointer();

        _dragContainers.Clear();
        _dragContainers.AddRange(GetContainersInOrder());
        CacheContainerLayoutPositions();

        // 测量卡片真实内容高度和底部间距
        ComputeCardDimensions();

        var size = new Size(_dragContainer.Bounds.Width, _cardContentHeight);

        if (size.Width < 1 || size.Height < 1) {
            Dispatcher.UIThread.Post(() => {
                if (_dragContainer != null) {
                    ComputeCardDimensions();
                    var s = new Size(_dragContainer.Bounds.Width, _cardContentHeight);
                    BeginDragVisuals(s);
                }
            }, DispatcherPriority.Render);
        }
        else {
            BeginDragVisuals(size);
        }
    }

    private void BeginDragVisuals(Size size) {
        if (_dragContainer == null || _dragItem == null) return;

        // 1) 手动构建浮空卡片 UI（不截取快照），获取原始卡片尺寸用于定位
        var memoCard = _dragContainer.GetVisualChildren()
                         .OfType<Border>()
                         .FirstOrDefault(b => b.Classes.Contains("MemoCard"));
        var popupSize = memoCard != null
            ? memoCard.Bounds.Size
            : _dragContainer.Bounds.Size;
        CreateFloatingPopup(popupSize);
        _dragContainer.Opacity = 0;

        if (_floatingPopup != null)
            _floatingPopup.Open();
        _latestPopupPointerInLayer = _items.TranslatePoint(_downPos, _layer) ?? new Point(0, 0);
        _popupPositionDirty = true;
        StartVisualFrameLoop();

        // 2) 占位框使用内容高度 + 底部间距
        _placeholder = CreatePlaceholder(size);
        if (_placeholder != null)
            _layer.Children.Add(_placeholder);

        UpdatePlaceholderPosition();
    }

    // ═══════════════════════════════════════════════
    //  落位
    // ═══════════════════════════════════════════════
    private void EndDrag(PixelPoint? releasePoint, bool requestPopout) {
        _longPressTimer.Stop();
        _scrollContext = null;

        var dragged = _dragItem;
        var startIndex = _dragIndex;
        var targetIndex = _insertIndex;

        CleanupDragState();

        if (dragged == null) return;

        if (requestPopout && releasePoint.HasValue && _requestPopout != null && _vm.Memos.Contains(dragged)) {
            _requestPopout(dragged, releasePoint.Value);
            return;
        }

        if (!requestPopout && targetIndex != startIndex) {
            _vm.MoveItem(dragged.Id, targetIndex);
        }
    }

    private void CleanupDragState() {
        var container = _dragContainer;
        _isDragging = false;

        ReleaseDragPointer();
        RemoveDragVisuals();

        if (container != null)
            container.Opacity = 1;

        _dragItem = null;
        _dragContainer = null;
        _placeholder = null;
        _pressedPointer = null;
        _dragContainers.Clear();
        _containerLayoutYs.Clear();
        _lastFrameTimestamp = null;
    }

    private void CancelLongPress() {
        _longPressTimer.Stop();
        ReleaseDragPointer();
        _dragItem = null;
        _dragContainer = null;
        _pressedPointer = null;
        _isDragging = false;
    }

    // ═══════════════════════════════════════════════
    //  悬浮项 — Popup 实现（手动构建卡片 UI）
    // ═══════════════════════════════════════════════
    private void CreateFloatingPopup(Size size) {
        if (_dragItem == null) return;
        if (size.Width < 1 || size.Height < 1) return;

        // 解析主题资源笔刷
        var accentBrush = (IBrush?)Application.Current!.Resources["AccentPrimaryBrush"];
        var surfaceBrush = (IBrush?)Application.Current!.Resources["SurfacePrimaryBrush"];
        var borderBrush = (IBrush?)Application.Current!.Resources["BorderDefaultBrush"];
        var textPrimary = (IBrush?)Application.Current!.Resources["TextPrimaryBrush"];
        var textSecondary = (IBrush?)Application.Current!.Resources["TextSecondaryBrush"];
        var textTertiary = (IBrush?)Application.Current!.Resources["TextTertiaryBrush"];
        if (surfaceBrush == null) surfaceBrush = Brushes.White;
        if (borderBrush == null) borderBrush = Brushes.LightGray;

        EnsureFloatingPopup(accentBrush, surfaceBrush, borderBrush, textPrimary, textSecondary, textTertiary);
        if (_floatingPopup == null || _floatingOuter == null || _floatingCard == null
            || _floatingTitle == null || _floatingSubtitle == null || _floatingTime == null
            || _floatingScale == null) return;

        const double shadowPad = 18;
        _floatingCard.Width = size.Width;
        _floatingCard.Height = size.Height;
        _floatingOuter.Width = size.Width + (shadowPad * 2);
        _floatingOuter.Height = size.Height + (shadowPad * 2);
        _popupSize = new Size(_floatingOuter.Width, _floatingOuter.Height);
        _floatingTitle.Text = _dragItem.Title;
        _floatingSubtitle.Text = _dragItem.Subtitle;
        var hasSubtitle = !string.IsNullOrEmpty(_dragItem.Subtitle);
        _floatingSubtitle.IsVisible = hasSubtitle;
        _floatingTime.Text = _dragItem.RelativeTime;
        _floatingOuter.Opacity = 1;
        _floatingScale.ScaleX = 1;
        _floatingScale.ScaleY = 1;
    }

    private void EnsureFloatingPopup(
        IBrush? accentBrush,
        IBrush surfaceBrush,
        IBrush borderBrush,
        IBrush? textPrimary,
        IBrush? textSecondary,
        IBrush? textTertiary) {
        if (_floatingPopup != null) return;
        const double shadowPad = 18;

        _floatingTitle = new TextBlock {
            FontWeight = FontWeight.SemiBold, FontSize = 14, Foreground = textPrimary,
            TextTrimming = TextTrimming.CharacterEllipsis, LineHeight = 20,
            Margin = new Thickness(0, 0, 8, 0), IsHitTestVisible = false,
        };
        _floatingSubtitle = new TextBlock {
            FontSize = 12, Foreground = textSecondary, TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, LineHeight = 17,
            LetterSpacing = 0.1, IsHitTestVisible = false,
        };
        _floatingTime = new TextBlock {
            [Grid.ColumnProperty] = 1, FontSize = 10.5, Foreground = textTertiary,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0), LetterSpacing = 0.3, IsHitTestVisible = false,
        };

        var details = new Grid {
            [Grid.RowProperty] = 1, ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 5, 0, 0), IsHitTestVisible = false,
        };
        details.Children.Add(_floatingSubtitle);
        details.Children.Add(_floatingTime);
        var content = new Grid {
            [Grid.ColumnProperty] = 1, RowDefinitions = new RowDefinitions("Auto,Auto"),
            Margin = new Thickness(13, 14, 8, 14), IsHitTestVisible = false,
        };
        content.Children.Add(_floatingTitle);
        content.Children.Add(details);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("3,*"), IsHitTestVisible = false };
        grid.Children.Add(new Border {
            Background = accentBrush, CornerRadius = new CornerRadius(2), Width = 3,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
            Margin = new Thickness(0, 10, 0, 10), Opacity = 0.5, IsHitTestVisible = false,
        });
        grid.Children.Add(content);

        _floatingCard = new Border {
            CornerRadius = new CornerRadius(12), Background = surfaceBrush, BorderBrush = borderBrush,
            BorderThickness = new Thickness(1), IsHitTestVisible = false, Child = grid,
            Margin = new Thickness(shadowPad),
            BoxShadow = new BoxShadows(new BoxShadow {
                OffsetX = 0, OffsetY = 8, Blur = 20, Color = Color.FromArgb(60, 0, 0, 0),
            }),
        };
        _floatingScale = new ScaleTransform(1, 1);
        _floatingOuter = new Border {
            Background = Brushes.Transparent, IsHitTestVisible = false, Child = _floatingCard,
            RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            RenderTransform = _floatingScale,
        };
        _floatingPopup = new Popup {
            Placement = PlacementMode.AnchorAndGravity, PlacementAnchor = PopupAnchor.TopLeft,
            PlacementGravity = PopupGravity.TopLeft, PlacementTarget = _layer,
            IsLightDismissEnabled = false, OverlayDismissEventPassThrough = true,
            OverlayInputPassThroughElement = _items, Child = _floatingOuter,
        };
        _floatingPopup.Opened += (_, _) => {
            if (_floatingOuter == null || _floatingScale == null) return;
            var topLevel = TopLevel.GetTopLevel(_floatingOuter);
            if (topLevel == null) {
                _floatingScale.ScaleX = 1.02;
                _floatingScale.ScaleY = 1.02;
                _floatingOuter.Opacity = 0.8;
                return;
            }
            topLevel.Background = Brushes.Transparent;
            var scale = _floatingScale;
            MotionAnimations.Start(_floatingOuter, topLevel, MotionPreferences.FastDuration,
                new CubicEaseOut(), progress => {
                    var value = 1 + (0.02 * progress);
                    scale.ScaleX = value;
                    scale.ScaleY = value;
                    _floatingOuter.Opacity = 1 - (0.2 * progress);
                });
        };
    }

    private void ApplyFloatingPopupPosition() {
        if (_floatingPopup == null || !_popupPositionDirty) return;
        _popupPositionDirty = false;
        _floatingPopup.HorizontalOffset = _latestPopupPointerInLayer.X + (_popupSize.Width / 2);
        _floatingPopup.VerticalOffset = _latestPopupPointerInLayer.Y + (_popupSize.Height / 2);
    }

    private void UpdateFloatingPosition(PointerEventArgs e) {
        _latestPopupPointerInLayer = e.GetPosition(_layer);
        _popupPositionDirty = true;
        StartVisualFrameLoop();
    }

    // ═══════════════════════════════════════════════
    //  占位框
    // ═══════════════════════════════════════════════
    private Control? CreatePlaceholder(Size size) {
        var brush = (IBrush?)Application.Current!.Resources["AccentSubtleBrush"];
        var borderBrush = (IBrush?)Application.Current!.Resources["AccentPrimaryBrush"];

        var border = new Border {
            Width = size.Width,
            Height = size.Height,
            Background = brush,
            Opacity = PlaceholderOpacity,
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(2),
            IsHitTestVisible = false,
        };
        if (borderBrush != null)
            border.BorderBrush = borderBrush;

        var path = new Path {
            Width = size.Width,
            Height = size.Height,
            Stroke = borderBrush,
            StrokeThickness = 2,
            Fill = null,
            IsHitTestVisible = false,
        };
        if (borderBrush != null) {
            path.StrokeDashArray = new AvaloniaList<double> { 4, 3 };
            path.Data = CreateRoundedRectGeometry(size.Width, size.Height, 12);
        }

        // Grid 本身比内容高出一个 _cardBottomGap，形成与下一项之间的间距。
        // 内容区（Border + Path）紧贴 Grid 顶部，底部留空。
        var grid = new Grid {
            Width = size.Width,
            Height = size.Height + _cardBottomGap,
            IsHitTestVisible = false,
        };
        grid.Children.Add(border);
        if (borderBrush != null)
            grid.Children.Add(path);

        return grid;
    }

    private static StreamGeometry CreateRoundedRectGeometry(double w, double h, double r) {
        var g = new StreamGeometry();
        using var ctx = g.Open();
        ctx.BeginFigure(new Point(r, 0), true);
        ctx.LineTo(new Point(w - r, 0));
        ctx.ArcTo(new Point(w, r), new Size(r, r), 0, false, SweepDirection.Clockwise);
        ctx.LineTo(new Point(w, h - r));
        ctx.ArcTo(new Point(w - r, h), new Size(r, r), 0, false, SweepDirection.Clockwise);
        ctx.LineTo(new Point(r, h));
        ctx.ArcTo(new Point(0, h - r), new Size(r, r), 0, false, SweepDirection.Clockwise);
        ctx.LineTo(new Point(0, r));
        ctx.ArcTo(new Point(r, 0), new Size(r, r), 0, false, SweepDirection.Clockwise);
        ctx.EndFigure(true);
        return g;
    }

    private void UpdatePlaceholderPosition() {
        if (_placeholder == null) return;

        var containers = ActiveContainers();
        if (containers.Count == 0) return;

        var targetY = ComputePlaceholderY(containers, _insertIndex);
        if (double.IsNaN(targetY)) {
            _placeholder.IsVisible = false;
            return;
        }
        _placeholder.IsVisible = true;

        var origin = _items.TranslatePoint(new Point(0, targetY), _layer);
        if (!origin.HasValue) return;

        Canvas.SetLeft(_placeholder, origin.Value.X + 5);
        Canvas.SetTop(_placeholder, origin.Value.Y);
    }

    /// <summary>
    /// 计算占位框在 _items 坐标系中的 Y。
    ///
    /// 注意：相邻项在让位动画期间会通过 RenderTransform (TranslateTransform)
    /// 产生视觉偏移，但占位框应位于该槽位的布局位置（即原始间隙），而非被
    /// 偏移后的视觉位置，否则向上拖拽时占位框会跟随被移开的项一起下落，
    /// 导致与鼠标位置不一致。
    ///
    /// 边界约束：当 _insertIndex 为首/尾时，大幅边缘滚动会导致对应容器
    /// 移出视口，使占位框跟随消失或位移出组件外。此处将首/尾位置的占位框
    /// Y 约束到 ScrollViewer 视口上下边界内，保证加速滚动时占位框始终可见。
    /// </summary>
    private double ComputePlaceholderY(List<Control> containers, int insertIndex) {
        if (containers.Count == 0) return double.NaN;
        if (insertIndex < 0 || insertIndex >= containers.Count) return double.NaN;
        var y = GetContainerY(containers[insertIndex]);
        // 减去 RenderTransform 偏移，还原布局位置
        var slideOff = (containers[insertIndex].RenderTransform as TranslateTransform)?.Y ?? 0;
        var result = y - slideOff - 5;

        // 边界约束：视口顶部 / 底部在 _items 坐标系中的位置
        var topBound = _scroller.Offset.Y;
        var bottomBound = _scroller.Offset.Y + _scroller.Viewport.Height
                          - (_cardContentHeight + _cardBottomGap);

        if (insertIndex == 0)
            result = Math.Max(result, topBound - 5);
        else if (insertIndex == containers.Count - 1)
            result = Math.Min(result, bottomBound);

        return result;
    }

    // ═══════════════════════════════════════════════
    //  插入索引计算
    // ═══════════════════════════════════════════════
    private int ComputeInsertIndex(Point pointerInItems) {
        var containers = ActiveContainers();
        if (containers.Count == 0) return 0;

        int insertPos = containers.Count;
        for (int i = 0; i < containers.Count; i++) {
            if (containers[i] == _dragContainer) continue;
            var midY = GetContainerY(containers[i]) + containers[i].Bounds.Height / 2;
            if (pointerInItems.Y < midY) {
                insertPos = i;
                break;
            }
        }

        if (insertPos <= _dragIndex)
            return insertPos;
        else
            return Math.Clamp(insertPos - 1, 0, _vm.Memos.Count - 1);
    }

    // ═══════════════════════════════════════════════
    //  相邻项让位动画（手动插值）
    // ═══════════════════════════════════════════════
    private void BeginNeighborSlides() {
        if (_dragContainer == null) return;

        var containers = ActiveContainers();
        if (containers.Count == 0) return;

        int lo = Math.Min(_dragIndex, _insertIndex);
        int hi = Math.Max(_dragIndex, _insertIndex);

        // 无位移（回到原槽）→ 全部在途让位项归零，避免残留偏移导致重叠
        if (lo == hi) {
            foreach (var s in _slides) {
                s.From = s.Current;
                s.To = 0;
                s.DurationMs = SlideDurationMilliseconds;
                s.Elapsed = 0;
            }
            StartVisualFrameLoop();
            return;
        }

        var slotSize = _cardContentHeight + _cardBottomGap;
        bool movingDown = _insertIndex > _dragIndex;

        // 先把不在范围内的 slide 归位
        _slideTargets.Clear();
        for (var idx = 0; idx < containers.Count; idx++) {
            var c = containers[idx];
            if (c == _dragContainer) continue;
            if (idx >= lo && idx <= hi)
                _slideTargets.Add(c);
        }

        // 把不在范围里的 slide 归位
        foreach (var s in _slides) {
            if (!_slideTargets.Contains(s.Control)) {
                s.From = s.Current;
                s.To = 0;
                s.DurationMs = SlideDurationMilliseconds;
                s.Elapsed = 0;
            }
        }

        for (var idx = 0; idx < containers.Count; idx++) {
            var c = containers[idx];
            if (c == _dragContainer) continue;
            if (idx < lo || idx > hi) continue;

            double target = movingDown ? -slotSize : slotSize;

            SlideState? existing = null;
            foreach (var slide in _slides) {
                if (!ReferenceEquals(slide.Control, c)) continue;
                existing = slide;
                break;
            }
            if (existing is { Control: not null }) {
                existing.From = existing.Current;
                existing.To = target;
                existing.DurationMs = SlideDurationMilliseconds;
                existing.Elapsed = 0;
            }
            else {
                var tf = c.RenderTransform as TranslateTransform ?? new TranslateTransform();
                c.RenderTransform = tf;
                _slides.Add(new SlideState {
                    Control = c,
                    Transform = tf,
                    From = tf.Y,
                    To = target,
                    Current = tf.Y,
                    DurationMs = SlideDurationMilliseconds,
                    Elapsed = 0,
                });
            }
        }

        StartVisualFrameLoop();
    }

    private void AdvanceSlides(double elapsedMilliseconds) {
        for (var i = _slides.Count - 1; i >= 0; i--) {
            var s = _slides[i];
            s.Elapsed += elapsedMilliseconds;
            var t = s.Elapsed >= s.DurationMs ? 1.0 : s.Elapsed / s.DurationMs;
            // CubicEaseInOut
            var eased = t < 0.5
                ? 4.0 * t * t * t
                : 1.0 - Math.Pow(-2.0 * t + 2.0, 3.0) / 2.0;

            s.Current = s.From + (s.To - s.From) * eased;
            s.Transform.Y = s.Current;

            if (t < 1) continue;
            s.Transform.Y = s.To;
            if (Math.Abs(s.To) < 0.001) {
                if (ReferenceEquals(s.Control.RenderTransform, s.Transform)) s.Control.RenderTransform = null;
                _slides.RemoveAt(i);
            }
            else {
                s.From = s.To;
                s.Elapsed = s.DurationMs;
            }
        }
    }

    // ═══════════════════════════════════════════════
    //  边缘滚动
    // ═══════════════════════════════════════════════
    private void UpdateEdgeScroll(Point pointerInItems) {
        var svOrigin = _scroller.TranslatePoint(new Point(0, 0), _items);
        if (!svOrigin.HasValue) return;
        var pointerInSv = pointerInItems - svOrigin.Value;

        var maxOffsetY = Math.Max(0, _scroller.Extent.Height - _scroller.Viewport.Height);
        if (TryGetEdgeScroll(
                pointerInSv.Y,
                _scroller.Bounds.Height,
                _scroller.Offset.Y,
                maxOffsetY,
                out var direction,
                out var strength)) {
            _scrollContext ??= new ScrollContext {
                PointerInItems = pointerInItems,
                Direction = direction,
                Strength = strength,
            };
            _scrollContext.PointerInItems = pointerInItems;
            _scrollContext.Direction = direction;
            _scrollContext.Strength = strength;
            StartVisualFrameLoop();
        }
        else {
            _scrollContext = null;
        }
    }

    private static bool TryGetEdgeScroll(
        double pointerY,
        double viewportHeight,
        double offsetY,
        double maxOffsetY,
        out int direction,
        out double strength) {
        double distance;
        if (pointerY < EdgeThreshold) {
            direction = -1;
            distance = EdgeThreshold - pointerY;
        }
        else if (pointerY > viewportHeight - EdgeThreshold) {
            direction = 1;
            distance = pointerY - (viewportHeight - EdgeThreshold);
        }
        else {
            direction = 0;
            strength = 0;
            return false;
        }

        strength = Math.Clamp(distance / EdgeThreshold, 0, 1);
        return direction < 0 ? offsetY > 0 : offsetY < maxOffsetY;
    }

    private void AdvanceEdgeScroll(double elapsedSeconds) {
        if (_scrollContext is not { } ctx) return;

        var delta = ctx.Direction * ctx.Strength * MaxScrollSpeedPerSecond * elapsedSeconds;
        var offset = _scroller.Offset;
        var maxOffsetY = Math.Max(0, _scroller.Extent.Height - _scroller.Viewport.Height);
        var canScroll = ctx.Direction < 0 ? offset.Y > 0 : offset.Y < maxOffsetY;
        if (!canScroll) {
            _scrollContext = null;
            return;
        }

        var newY = Math.Clamp(offset.Y + delta, 0, maxOffsetY);
        _scroller.Offset = new Vector(offset.X, newY);

        // 内容坐标随实际滚动量等量移动，避免写入 Offset 后强制查询视觉树坐标。
        var actualDeltaY = newY - offset.Y;
        ctx.PointerInItems = new Point(ctx.PointerInItems.X, ctx.PointerInItems.Y + actualDeltaY);

        var newIndex = ComputeInsertIndex(ctx.PointerInItems);
        if (newIndex != _insertIndex) {
            _insertIndex = newIndex;
            UpdatePlaceholderPosition();
            BeginNeighborSlides();
        }

        if (ctx.Direction < 0 ? newY <= 0 : newY >= maxOffsetY)
            _scrollContext = null;
    }

    private void StartVisualFrameLoop() {
        if (_frameRequested) return;
        _frameTopLevel ??= TopLevel.GetTopLevel(_items);
        if (_frameTopLevel == null) return;
        _frameRequested = true;
        _frameTopLevel.RequestAnimationFrame(OnVisualFrame);
    }

    private void OnVisualFrame(TimeSpan timestamp) {
        _frameRequested = false;
        if (_disposed) return;
        var elapsed = _lastFrameTimestamp.HasValue ? timestamp - _lastFrameTimestamp.Value : TimeSpan.Zero;
        _lastFrameTimestamp = timestamp;
        var milliseconds = Math.Clamp(elapsed.TotalMilliseconds, 0, 50);

        ApplyFloatingPopupPosition();
        AdvanceSlides(MotionPreferences.AnimationsEnabled ? milliseconds : double.MaxValue);
        AdvanceEdgeScroll(milliseconds / 1000);

        if (_scrollContext != null || HasActiveSlideAnimations() || _popupPositionDirty) {
            StartVisualFrameLoop();
        }
        else {
            _lastFrameTimestamp = null;
        }
    }

    private bool HasActiveSlideAnimations() {
        foreach (var slide in _slides) {
            if (slide.Elapsed < slide.DurationMs) return true;
        }
        return false;
    }

    private sealed class ScrollContext {
        public required Point PointerInItems;
        public required int Direction;
        public required double Strength;
    }

    private sealed class SlideState {
        public required Control Control;
        public required TranslateTransform Transform;
        public required double From;
        public required double To;
        public double Current;
        public double DurationMs;
        public double Elapsed;
    }

    // ═══════════════════════════════════════════════
    //  视觉元素清理
    // ═══════════════════════════════════════════════
    private void RemoveDragVisuals() {
        if (_floatingPopup != null) {
            _floatingPopup.Close();
            if (_floatingOuter != null) MotionAnimations.Cancel(_floatingOuter);
        }
        if (_placeholder != null) {
            _layer.Children.Remove(_placeholder);
            _placeholder = null;
        }
        foreach (var s in _slides)
            s.Control.RenderTransform = null;
        _slides.Clear();
    }

    // ═══════════════════════════════════════════════
    //  工具方法
    // ═══════════════════════════════════════════════

    private void CaptureDragPointer() {
        if (_pressedPointer == null) return;

        _pressedPointer.Capture(_items);
        _capturedPointer = _pressedPointer;
    }

    private void ReleaseDragPointer() {
        if (_capturedPointer == null) return;

        if (_capturedPointer.Captured == _items)
            _capturedPointer.Capture(null);
        _capturedPointer = null;
    }

    private PixelPoint? GetScreenPoint(PointerEventArgs e) {
        var topLevel = TopLevel.GetTopLevel(_items);
        if (topLevel == null) return null;

        return topLevel.PointToScreen(e.GetPosition(topLevel));
    }

    private bool IsOutsideMainWindow(PixelPoint? screenPoint) {
        if (!screenPoint.HasValue) return false;
        if (TopLevel.GetTopLevel(_items) is not Window window) return false;

        var screen = screenPoint.Value;
        var origin = window.Position;
        var scaling = window.RenderScaling;
        var width = Math.Max(1, (int)Math.Ceiling(window.ClientSize.Width * scaling));
        var height = Math.Max(1, (int)Math.Ceiling(window.ClientSize.Height * scaling));
        var bounds = new PixelRect(origin, new PixelSize(width, height));
        return !bounds.Contains(screen);
    }

    private List<Control> GetContainersInOrder() {
        var result = new List<Control>();
        foreach (var v in _items.GetVisualDescendants()) {
            if (v is Control c && IsMemoCardRoot(c))
                result.Add(c);
        }
        return result.OrderBy(GetContainerLayoutY).ToList();
    }

    private List<Control> ActiveContainers() =>
        _dragContainers.Count > 0 ? _dragContainers : GetContainersInOrder();

    private double GetContainerY(Control c) {
        if (_containerLayoutYs.TryGetValue(c, out var layoutY))
            return layoutY + ((c.RenderTransform as TranslateTransform)?.Y ?? 0);

        var pt = c.TranslatePoint(new Point(0, 0), _items);
        return pt.HasValue ? pt.Value.Y : c.Bounds.Y;
    }

    /// <summary>
    /// 容器的布局 Y（排除让位动画产生的 RenderTransform 偏移）。
    /// </summary>
    private double GetContainerLayoutY(Control c) {
        if (_containerLayoutYs.TryGetValue(c, out var layoutY))
            return layoutY;

        var visualY = GetContainerY(c);
        var slideOff = (c.RenderTransform as TranslateTransform)?.Y ?? 0;
        return visualY - slideOff;
    }

    private void CacheContainerLayoutPositions() {
        _containerLayoutYs.Clear();
        foreach (var container in _dragContainers) {
            var point = container.TranslatePoint(new Point(0, 0), _items);
            if (!point.HasValue) continue;

            var slideOffset = (container.RenderTransform as TranslateTransform)?.Y ?? 0;
            _containerLayoutYs[container] = point.Value.Y - slideOffset;
        }
    }

    /// <summary>
    /// 在拖拽开始时计算每张卡片的真实内容高度和底部间隙。
    ///
    /// 注意：IsMemoCardRoot 匹配到的 _dragContainer 是 ContentPresenter
    /// 而非 DataTemplate 根 Border。ContentPresenter 自身无 margin，
    /// _dragContainer.Margin.Bottom = 0 = 错误的间距。真正的底部 margin
    /// 在子 Border 上（Margin="0,0,0,10"），必须向下查找。
    /// </summary>
    private void ComputeCardDimensions() {
        if (_dragContainer == null) { _cardContentHeight = 100; _cardBottomGap = 10; return; }

        var containers = ActiveContainers();
        double boundsHeight = _dragContainer.Bounds.Height;

        // 优先通过相邻容器的布局位置差推导 slotHeight
        if (containers.Count >= 2) {
            int idx = containers.IndexOf(_dragContainer);
            if (idx >= 0 && idx < containers.Count - 1) {
                var yCur = GetContainerLayoutY(containers[idx]);
                var yNext = GetContainerLayoutY(containers[idx + 1]);
                var slotHeight = yNext - yCur;

                if (slotHeight > boundsHeight + 0.5) {
                    // Bounds 不含 margin → 内容高度就是 Bounds.Height
                    _cardContentHeight = boundsHeight;
                    _cardBottomGap = slotHeight - boundsHeight;
                }
                else {
                    // Bounds 含 margin（ContentPresenter），从子元素获取间距
                    _cardBottomGap = GetCardBottomMargin(_dragContainer);
                    _cardContentHeight = boundsHeight - _cardBottomGap;
                }
                return;
            }
        }

        // 回退：少于 2 项或只有最后一项
        _cardBottomGap = GetCardBottomMargin(_dragContainer);
        _cardContentHeight = Math.Max(boundsHeight - _cardBottomGap, 10);
    }

    /// <summary>
    /// 获取卡片底部的 margin/padding 间距。
    /// 当容器自身无 margin 时（ContentPresenter），递归查找视觉子树。
    /// </summary>
    private static double GetCardBottomMargin(Control container) {
        if (container.Margin.Bottom > 0) return container.Margin.Bottom;

        foreach (var v in container.GetVisualChildren()) {
            if (v is Control c && c.Margin.Bottom > 0)
                return c.Margin.Bottom;
        }
        return 10; // fallback — 与 MemoCard 默认 Margin.Bottom 一致
    }

    private Control? FindContainerForItem(MemoItem item) {
        foreach (var v in _items.GetVisualDescendants()) {
            if (v is Control c && IsMemoCardRoot(c) && ReferenceEquals(c.DataContext, item))
                return c;
        }
        return null;
    }

    private static bool IsMemoCardRoot(Control c) {
        if (c.DataContext is not MemoItem) return false;
        var parent = c.GetVisualParent();
        return parent == null || (parent as IDataContextProvider)?.DataContext is not MemoItem;
    }

    private static MemoItem? FindItemFromSource(object? source) {
        if (source is Visual vis) {
            foreach (var v in vis.GetVisualAncestors().Append(vis).OfType<IDataContextProvider>()) {
                if (v.DataContext is MemoItem m) return m;
            }
        }
        else if (source is ILogical log) {
            foreach (var l in log.GetLogicalAncestors().Append(log).OfType<IDataContextProvider>()) {
                if (l.DataContext is MemoItem m) return m;
            }
        }
        return null;
    }

    private static bool SourceIsDeleteButton(object? source) {
        if (source is Visual vis) {
            foreach (var v in vis.GetVisualAncestors().Append(vis)) {
                if (v is Control c && c.Classes.Contains("DeleteBtn"))
                    return true;
            }
        }
        return false;
    }
}
