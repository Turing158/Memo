using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Memo.Behaviors;
using Memo.Models;
using Memo.Utils;
using Memo.ViewModels;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace Memo.Views;

/// <summary>
///  MainWindow 使用的 IValueConverter 集合。
/// </summary>
public static class MainWindowConverters {
    /// <summary>整数 == 0 → true（用于空状态可见性）。</summary>
    public static readonly IValueConverter IsZero = new FuncConverter<int>(v => v == 0);

    /// <summary>整数 > 0 → true（用于列表可见性）。</summary>
    public static readonly IValueConverter IsGreaterThanZero = new FuncConverter<int>(v => v > 0);

    private sealed class FuncConverter<T> : IValueConverter {
        private readonly Func<T, bool> _fn;
        public FuncConverter(Func<T, bool> fn) => _fn = fn;
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => _fn((T)value!);
        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}

public partial class MainWindow : Window {
    private MainViewModel ViewModel => (MainViewModel)DataContext!;
    internal MainViewModel MemoViewModel => ViewModel;

    private AppSettings _settings = AppSettings.CreateDefault();
    private Action? _openSettings;
    private Action? _exitApplication;
    private Func<Task<CloseButtonAction?>>? _askCloseButtonAction;
    public event Action<MemoItem, PixelPoint>? MemoPopoutRequested;

    // 删除滑动动画的状态
    private DispatcherTimer? _deleteSlideTimer;
    private bool _isAnimatingDelete;

    // 窗口显示/隐藏过渡动画状态
    private DispatcherTimer? _windowTransitionTimer;
    private bool _isWindowTransitioning;

    // 长按拖拽重排管理器
    private DragReorderManager? _dragManager;

    public MainWindow() {
        InitializeComponent();
        PrepareWindowOpenState();
        // 等可视化树就绪后再给手柄赋值光标（Load 时 visual tree 才真正存在）
        Loaded += (_, _) => AssignResizeHandleCursors();

        // 为置顶按钮的旋转图标添加角度过渡动画
        SetupPinRotationTransition();

        var inputBox = this.FindControl<TextBox>("_inputBox")!;
        var list = this.FindControl<ItemsControl>("_memoList")!;
        var scroller = this.FindControl<ScrollViewer>("_scrollViewer")!;
        var dragLayer = this.FindControl<Canvas>("_dragFloatingLayer")!;

        // 长按拖拽重排管理器
        _dragManager = new DragReorderManager(list, scroller, dragLayer, ViewModel,
            (memo, position) => MemoPopoutRequested?.Invoke(memo, position));
        _dragManager.Attach();

        // Enter 提交 / Shift+Enter 换行
        inputBox.KeyDown += InputBox_KeyDown;

        // 双击卡片进入编辑 — Avalonia 11 AddHandler 签名
        list.AddHandler(InputElement.DoubleTappedEvent, OnMemoDoubleTapped,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble);

        // 加载数据 + 初始高亮 + 聚焦
        this.Loaded += async (_, _) => {
            await ViewModel.LoadAsync();
            UpdateEditVisual();
            inputBox.Focus();
        };
    }

    public void ConfigureAppActions(
        AppSettings settings,
        Action openSettings,
        Action exitApplication,
        Func<Task<CloseButtonAction?>> askCloseButtonAction) {
        _settings = settings;
        _openSettings = openSettings;
        _exitApplication = exitApplication;
        _askCloseButtonAction = askCloseButtonAction;
    }

    public void ApplySettings(AppSettings settings) {
        _settings = settings;
    }

    public void FocusInputForNewMemo() {
        var inputBox = this.FindControl<TextBox>("_inputBox");
        if (inputBox == null) return;

        inputBox.Focus();
        inputBox.CaretIndex = inputBox.Text?.Length ?? 0;
    }

    public void ShowOnStartup() {
        ShowWithOpenTransition(force: true);
    }

    public void ShowWithTransition() {
        ShowWithOpenTransition(force: false);
    }

    private void ShowWithOpenTransition(bool force) {
        if (!force && IsVisible && WindowState == WindowState.Normal && Opacity >= 1) {
            ShowInTaskbar = false;
            Activate();
            return;
        }

        PrepareWindowOpenState();
        WindowState = WindowState.Normal;
        ShowInTaskbar = false;
        Show();
        Activate();
        Dispatcher.UIThread.Post(PlayOpenTransition, DispatcherPriority.Render);
    }

    public void HideToTrayWithTransition() {
        if (_isWindowTransitioning) return;

        PlayWindowTransition(
            duration: TimeSpan.FromMilliseconds(160),
            fromOpacity: Opacity,
            toOpacity: 0,
            fromScale: CurrentShellScale(),
            toScale: 0.985,
            easing: new CubicEaseIn(),
            completed: () =>
            {
                Hide();
                WindowState = WindowState.Normal;
                ShowInTaskbar = false;
                PrepareWindowOpenState();
            });
    }

    private void PrepareWindowOpenState() {
        Opacity = 0;
        SetShellScale(0.97);
    }

    private void PlayOpenTransition() {
        PlayWindowTransition(
            duration: TimeSpan.FromMilliseconds(190),
            fromOpacity: Opacity,
            toOpacity: 1,
            fromScale: CurrentShellScale(),
            toScale: 1,
            easing: new CubicEaseOut(),
            completed: null);
    }

    private void PlayWindowTransition(
        TimeSpan duration,
        double fromOpacity,
        double toOpacity,
        double fromScale,
        double toScale,
        IEasing easing,
        Action? completed)
    {
        _windowTransitionTimer?.Stop();
        _isWindowTransitioning = true;

        var sw = Stopwatch.StartNew();
        Opacity = fromOpacity;
        SetShellScale(fromScale);

        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => { });
        timer.Tick += (_, _) => {
            var t = sw.Elapsed >= duration ? 1.0 : sw.Elapsed.TotalMilliseconds / duration.TotalMilliseconds;
            var eased = easing.Ease(t);

            Opacity = fromOpacity + ((toOpacity - fromOpacity) * eased);
            SetShellScale(fromScale + ((toScale - fromScale) * eased));

            if (t >= 1.0) {
                timer.Stop();
                Opacity = toOpacity;
                SetShellScale(toScale);
                _windowTransitionTimer = null;
                _isWindowTransitioning = false;
                completed?.Invoke();
            }
        };

        _windowTransitionTimer = timer;
        timer.Start();
    }

    private double CurrentShellScale() {
        var shell = this.FindControl<Border>("_windowShell");
        return shell?.RenderTransform is ScaleTransform scale ? scale.ScaleX : 1;
    }

    private void SetShellScale(double value) {
        var shell = this.FindControl<Border>("_windowShell");
        if (shell?.RenderTransform is ScaleTransform scale) {
            scale.ScaleX = value;
            scale.ScaleY = value;
        }
    }

    // —— 键盘处理 ——
    private void InputBox_KeyDown(object? sender, KeyEventArgs e) {
        if (e.Key != Key.Enter) return;

        // AcceptsReturn=True 时，统一在 KeyDown 先设 Handled=true，
        // 阻止 TextBox 内部把 Enter 键插入换行；再根据 Shift 决定「手动插行」还是「提交」。
        e.Handled = true;
        var inputBox = (TextBox)sender!;

        if ((e.KeyModifiers & KeyModifiers.Shift) == KeyModifiers.Shift) {
            // Shift+Enter → 按需插入新行（无上限，由 MaxLines 控制屏幕显示高度）
            var pos = inputBox.CaretIndex;
            var text = inputBox.Text ?? "";
            inputBox.Text = text.Insert(pos, "\n");
            inputBox.CaretIndex = pos + 1;
        }
        else {
            // Enter → 提交新增或保存编辑
            SubmitText(inputBox);
        }
    }

    private void SubmitText(TextBox inputBox) {
        var raw = inputBox.Text ?? "";
        var content = raw.TrimEnd('\r', '\n', ' ', '\t');

        if (FirstLineEmpty(content)) return; // 首行为空 → 忽略

        var vm = ViewModel;

        if (vm.EditingId.HasValue) {
            // 编辑态：保存内容并移到首位
            var itemId = vm.EditingId.Value;
            vm.UpdateItem(itemId, content);
            vm.MoveToFront(itemId);
            vm.EndEdit();
        }
        else {
            // 非编辑态：新增
            vm.AddItem(content);
        }

        inputBox.Text = string.Empty;
        UpdateEditVisual();
    }

    private static bool FirstLineEmpty(string text) =>
        string.IsNullOrWhiteSpace(text.Split('\n')[0]);

    // —— 双击编辑 ——
    private void OnMemoDoubleTapped(object? sender, RoutedEventArgs e) {
        var item = FindMemoItemFromSource(e.Source);
        if (item == null) return;

        var inputBox = this.FindControl<TextBox>("_inputBox")!;
        ViewModel.BeginEdit(item.Id);
        inputBox.Text = item.Content;
        inputBox.Focus();
        inputBox.CaretIndex = inputBox.Text?.Length ?? 0;
        UpdateEditVisual();
    }

    /// <summary>
    /// 从 event source 向上查找到载有 MemoItem DataContext 的控件（视觉树）。
    /// </summary>
    private static MemoItem? FindMemoItemFromSource(object? source) {
        if (source is Visual vis) {
            // GetVisualDescendants/Ancestors 通过 Avalonia.VisualTree 扩展
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

    // —— 标题栏拖拽移动窗口 ——
    private void _titleBarDrag_PointerPressed(object? sender, PointerPressedEventArgs e) {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
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
            _             => WindowEdge.North // 未识别 Tag 时不会走到（防御性）
        };
        BeginResizeDrag(edge, e);
    }

    // 给边缘/四角手柄设置对应方向的光标。Cursor.Parse 只识别 StandardCursorType 枚举名，
    // 故在代码里按 Tag 映射到确切枚举值，避免写错字符串导致启动崩溃。
    private void AssignResizeHandleCursors() => this.AssignResizeCursors();

    // —— 删除 ——
    private void OnDeleteClick(object? sender, RoutedEventArgs e) {
        var item = FindMemoItemFromSource(sender);
        if (item == null) return;

        // 如果上一次删除动画还没结束，本次直接走无动画路径，避免计时器冲突
        if (_isAnimatingDelete) {
            DeleteItemImmediate(item);
            return;
        }

        // 尝试走滑动动画路径；若测量失败则回退到无动画删除
        if (!TryAnimateDelete(item)) {
            DeleteItemImmediate(item);
        }
    }

    /// <summary>
    /// 无动画的直接删除（原逻辑）。
    /// </summary>
    private void DeleteItemImmediate(MemoItem item) {
        var vm = ViewModel;
        bool wasEditing = vm.EditingId == item.Id;
        vm.DeleteItem(item.Id); // ViewModel 内部清空 EditingId
        if (wasEditing) { /* R7：编辑中删除不清空输入框 */ }
        UpdateEditVisual();
    }

    /// <summary>
    /// 判断给定的 Control 是否是 MemoCard 的直接最外层容器。
    /// 条件：DataContext 匹配目标 item，且父元素不再是该 item 的 DataContext（即它已跨出 memo 内容区）。
    /// 例如：MemoCard Border 满足；MemoCard 内部的子 Grid / TextBlock 不满足（因为其父仍然是同一个 item）。
    /// </summary>
    private static bool IsMemoCardRoot(Control c, MemoItem item) {
        if (!ReferenceEquals(c.DataContext, item)) return false;
        // 父级的 DataContext 应该已经不再是这个 item（或者没有父级、父级无 DataContext）
        var parent = c.GetVisualParent();
        return parent == null || !ReferenceEquals((parent as IDataContextProvider)?.DataContext, item);
    }

    /// <summary>
    /// 在 ItemsControl 的视觉树中找到给定 MemoItem 对应的顶层容器（MemoCard Border）。
    /// 通过遍历视觉子项并匹配 DataContext 引用来实现，避免依赖 ItemContainerGenerator 的 API。
    /// </summary>
    private static Control? FindContainerForMemo(ItemsControl list, MemoItem item) {
        foreach (var v in list.GetVisualDescendants()) {
            // 找 DataContext 等于目标 item、但父级已经不是该 item 的那个节点
            // → 即 memo 卡片的最外层 Border（模版根节点）
            if (v is Control c && IsMemoCardRoot(c, item))
                return c;
        }
        return null;
    }

    /// <summary>
    /// 尝试驱动「删除项后下方卡片上移」的滑动动画。
    /// 返回 true 表示已进入动画流程；false 表示条件不满足、需回退到无动画删除。
    /// </summary>
    private bool TryAnimateDelete(MemoItem item) {
        var vm = ViewModel;
        var list = this.FindControl<ItemsControl>("_memoList");
        if (list == null) return false;

        var index = vm.Memos.IndexOf(item);
        if (index < 0) return false;
        if (index >= vm.Memos.Count - 1) return false; // 已是末项，无下方项需要滑动

        // 找到被删项和第一个下方项的容器
        var deletedContainer = FindContainerForMemo(list, item);
        var nextContainer = FindContainerForMemo(list, vm.Memos[index + 1]);
        if (deletedContainer == null || nextContainer == null) return false;

        // 测量两项之间的纵向距离（= 被删卡片高度 + margin）
        // 用 TranslatePoint 把容器原点转到 ItemsControl 的坐标系
        var ptDeleted = deletedContainer.TranslatePoint(new Point(0, 0), list);
        var ptNext = nextContainer.TranslatePoint(new Point(0, 0), list);
        if (ptDeleted == null || ptNext == null) return false;

        double slideDistance = ptNext.Value.Y - ptDeleted.Value.Y;
        if (slideDistance <= 0) return false;

        // 收集需要滑动的所有下方项
        var itemsBelow = new List<MemoItem>();
        for (int i = index + 1; i < vm.Memos.Count; i++)
            itemsBelow.Add(vm.Memos[i]);

        // 1. 先执行真正的删除 → 布局瞬间刷新，下方项跳到新位置
        bool wasEditing = vm.EditingId == item.Id;
        vm.DeleteItem(item.Id);
        UpdateEditVisual();
        if (wasEditing) { /* R7 */ }

        // 2. 等下一次布局完成后，再给下方项施加反向偏移并启动滑动动画
        //    用 DispatcherPriority.Render 可确保在布局提交之后、绘制之前执行
        Dispatcher.UIThread.Post(() => {
            StartSlideAnimation(list, itemsBelow, slideDistance);
        }, DispatcherPriority.Render);

        return true;
    }

    /// <summary>
    /// 给所有下方项施加 translateY = slideDistance（视觉上回到删除前的位置），
    /// 然后用 DispatcherTimer 逐帧把偏移插值到 0，实现上滑动画。
    /// </summary>
    private void StartSlideAnimation(ItemsControl list, List<MemoItem> itemsBelow, double slideDistance) {
        // 找到每个下方项当前的容器并施加初始偏移
        var entries = new List<(Control control, TranslateTransform transform)>();
        foreach (var memo in itemsBelow)  {
            var container = FindContainerForMemo(list, memo);
            if (container == null) continue;

            var tf = new TranslateTransform(0, slideDistance);
            container.RenderTransform = tf;
            entries.Add((container, tf));
        }

        if (entries.Count == 0) return;

        // 启动逐帧动画
        _deleteSlideTimer?.Stop();
        _isAnimatingDelete = true;

        var duration = TimeSpan.FromMilliseconds(200);
        var sw = Stopwatch.StartNew();
        var timer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(16),
            DispatcherPriority.Render,
            (_, _) => { });

        timer.Tick += (_, _) => {
            var elapsed = sw.Elapsed;
            double t = elapsed >= duration ? 1.0 : elapsed.TotalSeconds / duration.TotalSeconds;
            // CubicEaseOut: 1 - (1-t)^3
            double eased = 1.0 - Math.Pow(1.0 - t, 3.0);

            double offset = slideDistance * (1.0 - eased);
            foreach (var (_, tf) in entries) {
                tf.Y = offset;
            }

            if (t >= 1.0) {
                timer.Stop();
                _deleteSlideTimer = null;
                _isAnimatingDelete = false;

                // 动画结束，清除 RenderTransform 恢复干净状态
                foreach (var (control, _) in entries) {
                    control.RenderTransform = null;
                }
            }
        };

        _deleteSlideTimer = timer;
        timer.Start();
    }

    // —— 同步编辑态视觉（设置卡片 Classes.editing） ——
    private void UpdateEditVisual() {
        var vm = ViewModel;
        var list = this.FindControl<ItemsControl>("_memoList");
        if (list == null) return;

        var editingId = vm.EditingId;
        foreach (var child in list.GetVisualDescendants()) {
            if (child is Border { DataContext: MemoItem m } b) {
                b.Classes.Set("editing", editingId.HasValue && m.Id == editingId.Value);
            }
        }
    }

    // —— 标题栏按钮 ——
    private void OnSettingsClick(object? sender, RoutedEventArgs e) {
        _openSettings?.Invoke();
    }

    private bool _isPinned;
    public bool IsPinned => _isPinned;

    public void TogglePinned() => SetPinned(!_isPinned);

    public void SetPinned(bool isPinned) {
        _isPinned = isPinned;
        Topmost = _isPinned;
        UpdatePinButtonVisual();
    }

    private void OnPinToggle(object? sender, RoutedEventArgs e) => TogglePinned();

    private void UpdatePinButtonVisual() {
        var button = this.FindControl<Button>("_pinButton");
        if (button == null) return;

        button.Classes.Set("PinActive", _isPinned);

        if (button.Content is PathIcon pi && pi.RenderTransform is RotateTransform rt) {
            rt.Angle = _isPinned ? -45 : 0;
        }
    }

    private void SetupPinRotationTransition() {
        // 延迟到可视化树加载完成后再查找元素
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

    private void OnMinimizeClick(object? sender, RoutedEventArgs e) {
        // 最小化到系统托盘
        HideToTrayWithTransition();
    }

    private async void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        if (!_settings.HasAskedCloseButtonAction && _askCloseButtonAction != null) {
            var action = await _askCloseButtonAction();
            if (action == null) return;

            _settings.CloseButtonAction = action.Value;
            _settings.HasAskedCloseButtonAction = true;
        }

        if (_settings.CloseButtonAction == CloseButtonAction.Close) {
            _exitApplication?.Invoke();
            return;
        }

        // 关闭 → 隐藏到系统托盘而非退出
        HideToTrayWithTransition();
    }
}
