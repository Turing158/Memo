using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using System;

namespace Memo.Components;

/// <summary>
/// 带左右标签的滑块开关组件。
/// Value=false → 滑块在左（对应 LeftLabel）；Value=true → 滑块在右（对应 RightLabel）。
/// 支持点击切换、空格/回车切换、键盘聚焦态，并带滑块滑动动画。
/// </summary>
public partial class LabeledToggleSwitch : UserControl {
    private DispatcherTimer? _animTimer;
    private double _columnWidth;
    private bool _value;
    private string _leftLabelText = "";
    private string _rightLabelText = "";

    public event Action<bool>? ValueChanged;

    public LabeledToggleSwitch() {
        InitializeComponent();
        GotFocus += (_, _) => SetFocused(true);
        LostFocus += (_, _) => SetFocused(false);
    }

    public static readonly DirectProperty<LabeledToggleSwitch, string> LeftLabelProperty =
        AvaloniaProperty.RegisterDirect<LabeledToggleSwitch, string>(
            nameof(LeftLabel), o => o.LeftLabel, (o, v) => o.LeftLabel = v);

    public string LeftLabel {
        get => _leftLabelText;
        set {
            if (_leftLabelText == value) return;
            _leftLabelText = value;
            if (_leftLabel != null) _leftLabel.Text = value;
            UpdateThumbLabel();
        }
    }

    public static readonly DirectProperty<LabeledToggleSwitch, string> RightLabelProperty =
        AvaloniaProperty.RegisterDirect<LabeledToggleSwitch, string>(
            nameof(RightLabel), o => o.RightLabel, (o, v) => o.RightLabel = v);

    public string RightLabel {
        get => _rightLabelText;
        set {
            if (_rightLabelText == value) return;
            _rightLabelText = value;
            if (_rightLabel != null) _rightLabel.Text = value;
            UpdateThumbLabel();
        }
    }

    public bool Value {
        get => _value;
        set {
            if (_value == value) return;
            _value = value;
            UpdateVisuals(animate: true);
            ValueChanged?.Invoke(_value);
        }
    }

    private void SetFocused(bool focused) {
        if (_track == null) return;
        _track.Classes.Set("focused", focused);
    }

    private void UpdateThumbLabel() {
        if (_thumbLabel == null) return;
        _thumbLabel.Text = _value ? _rightLabelText : _leftLabelText;
    }

    private void OnTrackPointerPressed(object? sender, PointerPressedEventArgs e) {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) {
            Focus();
            Value = !Value;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e) {
        if (e.Key == Key.Space || e.Key == Key.Enter) {
            Value = !Value;
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e) {
        base.OnSizeChanged(e);
        RecolumnWidth();
        UpdateVisuals(animate: false);
    }

    private void RecolumnWidth() {
        if (_grid == null || _grid.ColumnDefinitions.Count < 2) return;
        _columnWidth = _grid.Bounds.Width / 2;
        if (_columnWidth <= 0) return;
        if (_thumb != null) _thumb.Width = _columnWidth;
    }

    private void UpdateVisuals(bool animate) {
        if (_thumb == null) return;

        if (_columnWidth <= 0) RecolumnWidth();
        UpdateThumbLabel();
        if (_columnWidth <= 0) return; // 尚未完成布局，等下一次 SizeChanged 再定位

        var target = _value ? _columnWidth : 0;

        if (_thumb.RenderTransform is not TranslateTransform) {
            _thumb.RenderTransform = new TranslateTransform(target, 0);
            return;
        }

        if (animate) {
            AnimateThumb(target);
        }
        else {
            _animTimer?.Stop();
            ((TranslateTransform)_thumb.RenderTransform).X = target;
        }
    }

    private void AnimateThumb(double target) {
        if (_thumb == null) return;
        var transform = (TranslateTransform)_thumb.RenderTransform!;
        var from = transform.X;
        if (Math.Abs(target - from) < 0.5) return;

        _animTimer?.Stop();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var duration = TimeSpan.FromMilliseconds(180);
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => { });
        timer.Tick += (_, _) => {
            var t = sw.Elapsed >= duration ? 1.0 : sw.Elapsed.TotalMilliseconds / duration.TotalMilliseconds;
            // cubic ease-out
            var eased = 1.0 - Math.Pow(1.0 - t, 3.0);
            transform.X = from + ((target - from) * eased);
            if (t >= 1.0) {
                timer.Stop();
                transform.X = target;
            }
        };
        _animTimer = timer;
        timer.Start();
    }
}
