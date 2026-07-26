using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Memo.UI;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;

namespace Memo.Components;

public sealed record SegmentedSelectorOption(string Key, string Label);

public sealed class SegmentedSelectionChangedEventArgs : EventArgs {
    public SegmentedSelectionChangedEventArgs(string? oldKey, string newKey) {
        OldKey = oldKey;
        NewKey = newKey;
    }

    public string? OldKey { get; }
    public string NewKey { get; }
}

/// <summary>
/// A single-selection segmented control with a sliding indicator and one keyboard tab stop.
/// </summary>
public partial class SegmentedSelector : UserControl {
    private IReadOnlyList<SegmentedSelectorOption> _options = Array.Empty<SegmentedSelectorOption>();
    private INotifyCollectionChanged? _observableOptions;
    private readonly List<Border> _optionControls = new();
    private FrameAnimation? _indicatorAnimation;
    private string? _selectedKey;
    private double _segmentWidth;

    public SegmentedSelector() {
        InitializeComponent();
        _optionsGrid.SizeChanged += (_, _) => CalibrateIndicator();
        GotFocus += OnSelectorGotFocus;
        LostFocus += OnSelectorLostFocus;
    }

    public static readonly DirectProperty<SegmentedSelector, IReadOnlyList<SegmentedSelectorOption>> OptionsProperty =
        AvaloniaProperty.RegisterDirect<SegmentedSelector, IReadOnlyList<SegmentedSelectorOption>>(
            nameof(Options), selector => selector.Options, (selector, value) => selector.Options = value);

    public IReadOnlyList<SegmentedSelectorOption> Options {
        get => _options;
        set {
            value ??= Array.Empty<SegmentedSelectorOption>();
            if (ReferenceEquals(_options, value)) return;

            ValidateOptions(value);
            UnsubscribeFromOptions();
            SetAndRaise(OptionsProperty, ref _options, value);
            SubscribeToOptions(value);
            RebuildOptions();
        }
    }

    public static readonly DirectProperty<SegmentedSelector, string?> SelectedKeyProperty =
        AvaloniaProperty.RegisterDirect<SegmentedSelector, string?>(
            nameof(SelectedKey), selector => selector.SelectedKey, (selector, value) => selector.SelectedKey = value);

    public string? SelectedKey {
        get => _selectedKey;
        set => SetSelectedKey(value, raiseEvent: true, animate: true);
    }

    public event EventHandler<SegmentedSelectionChangedEventArgs>? SelectionChanged;

    protected override void OnSizeChanged(SizeChangedEventArgs e) {
        base.OnSizeChanged(e);
        CalibrateIndicator();
    }

    protected override void OnKeyDown(KeyEventArgs e) {
        if (_options.Count == 0) {
            base.OnKeyDown(e);
            return;
        }

        _track.Classes.Set("focused", true);
        _focusCue.Classes.Set("focused", true);

        var currentIndex = SelectedIndex;
        var targetIndex = e.Key switch {
            Key.Left => currentIndex <= 0 ? _options.Count - 1 : currentIndex - 1,
            Key.Right => currentIndex >= _options.Count - 1 ? 0 : currentIndex + 1,
            Key.Home => 0,
            Key.End => _options.Count - 1,
            Key.Space or Key.Enter => currentIndex,
            _ => -1,
        };

        if (targetIndex >= 0) {
            SetSelectedKey(_options[targetIndex].Key, raiseEvent: true, animate: true);
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) {
        CancelIndicatorAnimation();
        base.OnDetachedFromVisualTree(e);
    }

    private int SelectedIndex {
        get {
            var index = _options.ToList().FindIndex(option => option.Key == _selectedKey);
            return index >= 0 ? index : 0;
        }
    }

    private static void ValidateOptions(IReadOnlyList<SegmentedSelectorOption> options) {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var option in options) {
            if (string.IsNullOrWhiteSpace(option.Key))
                throw new ArgumentException("Segmented selector option keys cannot be empty.", nameof(options));
            if (!keys.Add(option.Key))
                throw new ArgumentException($"Segmented selector option key '{option.Key}' is duplicated.", nameof(options));
        }
    }

    private void SubscribeToOptions(IReadOnlyList<SegmentedSelectorOption> options) {
        if (options is not INotifyCollectionChanged observable) return;
        _observableOptions = observable;
        observable.CollectionChanged += OnOptionsCollectionChanged;
    }

    private void UnsubscribeFromOptions() {
        if (_observableOptions == null) return;
        _observableOptions.CollectionChanged -= OnOptionsCollectionChanged;
        _observableOptions = null;
    }

    private void OnOptionsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) {
        ValidateOptions(_options);
        RebuildOptions();
    }

    private void RebuildOptions() {
        CancelIndicatorAnimation();
        _optionsGrid.Children.Clear();
        _optionsGrid.ColumnDefinitions.Clear();
        _optionControls.Clear();

        for (var index = 0; index < _options.Count; index++) {
            var option = _options[index];
            _optionsGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

            var label = new TextBlock {
                Text = option.Label,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Classes = { "segment-label" },
            };
            var optionControl = new Border {
                Tag = option.Key,
                Child = label,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Classes = { "segment-option" },
            };
            Grid.SetColumn(optionControl, index);
            optionControl.PointerPressed += OnOptionPointerPressed;
            optionControl.PointerReleased += OnOptionPointerReleased;
            optionControl.PointerCaptureLost += OnOptionPointerCaptureLost;
            _optionsGrid.Children.Add(optionControl);
            _optionControls.Add(optionControl);
        }

        var previousKey = _selectedKey;
        var selectedStillExists = _options.Any(option => option.Key == previousKey);
        var nextKey = selectedStillExists ? _selectedKey : _options.FirstOrDefault()?.Key;
        SetSelectedKey(
            nextKey,
            raiseEvent: previousKey != null && !selectedStillExists,
            animate: false);
        UpdateOptionClasses();
        CalibrateIndicator();
    }

    private void OnOptionPointerPressed(object? sender, PointerPressedEventArgs e) {
        if (sender is not Border optionControl
            || !e.GetCurrentPoint(optionControl).Properties.IsLeftButtonPressed) return;

        _track.Classes.Set("focused", false);
        _focusCue.Classes.Set("focused", false);
        Focus(NavigationMethod.Pointer);
        optionControl.Classes.Set("pressed", true);
        e.Pointer.Capture(optionControl);
        e.Handled = true;
    }

    private void OnOptionPointerReleased(object? sender, PointerReleasedEventArgs e) {
        if (sender is not Border optionControl || e.InitialPressMouseButton != MouseButton.Left) return;

        optionControl.Classes.Set("pressed", false);
        e.Pointer.Capture(null);
        var point = e.GetPosition(optionControl);
        if (new Rect(optionControl.Bounds.Size).Contains(point) && optionControl.Tag is string key)
            SetSelectedKey(key, raiseEvent: true, animate: true);
        e.Handled = true;
    }

    private static void OnOptionPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) {
        if (sender is Border optionControl) optionControl.Classes.Set("pressed", false);
    }

    private void OnSelectorGotFocus(object? sender, GotFocusEventArgs e) {
        var showFocus = e.NavigationMethod != NavigationMethod.Pointer;
        _track.Classes.Set("focused", showFocus);
        _focusCue.Classes.Set("focused", showFocus);
    }

    private void OnSelectorLostFocus(object? sender, RoutedEventArgs e) {
        _track.Classes.Set("focused", false);
        _focusCue.Classes.Set("focused", false);
        foreach (var option in _optionControls) option.Classes.Set("pressed", false);
    }

    private void SetSelectedKey(string? key, bool raiseEvent, bool animate) {
        if (_options.Count == 0) key = null;
        else if (key == null || !_options.Any(option => option.Key == key)) key = _options[0].Key;
        if (_selectedKey == key) {
            UpdateOptionClasses();
            return;
        }

        var oldKey = _selectedKey;
        SetAndRaise(SelectedKeyProperty, ref _selectedKey, key);
        UpdateOptionClasses();
        MoveIndicator(animate);

        if (raiseEvent && key != null)
            SelectionChanged?.Invoke(this, new SegmentedSelectionChangedEventArgs(oldKey, key));
    }

    private void UpdateOptionClasses() {
        foreach (var optionControl in _optionControls)
            optionControl.Classes.Set("selected", Equals(optionControl.Tag, _selectedKey));
    }

    private void CalibrateIndicator() {
        CancelIndicatorAnimation();
        if (_options.Count == 0 || _selectedKey == null) {
            _indicator.IsVisible = false;
            return;
        }

        _segmentWidth = _optionsGrid.Bounds.Width / _options.Count;
        var height = _optionsGrid.Bounds.Height;
        if (_segmentWidth <= 0 || height <= 0) return;

        _indicator.IsVisible = true;
        _indicator.Width = _segmentWidth;
        _indicator.Height = height;
        SetIndicatorX(SelectedIndex * _segmentWidth);
    }

    private void MoveIndicator(bool animate) {
        if (_options.Count == 0 || _selectedKey == null) {
            _indicator.IsVisible = false;
            return;
        }

        if (_segmentWidth <= 0) {
            CalibrateIndicator();
            return;
        }

        _indicator.IsVisible = true;
        var target = SelectedIndex * _segmentWidth;
        if (!animate || VisualRoot == null) {
            CancelIndicatorAnimation();
            SetIndicatorX(target);
            return;
        }

        AnimateIndicator(target);
    }

    private void AnimateIndicator(double target) {
        var transform = EnsureIndicatorTransform();
        var from = transform.X;
        if (Math.Abs(target - from) < 0.25) {
            SetIndicatorX(target);
            return;
        }

        CancelIndicatorAnimation();
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) {
            SetIndicatorX(target);
            return;
        }

        FrameAnimation? animation = null;
        animation = new FrameAnimation(
            topLevel,
            MotionPreferences.StandardDuration,
            new CubicEaseOut(),
            progress => transform.X = from + ((target - from) * progress),
            () => {
                if (ReferenceEquals(_indicatorAnimation, animation)) _indicatorAnimation = null;
            });
        _indicatorAnimation = animation;
        animation.Start();
    }

    private TranslateTransform EnsureIndicatorTransform() {
        if (_indicator.RenderTransform is TranslateTransform transform) return transform;
        transform = new TranslateTransform();
        _indicator.RenderTransform = transform;
        return transform;
    }

    private void SetIndicatorX(double x) => EnsureIndicatorTransform().X = x;

    private void CancelIndicatorAnimation() {
        _indicatorAnimation?.Cancel();
        _indicatorAnimation = null;
    }
}
