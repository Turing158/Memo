using Avalonia;
using Avalonia.Animation;
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
        SetupPinRotationTransition();
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

    // —— 构建教程内容（只读），快捷键读取用户当前设置 ——
    private void BuildContent(AppSettings s) {
        var content = this.FindControl<TextBlock>("_tutorialContent");
        if (content == null) return;

        var lines = new System.Collections.Generic.List<string> {
            "一、创建备忘录",
            "在软件顶部输入框中输入内容，按 Enter 即可添加一条新备忘录（Shift+Enter 换行）。",
            "双击列表中的备忘录可在顶部输入框中重新编辑。",
            "",
            "二、编辑与分离",
            "双击卡片进入编辑（Esc 保存）；长按拖拽卡片可拉出独立窗口，拖回主窗体则合并。",
            "",
            "三、置顶与关闭",
            "点便签右上角图钉可置顶，「×」关闭；主窗口关闭按钮可在设置中选择最小化到托盘或退出。",
            "",
            "四、快捷键",
            $"置顶主窗口：{s.ToggleTopmostHotkey}",
            $"最小化到托盘：{s.MinimizeHotkey}",
            $"显示主窗口：{s.ShowWindowHotkey}",
        };

        if (s.QuickMemoEnabled) {
            lines.Add($"快速添加（剪贴板）：{s.QuickMemoHotkey}");
        }

        lines.Add("");
        lines.Add("五、托盘图标");
        lines.Add("点击托盘图标显示主窗口（单击/双击可在设置中切换）。");

        content.Text = string.Join("\n", lines);
    }
}
