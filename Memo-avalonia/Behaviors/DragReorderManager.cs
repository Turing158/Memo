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
using Memo.ViewModels;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Memo.Behaviors;

/// <summary>
/// 长按拖拽重排管理器。
///
/// 核心设计：拖拽期间不修改集合（被拖项 Opacity=0 保持布局空间），
/// 悬浮项用 Popup 渲染在弹出层（ZIndex 高于窗口内容），
/// 占位框仍绘制在悬浮层 Canvas 上，
/// 相邻项让位用 TranslateTransform + DispatcherTimer 插值动画。
///
/// 教训（来自 drag_debug.log 的前次失败尝试）：绝不在拖拽期间从集合移除项。
/// </summary>
public sealed class DragReorderManager {
    // ── 时间 / 阈值常量 ──
    private const double LongPressMs = 500;
    private const double MoveThreshold = 8;
    private const double EdgeThreshold = 40;
    private const double MaxScrollSpeed = 12;
    private const double AnimMs = 180;
    private const double PlaceholderOpacity = 0.35;
    private const double PopupOpacity = 0.7;

    private readonly ItemsControl _items;
    private readonly ScrollViewer _scroller;
    private readonly Canvas _layer;
    private readonly MainViewModel _vm;

    // ── 拖拽状态 ──
    private bool _isDragging;
    private MemoItem? _dragItem;
    private Control? _dragContainer;
    private int _dragIndex;
    private int _insertIndex;
    private Point _grabOffset;
    private Point _downPos;

    // ── 卡片尺寸（拖拽开始时测量，用于占位框和让位动画） ──
    private double _cardContentHeight;
    private double _cardBottomGap;

    // ── 视觉元素 ──
    private Popup? _floatingPopup;
    private Size _popupSize;
    private Control? _placeholder;

    // ── 让位动画状态（手动插值，兼容 Avalonia 11） ──
    private readonly List<SlideState> _slides = new();
    private readonly DispatcherTimer _animTimer;

    // ── 计时器 ──
    private readonly DispatcherTimer _longPressTimer;
    private readonly DispatcherTimer _scrollTimer;

    public DragReorderManager(ItemsControl items, ScrollViewer scroller, Canvas layer, MainViewModel vm) {
        _items = items;
        _scroller = scroller;
        _layer = layer;
        _vm = vm;

        _longPressTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(LongPressMs),
            DispatcherPriority.Normal,
            (_, _) => OnLongPressElapsed());

        _scrollTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(16),
            DispatcherPriority.Render,
            (_, _) => OnScrollTick());

        _animTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(16),
            DispatcherPriority.Render,
            (_, _) => OnAnimTick());
    }

    public bool IsDragging => _isDragging;

    // ═══════════════════════════════════════════════
    //  挂载事件
    // ═══════════════════════════════════════════════
    public void Attach() {
        _items.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed,
            RoutingStrategies.Bubble, handledEventsToo: true);
        _items.AddHandler(InputElement.PointerMovedEvent, OnPointerMoved,
            RoutingStrategies.Bubble);
        _items.AddHandler(InputElement.PointerReleasedEvent, OnPointerReleased,
            RoutingStrategies.Bubble, handledEventsToo: true);
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

        EndDrag();
    }

    // ═══════════════════════════════════════════════
    //  长按到时 → 进入拖拽态
    // ═══════════════════════════════════════════════
    private void OnLongPressElapsed() {
        _longPressTimer.Stop();
        if (_dragItem == null || _dragContainer == null) return;

        _isDragging = true;

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
        UpdateFloatingPopupPosition(e: null);

        // 2) 占位框使用内容高度 + 底部间距
        _placeholder = CreatePlaceholder(size);
        if (_placeholder != null)
            _layer.Children.Add(_placeholder);

        UpdatePlaceholderPosition();
    }

    // ═══════════════════════════════════════════════
    //  落位
    // ═══════════════════════════════════════════════
    private void EndDrag() {
        _longPressTimer.Stop();
        _scrollTimer.Stop();

        var dragged = _dragItem;
        var startIndex = _dragIndex;
        var targetIndex = _insertIndex;

        RemoveDragVisuals();

        var container = _dragContainer;
        if (container != null)
            container.Opacity = 1;

        // 让位项归位
        foreach (var s in _slides) {
            s.From = s.Current;
            s.To = 0;
            s.DurationMs = AnimMs;
            s.Elapsed = 0;
        }
        if (_slides.Count > 0 && !_animTimer.IsEnabled)
            _animTimer.Start();

        _isDragging = false;
        _dragItem = null;
        _dragContainer = null;
        _floatingPopup = null;
        _placeholder = null;

        if (dragged != null && targetIndex != startIndex) {
            _vm.MoveItem(dragged.Id, targetIndex);
        }
    }

    private void CancelLongPress() {
        _longPressTimer.Stop();
        _dragItem = null;
        _dragContainer = null;
        _isDragging = false;
    }

    // ═══════════════════════════════════════════════
    //  悬浮项 — Popup 实现（手动构建卡片 UI）
    // ═══════════════════════════════════════════════
    private void CreateFloatingPopup(Size size) {
        if (_dragItem == null) return;
        if (size.Width < 1 || size.Height < 1) return;

        _popupSize = size;

        // 解析主题资源笔刷
        var accentBrush = (IBrush?)Application.Current!.Resources["AccentPrimaryBrush"];
        var surfaceBrush = (IBrush?)Application.Current!.Resources["SurfacePrimaryBrush"];
        var borderBrush = (IBrush?)Application.Current!.Resources["BorderDefaultBrush"];
        var textPrimary = (IBrush?)Application.Current!.Resources["TextPrimaryBrush"];
        var textSecondary = (IBrush?)Application.Current!.Resources["TextSecondaryBrush"];
        var textTertiary = (IBrush?)Application.Current!.Resources["TextTertiaryBrush"];
        if (surfaceBrush == null) surfaceBrush = Brushes.White;
        if (borderBrush == null) borderBrush = Brushes.LightGray;

        // ── 阴影边距（Blur=20 → 半径=10，OffsetY=8 → 底部多8） ──
        double shadowPad = 18;

        // ── 卡片本体（背景 + 边框 + 圆角） ──
        var card = new Border {
            Width = size.Width,
            Height = size.Height,
            CornerRadius = new CornerRadius(12),
            Background = surfaceBrush,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(1),
            IsHitTestVisible = false,
            BoxShadow = new BoxShadows(new BoxShadow {
                OffsetX = 0,
                OffsetY = 8,
                Blur = 20,
                Color = Color.FromArgb(60, 0, 0, 0),
            }),
        };

        // ── 透明外层容留阴影空间 ──
        var outer = new Border {
            Width = size.Width + shadowPad * 2,
            Height = size.Height + shadowPad * 2,
            Background = Brushes.Transparent,
            IsHitTestVisible = false,
            Child = card,
        };
        card.Margin = new Thickness(shadowPad);

        // ── 入场动画：从 1× / 不透明 → 1.02× / 0.8 不透明 ──
        outer.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        outer.RenderTransform = TransformOperations.Parse("scale(1)");
        outer.Opacity = 1.0;
        outer.Transitions = new Transitions {
            new TransformOperationsTransition {
                Property = Visual.RenderTransformProperty,
                Duration = TimeSpan.FromMilliseconds(150),
                Easing = new CubicEaseOut(),
            },
            new DoubleTransition {
                Property = Visual.OpacityProperty,
                Duration = TimeSpan.FromMilliseconds(150),
                Easing = new CubicEaseOut(),
            },
        };

        // Popup 总尺寸（含阴影间距）
        _popupSize = new Size(outer.Width, outer.Height);

        // ── 列：装饰条 | 内容区 ──
        var grid = new Grid {
            ColumnDefinitions = new ColumnDefinitions("3,*,Auto"),
            IsHitTestVisible = false,
        };

        // 左侧 Accent 装饰条
        grid.Children.Add(new Border {
            [Grid.ColumnProperty] = 0,
            Background = accentBrush,
            CornerRadius = new CornerRadius(2),
            Width = 3,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
            Margin = new Thickness(0, 10, 0, 10),
            Opacity = 0.5,
            IsHitTestVisible = false,
        });

        // ── 内容区 ──
        var contentGrid = new Grid {
            [Grid.ColumnProperty] = 1,
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            Margin = new Thickness(13, 14, 8, 14),
            IsHitTestVisible = false,
        };

        // 标题行
        var titleBlock = new TextBlock {
            Text = _dragItem.Title,
            FontWeight = FontWeight.SemiBold,
            FontSize = 14,
            Foreground = textPrimary,
            TextTrimming = TextTrimming.CharacterEllipsis,
            LineHeight = 20,
            Margin = new Thickness(0, 0, 8, 0),
            IsHitTestVisible = false,
        };
        Grid.SetColumn(titleBlock, 0);
        contentGrid.Children.Add(titleBlock);

        // 副标题 + 时间行（仅在副标题不为空时显示整行）
        var hasSubtitle = !string.IsNullOrEmpty(_dragItem.Subtitle);
        if (hasSubtitle) {
            var subGrid = new Grid {
                [Grid.RowProperty] = 1,
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Margin = new Thickness(0, 5, 0, 0),
                IsHitTestVisible = false,
            };

            var subtitleBlock = new TextBlock {
                Text = _dragItem.Subtitle,
                FontSize = 12,
                Foreground = textSecondary,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                LineHeight = 17,
                LetterSpacing = 0.1,
                IsHitTestVisible = false,
            };
            Grid.SetColumn(subtitleBlock, 0);
            subGrid.Children.Add(subtitleBlock);

            var timeBlock = new TextBlock {
                Text = _dragItem.RelativeTime,
                FontSize = 10.5,
                Foreground = textTertiary,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0),
                LetterSpacing = 0.3,
                IsHitTestVisible = false,
            };
            Grid.SetColumn(timeBlock, 1);
            subGrid.Children.Add(timeBlock);

            contentGrid.Children.Add(subGrid);
        }
        else {
            // 无副标题：单独显示时间行
            var timeBlock = new TextBlock {
                [Grid.RowProperty] = 1,
                Text = _dragItem.RelativeTime,
                FontSize = 10.5,
                Foreground = textTertiary,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Margin = new Thickness(0, 5, 0, 0),
                LetterSpacing = 0.3,
                IsHitTestVisible = false,
            };
            contentGrid.Children.Add(timeBlock);
        }

        grid.Children.Add(contentGrid);
        card.Child = grid;

        // ── Popup ──
        _floatingPopup = new Popup {
            Placement = PlacementMode.AnchorAndGravity,
            PlacementAnchor = PopupAnchor.TopLeft,
            PlacementGravity = PopupGravity.TopLeft,
            PlacementTarget = _layer,
            IsLightDismissEnabled = false,
            OverlayDismissEventPassThrough = true,
            OverlayInputPassThroughElement = _items,
            Child = outer,
        };

        // 打开后执行入场动画 + 消除 Popup 宿主窗口白色底色
        _floatingPopup.Opened += (_, _) => {
            var topLevel = TopLevel.GetTopLevel(outer);
            if (topLevel != null)
                topLevel.Background = Brushes.Transparent;

            // 触发 Transition：scale 1→1.08，opacity 1→0.7
            outer.RenderTransform = TransformOperations.Parse("scale(1.02)");
            outer.Opacity = 0.8;
        };
    }

    private void UpdateFloatingPopupPosition(PointerEventArgs? e) {
        if (_floatingPopup == null) return;

        Point pointerInLayer;

        if (e != null)
            pointerInLayer = e.GetPosition(_layer);
        else
            pointerInLayer = _items.TranslatePoint(_downPos, _layer) ?? new Point(0, 0);

        // 悬浮层中心对准鼠标
        var x = pointerInLayer.X + _popupSize.Width / 2;
        var y = pointerInLayer.Y + _popupSize.Height / 2;

        _floatingPopup.HorizontalOffset = x;
        _floatingPopup.VerticalOffset = y;
    }

    private void UpdateFloatingPosition(PointerEventArgs e) {
        UpdateFloatingPopupPosition(e);
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

        var containers = GetContainersInOrder();
        if (containers.Count == 0) return;

        var targetY = ComputePlaceholderY(containers, _insertIndex);
        if (double.IsNaN(targetY)) {
            _placeholder.IsVisible = false;
            return;
        }
        _placeholder.IsVisible = true;

        var origin = _items.TranslatePoint(new Point(0, targetY), _layer);
        if (!origin.HasValue) return;

        Canvas.SetLeft(_placeholder, origin.Value.X);
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
        var containers = GetContainersInOrder();
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

        var containers = GetContainersInOrder();
        if (containers.Count == 0) return;

        int lo = Math.Min(_dragIndex, _insertIndex);
        int hi = Math.Max(_dragIndex, _insertIndex);

        // 无位移（回到原槽）→ 全部在途让位项归零，避免残留偏移导致重叠
        if (lo == hi) {
            foreach (var s in _slides.ToList()) {
                s.From = s.Current;
                s.To = 0;
                s.DurationMs = AnimMs;
                s.Elapsed = 0;
            }
            if (_slides.Count > 0 && !_animTimer.IsEnabled)
                _animTimer.Start();
            return;
        }

        var slotSize = _cardContentHeight + _cardBottomGap;
        bool movingDown = _insertIndex > _dragIndex;

        // 先把不在范围内的 slide 归位
        var inRange = new HashSet<Control>();
        foreach (var c in containers) {
            if (c == _dragContainer) continue;
            var idx = containers.IndexOf(c);
            if (idx >= lo && idx <= hi)
                inRange.Add(c);
        }

        // 把不在范围里的 slide 归位
        foreach (var s in _slides.ToList()) {
            if (!inRange.Contains(s.Control)) {
                s.From = s.Current;
                s.To = 0;
                s.DurationMs = AnimMs;
                s.Elapsed = 0;
            }
        }

        foreach (var c in containers) {
            if (c == _dragContainer) continue;
            var idx = containers.IndexOf(c);
            if (idx < lo || idx > hi) continue;

            double target = movingDown ? -slotSize : slotSize;

            var existing = _slides.FirstOrDefault(s => ReferenceEquals(s.Control, c));
            if (existing is { Control: not null }) {
                existing.From = existing.Current;
                existing.To = target;
                existing.DurationMs = AnimMs;
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
                    DurationMs = AnimMs,
                    Elapsed = 0,
                });
            }
        }

        if (!_animTimer.IsEnabled)
            _animTimer.Start();
    }

    private void OnAnimTick() {
        if (_slides.Count == 0) {
            _animTimer.Stop();
            return;
        }

        var dt = 16.0;
        var completed = new List<SlideState>();

        foreach (var s in _slides) {
            s.Elapsed += dt;
            var t = s.Elapsed >= s.DurationMs ? 1.0 : s.Elapsed / s.DurationMs;
            // CubicEaseInOut
            var eased = t < 0.5
                ? 4.0 * t * t * t
                : 1.0 - Math.Pow(-2.0 * t + 2.0, 3.0) / 2.0;

            s.Current = s.From + (s.To - s.From) * eased;
            s.Transform.Y = s.Current;

            if (t >= 1.0)
                completed.Add(s);
        }

        foreach (var s in completed) {
            s.Transform.Y = s.To;
            if (Math.Abs(s.To) < 0.001) {
                s.Control.RenderTransform = null;
                _slides.Remove(s);
            }
            else {
                s.From = s.To;
                s.Elapsed = s.DurationMs;
            }
        }

        if (_slides.Count == 0)
            _animTimer.Stop();
    }

    // ═══════════════════════════════════════════════
    //  边缘滚动
    // ═══════════════════════════════════════════════
    private void UpdateEdgeScroll(Point pointerInItems) {
        var svOrigin = _scroller.TranslatePoint(new Point(0, 0), _items);
        if (!svOrigin.HasValue) return;
        var pointerInSv = pointerInItems - svOrigin.Value;

        var viewportHeight = _scroller.Bounds.Height;
        double distance;

        if (pointerInSv.Y < EdgeThreshold)
            distance = EdgeThreshold - pointerInSv.Y;
        else if (pointerInSv.Y > viewportHeight - EdgeThreshold)
            distance = pointerInSv.Y - (viewportHeight - EdgeThreshold);
        else
            distance = 0;

        if (distance > 0 && !_scrollTimer.IsEnabled) {
            _scrollTimer.Tag = new ScrollContext {
                PointerInItems = pointerInItems,
                PointerInViewport = pointerInSv,
                Direction = pointerInSv.Y < EdgeThreshold ? -1 : 1,
                Strength = distance / EdgeThreshold,
            };
            _scrollTimer.Start();
        }
        else if (distance > 0 && _scrollTimer.IsEnabled) {
            if (_scrollTimer.Tag is ScrollContext ctx) {
                ctx.PointerInItems = pointerInItems;
                ctx.PointerInViewport = pointerInSv;
                ctx.Direction = pointerInSv.Y < EdgeThreshold ? -1 : 1;
                ctx.Strength = distance / EdgeThreshold;
            }
        }
        else if (distance == 0 && _scrollTimer.IsEnabled) {
            _scrollTimer.Stop();
        }
    }

    private void OnScrollTick() {
        if (_scrollTimer.Tag is not ScrollContext ctx) return;

        var delta = ctx.Direction * ctx.Strength * MaxScrollSpeed;
        var offset = _scroller.Offset;
        var maxOffsetY = Math.Max(0, _scroller.Extent.Height - _scroller.Viewport.Height);
        var newY = Math.Clamp(offset.Y + delta, 0, maxOffsetY);
        _scroller.Offset = new Vector(offset.X, newY);

        // 重算 PointerInItems：内容已滚动，视口坐标不变，但 _items 空间坐标已变
        var svOrigin = _scroller.TranslatePoint(new Point(0, 0), _items);
        if (svOrigin.HasValue)
            ctx.PointerInItems = ctx.PointerInViewport + svOrigin.Value;

        var newIndex = ComputeInsertIndex(ctx.PointerInItems);
        if (newIndex != _insertIndex) {
            _insertIndex = newIndex;
            UpdatePlaceholderPosition();
            BeginNeighborSlides();
        }
    }

    private sealed class ScrollContext {
        public required Point PointerInItems;
        public required Point PointerInViewport;
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
            _floatingPopup = null;
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

    private List<Control> GetContainersInOrder() {
        var result = new List<Control>();
        foreach (var v in _items.GetVisualDescendants()) {
            if (v is Control c && IsMemoCardRoot(c))
                result.Add(c);
        }
        return result.OrderBy(GetContainerLayoutY).ToList();
    }

    private double GetContainerY(Control c) {
        var pt = c.TranslatePoint(new Point(0, 0), _items);
        return pt.HasValue ? pt.Value.Y : c.Bounds.Y;
    }

    /// <summary>
    /// 容器的布局 Y（排除让位动画产生的 RenderTransform 偏移）。
    /// </summary>
    private double GetContainerLayoutY(Control c) {
        var visualY = GetContainerY(c);
        var slideOff = (c.RenderTransform as TranslateTransform)?.Y ?? 0;
        return visualY - slideOff;
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

        var containers = GetContainersInOrder();
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
