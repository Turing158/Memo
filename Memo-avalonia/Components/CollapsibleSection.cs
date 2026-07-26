using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Layout;
using Memo.UI;
using System;

namespace Memo.Components;

/// <summary>
/// 可折叠容器：条件显隐的设置项在展开/收起时高度与透明度平滑过渡。
/// 内部用进度值驱动 MeasureOverride（0=收起，1=展开），每帧重新测量，
/// 因此无需预先量高、动画中途可反向、内容尺寸变化也能实时自适应；
/// 并用负下边距在动画中抵消父 StackPanel 的 Spacing，避免收起结束瞬间跳变。
/// </summary>
public sealed class CollapsibleSection : ContentControl {
    private static readonly TimeSpan MinimumReversalDuration = TimeSpan.FromMilliseconds(60);
    private bool _isExpanded = true;
    private double _spacingCompensation;
    private double _progress = 1; // 0=收起 1=展开
    private double _fullHeight;   // 上次测量得到的内容完整高度

    public CollapsibleSection() {
        ClipToBounds = true;
        VerticalContentAlignment = VerticalAlignment.Top;
    }

    // 复用 ContentControl 默认主题，否则纯代码子类没有模板不渲染内容
    protected override Type StyleKeyOverride => typeof(ContentControl);

    public static readonly DirectProperty<CollapsibleSection, bool> IsExpandedProperty =
        AvaloniaProperty.RegisterDirect<CollapsibleSection, bool>(
            nameof(IsExpanded), section => section.IsExpanded, (section, value) => section.IsExpanded = value);

    public bool IsExpanded {
        get => _isExpanded;
        set {
            if (_isExpanded == value) return;
            SetAndRaise(IsExpandedProperty, ref _isExpanded, value);
            ApplyExpansionState(animate: IsLoaded);
        }
    }

    public static readonly DirectProperty<CollapsibleSection, double> SpacingCompensationProperty =
        AvaloniaProperty.RegisterDirect<CollapsibleSection, double>(
            nameof(SpacingCompensation), section => section.SpacingCompensation, (section, value) => section.SpacingCompensation = value);

    /// <summary>父 StackPanel 的 Spacing；收起时用负下边距抵消，使贡献总高度恰好为 0。</summary>
    public double SpacingCompensation {
        get => _spacingCompensation;
        set {
            var coerced = Math.Max(0, value);
            if (Math.Abs(_spacingCompensation - coerced) < 0.001) return;
            SetAndRaise(SpacingCompensationProperty, ref _spacingCompensation, coerced);
            SetProgress(_progress);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) {
        base.OnAttachedToVisualTree(e);
        // XAML 里的 IsExpanded="False" 初始值需要在进入视觉树时立即落到收起终态
        SetProgress(_isExpanded ? 1 : 0);
        IsVisible = _isExpanded;
        IsHitTestVisible = _isExpanded;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) {
        MotionAnimations.Cancel(this);
        SetProgress(_isExpanded ? 1 : 0);
        IsVisible = _isExpanded;
        IsHitTestVisible = _isExpanded;
        base.OnDetachedFromVisualTree(e);
    }

    protected override Size MeasureOverride(Size availableSize) {
        // 以无限高测量内容，得到完整高度后按进度折算本容器占用的高度
        var desired = base.MeasureOverride(new Size(availableSize.Width, double.PositiveInfinity));
        _fullHeight = desired.Height;
        return new Size(desired.Width, Math.Max(0, _fullHeight * _progress));
    }

    protected override Size ArrangeOverride(Size finalSize) {
        // 内容始终按完整高度自顶排布，容器较小的 bounds 从底部裁切（向下展开的观感）
        base.ArrangeOverride(new Size(finalSize.Width, Math.Max(finalSize.Height, _fullHeight)));
        return finalSize;
    }

    private void ApplyExpansionState(bool animate) {
        var topLevel = TopLevel.GetTopLevel(this);
        if (!animate || topLevel == null) {
            MotionAnimations.Cancel(this);
            SetProgress(_isExpanded ? 1 : 0);
            IsVisible = _isExpanded;
            IsHitTestVisible = _isExpanded;
            return;
        }

        var from = _progress;
        var target = _isExpanded ? 1d : 0d;
        var distance = Math.Abs(target - from);
        if (distance < 0.001) {
            MotionAnimations.Cancel(this);
            SetProgress(target);
            IsVisible = _isExpanded;
            IsHitTestVisible = _isExpanded;
            return;
        }

        var duration = DurationForDistance(distance);
        if (_isExpanded) {
            IsVisible = true;
            IsHitTestVisible = true;
            MotionAnimations.Start(this, topLevel, duration, new CubicEaseOut(),
                progress => SetProgress(MotionAnimations.Lerp(from, 1, progress)));
        }
        else {
            // 收起过程中禁用命中测试，避免半收起的内容仍可点击
            IsHitTestVisible = false;
            MotionAnimations.Start(this, topLevel, duration, new CubicEaseIn(),
                progress => SetProgress(MotionAnimations.Lerp(from, 0, progress)),
                () => IsVisible = false);
        }
    }

    private void SetProgress(double progress) {
        _progress = Math.Clamp(progress, 0, 1);
        // Keep text crisp while most of the layout motion happens, then fade near the edge.
        Opacity = Math.Clamp(_progress * 1.5, 0, 1);
        // Compensate on the trailing edge so the content never slides into the item above it.
        Margin = new Thickness(0, 0, 0, -_spacingCompensation * (1 - _progress));
        InvalidateMeasure();
    }

    private static TimeSpan DurationForDistance(double distance) {
        var standard = MotionPreferences.StandardDuration;
        if (standard <= TimeSpan.Zero) return TimeSpan.Zero;

        var scaledTicks = (long)(standard.Ticks * Math.Clamp(distance, 0, 1));
        return TimeSpan.FromTicks(Math.Max(MinimumReversalDuration.Ticks, scaledTicks));
    }
}
