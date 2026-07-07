using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using System;
using System.Diagnostics;

namespace note_avalonia;

internal sealed class WindowTransitionController {
    private readonly Window _window;
    private readonly Control _shell;
    private DispatcherTimer? _timer;
    private bool _isTransitioning;

    public WindowTransitionController(Window window, Control shell) {
        _window = window;
        _shell = shell;
    }

    public void PrepareOpen() {
        _window.Opacity = 0;
        SetScale(0.97);
    }

    public void PlayOpen() {
        Play(TimeSpan.FromMilliseconds(180), _window.Opacity, 1, CurrentScale(), 1, new CubicEaseOut(), null);
    }

    public void CloseAfterTransition(Action close) {
        if (_isTransitioning) return;

        Play(TimeSpan.FromMilliseconds(145), _window.Opacity, 0, CurrentScale(), 0.985, new CubicEaseIn(), close);
    }

    private void Play(
        TimeSpan duration,
        double fromOpacity,
        double toOpacity,
        double fromScale,
        double toScale,
        IEasing easing,
        Action? completed) {
        _timer?.Stop();
        _isTransitioning = true;

        var sw = Stopwatch.StartNew();
        _window.Opacity = fromOpacity;
        SetScale(fromScale);

        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => { });
        timer.Tick += (_, _) => {
            var t = sw.Elapsed >= duration ? 1.0 : sw.Elapsed.TotalMilliseconds / duration.TotalMilliseconds;
            var eased = easing.Ease(t);

            _window.Opacity = fromOpacity + ((toOpacity - fromOpacity) * eased);
            SetScale(fromScale + ((toScale - fromScale) * eased));

            if (t >= 1.0) {
                timer.Stop();
                _window.Opacity = toOpacity;
                SetScale(toScale);
                _timer = null;
                _isTransitioning = false;
                completed?.Invoke();
            }
        };

        _timer = timer;
        timer.Start();
    }

    private double CurrentScale() => _shell.RenderTransform is ScaleTransform scale ? scale.ScaleX : 1;

    private void SetScale(double value) {
        if (_shell.RenderTransform is ScaleTransform scale) {
            scale.ScaleX = value;
            scale.ScaleY = value;
        }
    }
}
