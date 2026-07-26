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
            "顶部编辑器支持标题、强调、列表、任务项、引用、链接、代码、表格与图片。",
            "新建时按 Enter 保存，Shift+Enter 换行；工具栏可直接插入常用格式。",
            "",
            "二、编辑与预览",
            "双击卡片或便签内容进入编辑；停止输入 500ms 后自动保存。",
            "Ctrl+Enter 保存并预览，Esc 恢复进入编辑时的内容。",
            "",
            "三、插入图片",
            "可粘贴、拖入或选择本地图片，也可从更多菜单插入 HTTPS 图片地址。",
            "",
            "四、分离便签",
            "长按拖拽卡片可拉出独立窗口，拖回主窗体则合并。",
            "",
            "五、置顶与关闭",
            "点便签右上角图钉可置顶，「×」关闭；主窗口关闭按钮可在设置中选择最小化到托盘或退出。",
            "",
            "六、快捷键",
            $"置顶主窗口：{s.ToggleTopmostHotkey}",
            $"最小化到托盘：{s.MinimizeHotkey}",
            $"显示主窗口：{s.ShowWindowHotkey}",
        };

        if (s.QuickMemoEnabled) {
            lines.Add($"快速添加（剪贴板）：{s.QuickMemoHotkey}");
        }

        lines.Add("");
        lines.Add("七、托盘图标");
        lines.Add("点击托盘图标显示主窗口（单击/双击可在设置中切换）。");

        content.Text = string.Join("\n", lines);
    }
}
