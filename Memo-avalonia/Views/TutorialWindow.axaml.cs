using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Memo.Models;
using Memo.UI;
using Memo.Utils;
using System;
using System.Collections.Generic;

namespace Memo.Views;

public partial class TutorialWindow : Window {
    private readonly WindowTransitionController _transition;
    private bool _isClosingAfterTransition;
    private bool _isPinned;

    public TutorialWindow() {
        InitializeComponent();
        Loaded += (_, _) => this.AssignResizeCursors();
        _transition = new WindowTransitionController(this, this.FindControl<Border>("_popoutShell")!);
        _transition.PrepareOpen();
        Opened += (_, _) => _transition.PlayOpen();
        Closed += OnWindowClosed;
    }

    public TutorialWindow(AppSettings settings)
        : this() {
        BuildContent(settings);
    }

    public bool IsPinned => _isPinned;

    public void TogglePinned() => SetPinned(!_isPinned);

    public void SetPinned(bool isPinned) {
        _isPinned = isPinned;
        Topmost = _isPinned;
        UpdatePinButtonVisual();
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e) {
        if (TitleBarDragHelper.CanStartDrag(this, e)) {
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
        CloseWithTransition();
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

    private void OnWindowClosed(object? sender, EventArgs e) {
        _transition.Cancel();
        if (this.FindControl<Button>("_pinButton")?.Content is PathIcon pinIcon)
            MotionAnimations.Cancel(pinIcon);
    }

    // —— 构建教程内容（只读），快捷键读取用户当前设置 ——
    private void BuildContent(AppSettings s) {
        var content = this.FindControl<TextBlock>("_tutorialContent");
        if (content == null) return;

        var lines = new System.Collections.Generic.List<string> {
            "一、Markdown 备忘录",
            "顶部是可直接排版和编辑的单框 Markdown 编辑器，支持标题、强调、删除线、有序/无序列表、任务项、引用、链接、代码、表格、分隔线与图片。",
            "新建时按 Ctrl+Enter 新增，Enter 换行；工具栏可直接插入常用格式，「更多」菜单也可打开 Markdown 源码编辑器。",
            "",
            "二、所见即所得编辑",
            "单击卡片即可在顶部载入并直接编辑；便签正文始终可编辑，停止输入 500ms 后自动保存。",
            "编辑已有内容时按 Ctrl+Enter 立即保存且不切换界面，Esc 恢复本次载入时的内容。",
            "编辑时可用 Ctrl+B / Ctrl+I / Ctrl+K 快速插入粗体、斜体、链接。",
            "",
            "三、插入图片",
            "可粘贴、拖入或选择本地图片，也可从更多菜单插入 HTTPS 网络图片。",
            "",
            "四、分离便签",
            "长按拖拽卡片可拉出独立窗口；关闭「重复便签」时，拖出已弹出的备忘录会移动其现有便签位置，开启后总是新建。",
            "便签右上角可切换「显示 Markdown 工具栏」，此设置与正文编辑状态无关。",
            "点击便签时间戳可在「相对时间」和「完整时间」之间切换。",
            "",
            "五、窗口调整",
            "主窗口、便签、设置与教程窗口均无边框，且支持从边缘和四角拖拽缩放；可拖动标题栏移动位置。",
            "",
            "六、贴边停靠",
            "拖动主窗口靠近屏幕任意边缘（约 40 像素内）时，窗口收起为吸附在边缘的小圆角方块；拖动方块可沿边缘滑动。",
            "把方块向屏幕内侧拖离边缘一定距离后，窗口在光标处展开还原；右键方块可打开托盘菜单。",
            "可在设置中调整贴边方块大小（30–75 像素，默认 44），调整时实时预览；也可关闭「启用贴边」。",
            "",
            "七、置顶与关闭",
            "点右上角图钉可置顶主窗口，便签右上角图钉可置顶便签；主窗口关闭按钮可在设置中选择最小化到托盘或直接退出。",
            "",
            "八、快捷键",
            $"置顶主窗口：{s.ToggleTopmostHotkey}",
            $"最小化到托盘：{s.MinimizeHotkey}",
            $"显示主窗口：{s.ShowWindowHotkey}",
        };

        if (s.QuickMemoEnabled) {
            lines.Add($"快速添加（剪贴板）：{s.QuickMemoHotkey}");
        }

        lines.Add("");
        lines.Add("九、托盘图标");
        lines.Add("左键双击（或按设置改为单击）托盘图标恢复/显示主窗口；右键打开托盘菜单。");
        lines.Add("托盘菜单提供：打开备忘录、新建备忘录、窗口置顶、退出应用。");
        lines.Add("");
        lines.Add("十、其它设置");
        lines.Add("「快速添加剪贴板内容」可启用/禁用快捷键快速添加；开启后可选「快速添加后自动显示便签」在鼠标位置弹出便签。");
        lines.Add("「界面动效」可跟随系统、始终开启或关闭，控制窗口与控件的过渡动画。");
        lines.Add("「教程」可随时从这里重新打开；「重置设置」可一键恢复默认（带二次确认）。");

        content.Text = string.Join("\n", lines);
    }
}
