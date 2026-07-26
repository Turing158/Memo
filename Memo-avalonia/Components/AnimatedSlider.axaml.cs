using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Memo.UI;
using System;
using System.Linq;

namespace Memo.Components;

public sealed class SliderValueChangedEventArgs : EventArgs {
    public SliderValueChangedEventArgs(int oldValue, int newValue) {
        OldValue = oldValue;
        NewValue = newValue;
    }

    public int OldValue { get; }
    public int NewValue { get; }
}

/// <summary>
/// An integer slider with endpoint labels, animated thumb feedback and a drag tooltip.
/// </summary>
public partial class AnimatedSlider : UserControl {
    private const double IdleThumbScale = 1;
    private const double HoverThumbScale = 1.1;
    private const double PressedThumbScale = 0.96;

    private int _minimum;
    private int _maximum = 100;
    private int _value;
    private int _step = 1;
    private string _valueSuffix = string.Empty;
    private int _lastCommittedValue;
    private bool _isInitialized;
    private bool _isSyncingSlider;
    private bool _isPointerInteraction;
    private bool _isKeyboardInteraction;
    private bool _thumbPointerOver;
    private Thumb? _thumb;

    public AnimatedSlider() {
        InitializeComponent();
        _isInitialized = true;
        _tooltipPopup.PlacementTarget = _slider;

        _slider.AddHandler(InputElement.PointerPressedEvent, OnSliderPointerPressed,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        _slider.AddHandler(InputElement.PointerReleasedEvent, OnSliderPointerReleased,
            RoutingStrategies.Bubble, handledEventsToo: true);
        _slider.AddHandler(InputElement.PointerCaptureLostEvent, OnSliderPointerCaptureLost,
            RoutingStrategies.Bubble, handledEventsToo: true);
        _slider.AddHandler(InputElement.KeyDownEvent, OnSliderKeyDown,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        _slider.AddHandler(InputElement.KeyUpEvent, OnSliderKeyUp,
            RoutingStrategies.Bubble, handledEventsToo: true);
        _slider.PropertyChanged += OnSliderPropertyChanged;
        _slider.GotFocus += OnSliderGotFocus;
        _slider.LostFocus += OnSliderLostFocus;
        _slider.SizeChanged += (_, _) => {
            UpdateBarProgress();
            UpdateTooltipOffset();
        };
        _barTrack.SizeChanged += (_, _) => UpdateBarProgress();
        Loaded += (_, _) => Dispatcher.UIThread.Post(ResolveThumb, DispatcherPriority.Render);

        SyncSliderConfiguration();
        UpdateText();
        UpdateBarProgress();
        _lastCommittedValue = _value;
    }

    public static readonly DirectProperty<AnimatedSlider, int> MinimumProperty =
        AvaloniaProperty.RegisterDirect<AnimatedSlider, int>(
            nameof(Minimum), slider => slider.Minimum, (slider, value) => slider.Minimum = value);

    public int Minimum {
        get => _minimum;
        set {
            if (_minimum == value) return;
            SetAndRaise(MinimumProperty, ref _minimum, value);
            if (_maximum < _minimum) Maximum = _minimum;
            OnRangeChanged();
        }
    }

    public static readonly DirectProperty<AnimatedSlider, int> MaximumProperty =
        AvaloniaProperty.RegisterDirect<AnimatedSlider, int>(
            nameof(Maximum), slider => slider.Maximum, (slider, value) => slider.Maximum = value);

    public int Maximum {
        get => _maximum;
        set {
            var normalized = Math.Max(_minimum, value);
            if (_maximum == normalized) return;
            SetAndRaise(MaximumProperty, ref _maximum, normalized);
            OnRangeChanged();
        }
    }

    public static readonly DirectProperty<AnimatedSlider, int> ValueProperty =
        AvaloniaProperty.RegisterDirect<AnimatedSlider, int>(
            nameof(Value), slider => slider.Value, (slider, value) => slider.Value = value,
            defaultBindingMode: BindingMode.TwoWay);

    public int Value {
        get => _value;
        set => SetValueCore(value, raiseEvent: true);
    }

    public static readonly DirectProperty<AnimatedSlider, int> StepProperty =
        AvaloniaProperty.RegisterDirect<AnimatedSlider, int>(
            nameof(Step), slider => slider.Step, (slider, value) => slider.Step = value);

    public int Step {
        get => _step;
        set {
            var normalized = Math.Max(1, value);
            if (_step == normalized) return;
            SetAndRaise(StepProperty, ref _step, normalized);
            OnRangeChanged();
        }
    }

    public static readonly DirectProperty<AnimatedSlider, string> ValueSuffixProperty =
        AvaloniaProperty.RegisterDirect<AnimatedSlider, string>(
            nameof(ValueSuffix), slider => slider.ValueSuffix, (slider, value) => slider.ValueSuffix = value);

    public string ValueSuffix {
        get => _valueSuffix;
        set {
            value ??= string.Empty;
            if (_valueSuffix == value) return;
            SetAndRaise(ValueSuffixProperty, ref _valueSuffix, value);
            UpdateText();
        }
    }

    public event EventHandler<SliderValueChangedEventArgs>? ValueChanged;
    public event EventHandler<SliderValueChangedEventArgs>? ValueCommitted;

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) {
        _isPointerInteraction = false;
        _isKeyboardInteraction = false;
        CommitValue();
        MotionAnimations.Cancel(_tooltipBubble);
        MotionAnimations.Cancel(this);
        _tooltipPopup.IsOpen = false;
        UnsubscribeThumb();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnRangeChanged() {
        if (!_isInitialized) return;
        SyncSliderConfiguration();
        SetValueCore(_value, raiseEvent: true);
        UpdateText();
        UpdateBarProgress();
    }

    private void SyncSliderConfiguration() {
        if (!_isInitialized) return;
        _isSyncingSlider = true;
        try {
            _slider.Minimum = _minimum;
            _slider.Maximum = _maximum;
            _slider.TickFrequency = _step;
            _slider.SmallChange = _step;
            _slider.LargeChange = Math.Max(_step, _step * 5);
            _slider.Value = NormalizeValue(_value);
        }
        finally {
            _isSyncingSlider = false;
        }
    }

    private int NormalizeValue(int value) {
        var clamped = Math.Clamp(value, _minimum, _maximum);
        if (_step <= 1) return clamped;
        var snapped = _minimum + (int)Math.Round((clamped - _minimum) / (double)_step) * _step;
        return Math.Clamp(snapped, _minimum, _maximum);
    }

    private void SetValueCore(int value, bool raiseEvent) {
        var normalized = NormalizeValue(value);
        if (_value == normalized) {
            if (_isInitialized && !_isSyncingSlider && Math.Abs(_slider.Value - normalized) > 0.001)
                _slider.Value = normalized;
            UpdateText();
            UpdateBarProgress();
            UpdateTooltipOffset();
            return;
        }

        var oldValue = _value;
        SetAndRaise(ValueProperty, ref _value, normalized);
        if (_isInitialized && !_isSyncingSlider) {
            _isSyncingSlider = true;
            try {
                _slider.Value = normalized;
            }
            finally {
                _isSyncingSlider = false;
            }
        }

        if (!IsUserInteractionActive) _lastCommittedValue = normalized;
        UpdateText();
        UpdateBarProgress();
        UpdateTooltipOffset();
        if (raiseEvent) ValueChanged?.Invoke(this, new SliderValueChangedEventArgs(oldValue, normalized));
    }

    private void OnSliderPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e) {
        if (_isSyncingSlider || e.Property != RangeBase.ValueProperty) return;
        SetValueCore((int)Math.Round(_slider.Value), raiseEvent: true);
    }

    private bool IsUserInteractionActive => _isPointerInteraction || _isKeyboardInteraction;

    private void OnSliderPointerPressed(object? sender, PointerPressedEventArgs e) {
        if (!e.GetCurrentPoint(_slider).Properties.IsLeftButtonPressed) return;
        _isPointerInteraction = true;
        _focusCue.Classes.Set("focused", false);
        ShowTooltip();
        AnimateThumbScale(PressedThumbScale);
    }

    private void OnSliderPointerReleased(object? sender, PointerReleasedEventArgs e) {
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        EndPointerInteraction();
    }

    private void OnSliderPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) =>
        EndPointerInteraction();

    private void EndPointerInteraction() {
        if (!_isPointerInteraction) return;
        _isPointerInteraction = false;
        AnimateThumbScale(_thumbPointerOver ? HoverThumbScale : IdleThumbScale);
        HideTooltip();
        CommitValue();
    }

    private void OnSliderKeyDown(object? sender, KeyEventArgs e) {
        if (!IsSliderNavigationKey(e.Key)) return;
        _isKeyboardInteraction = true;
        ShowTooltip();
        AnimateThumbScale(PressedThumbScale);
    }

    private void OnSliderKeyUp(object? sender, KeyEventArgs e) {
        if (!IsSliderNavigationKey(e.Key) || !_isKeyboardInteraction) return;
        _isKeyboardInteraction = false;
        AnimateThumbScale(_thumbPointerOver ? HoverThumbScale : IdleThumbScale);
        HideTooltip();
        CommitValue();
    }

    private static bool IsSliderNavigationKey(Key key) => key is
        Key.Left or Key.Right or Key.Up or Key.Down or
        Key.PageUp or Key.PageDown or Key.Home or Key.End;

    private void CommitValue() {
        if (_lastCommittedValue == _value) return;
        var oldValue = _lastCommittedValue;
        _lastCommittedValue = _value;
        ValueCommitted?.Invoke(this, new SliderValueChangedEventArgs(oldValue, _value));
    }

    private void OnSliderGotFocus(object? sender, GotFocusEventArgs e) =>
        _focusCue.Classes.Set("focused", e.NavigationMethod != NavigationMethod.Pointer);

    private void OnSliderLostFocus(object? sender, RoutedEventArgs e) {
        _focusCue.Classes.Set("focused", false);
        if (_isKeyboardInteraction) {
            _isKeyboardInteraction = false;
            HideTooltip();
            CommitValue();
        }
    }

    private void ResolveThumb() {
        var thumb = _slider.GetVisualDescendants().OfType<Thumb>().FirstOrDefault();
        if (ReferenceEquals(_thumb, thumb)) return;
        UnsubscribeThumb();
        _thumb = thumb;
        if (_thumb == null) return;

        _thumb.RenderTransform = new ScaleTransform(IdleThumbScale, IdleThumbScale);
        _thumb.PointerEntered += OnThumbPointerEntered;
        _thumb.PointerExited += OnThumbPointerExited;
        UpdateTooltipOffset();
    }

    private void UnsubscribeThumb() {
        if (_thumb == null) return;
        _thumb.PointerEntered -= OnThumbPointerEntered;
        _thumb.PointerExited -= OnThumbPointerExited;
        _thumb = null;
    }

    private void OnThumbPointerEntered(object? sender, PointerEventArgs e) {
        _thumbPointerOver = true;
        if (!_isPointerInteraction) AnimateThumbScale(HoverThumbScale);
    }

    private void OnThumbPointerExited(object? sender, PointerEventArgs e) {
        _thumbPointerOver = false;
        if (!_isPointerInteraction) AnimateThumbScale(IdleThumbScale);
    }

    private void AnimateThumbScale(double targetScale) {
        if (_thumb?.RenderTransform is not ScaleTransform transform) return;
        var fromScale = transform.ScaleX;
        if (Math.Abs(fromScale - targetScale) < 0.001) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) {
            transform.ScaleX = targetScale;
            transform.ScaleY = targetScale;
            return;
        }

        MotionAnimations.Start(
            this,
            topLevel,
            MotionPreferences.FastDuration,
            new CubicEaseOut(),
            progress => {
                var scale = MotionAnimations.Lerp(fromScale, targetScale, progress);
                transform.ScaleX = scale;
                transform.ScaleY = scale;
            });
    }

    private void ShowTooltip() {
        UpdateText();
        UpdateTooltipOffset();
        _tooltipBubble.IsVisible = true;
        _tooltipPopup.IsOpen = true;
        var transform = EnsureTooltipTransform();
        var fromOpacity = _tooltipBubble.Opacity;
        var fromY = transform.Y;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) {
            _tooltipBubble.Opacity = 1;
            transform.Y = 0;
            return;
        }

        MotionAnimations.Start(
            _tooltipBubble,
            topLevel,
            MotionPreferences.FastDuration,
            new CubicEaseOut(),
            progress => {
                _tooltipBubble.Opacity = MotionAnimations.Lerp(fromOpacity, 1, progress);
                transform.Y = MotionAnimations.Lerp(fromY, 0, progress);
            });
    }

    private void HideTooltip() {
        if (!_tooltipBubble.IsVisible) return;
        var transform = EnsureTooltipTransform();
        var fromOpacity = _tooltipBubble.Opacity;
        var fromY = transform.Y;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) {
            _tooltipBubble.Opacity = 0;
            transform.Y = 3;
            _tooltipBubble.IsVisible = false;
            _tooltipPopup.IsOpen = false;
            return;
        }

        MotionAnimations.Start(
            _tooltipBubble,
            topLevel,
            MotionPreferences.FastDuration,
            new CubicEaseOut(),
            progress => {
                _tooltipBubble.Opacity = MotionAnimations.Lerp(fromOpacity, 0, progress);
                transform.Y = MotionAnimations.Lerp(fromY, 3, progress);
            },
            () => {
                _tooltipBubble.IsVisible = false;
                _tooltipPopup.IsOpen = false;
            });
    }

    private TranslateTransform EnsureTooltipTransform() {
        if (_tooltipBubble.RenderTransform is TranslateTransform transform) return transform;
        transform = new TranslateTransform { Y = 3 };
        _tooltipBubble.RenderTransform = transform;
        return transform;
    }

    private void UpdateBarProgress() {
        if (!_isInitialized || _barTrack.Bounds.Width <= 0) return;
        var range = Math.Max(1, _maximum - _minimum);
        var ratio = Math.Clamp((_value - _minimum) / (double)range, 0, 1);
        _barProgress.Width = _barTrack.Bounds.Width * ratio;
    }

    private void UpdateTooltipOffset() {
        if (!_isInitialized || _slider.Bounds.Width <= 0) return;
        var range = Math.Max(1, _maximum - _minimum);
        var ratio = Math.Clamp((_value - _minimum) / (double)range, 0, 1);
        var thumbWidth = _thumb?.Bounds.Width ?? 44;
        if (thumbWidth <= 0) thumbWidth = 44;
        var travelWidth = Math.Max(0, _slider.Bounds.Width - thumbWidth);
        _tooltipPopup.HorizontalOffset = (ratio - 0.5) * travelWidth;
    }


    private void UpdateText() {
        if (!_isInitialized) return;
        _minimumText.Text = FormatValue(_minimum);
        _maximumText.Text = FormatValue(_maximum);
        _tooltipText.Text = FormatValue(_value);
        _slider.SetValue(AutomationProperties.HelpTextProperty,
            $"当前值 {FormatValue(_value)}，范围 {FormatValue(_minimum)} 到 {FormatValue(_maximum)}");
    }

    private string FormatValue(int value) => $"{value}{_valueSuffix}";
}
