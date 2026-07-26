using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using System;

namespace Memo.UI;

internal sealed class WindowTransitionController {
    private readonly Window _window;
    private readonly Control _shell;
    private FrameAnimation? _animation;

    public WindowTransitionController(Window window, Control shell) {
        _window = window;
        _shell = shell;
    }

    public void PrepareOpen() {
        Cancel();
        if (!MotionPreferences.AnimationsEnabled) {
            _window.Opacity = 1;
            SetScale(1);
            return;
        }
        _window.Opacity = 0;
        SetScale(0.97);
    }

    public bool IsTransitioning => _animation?.IsRunning == true;

    public void PlayOpen(Action? completed = null) {
        if (!MotionPreferences.AnimationsEnabled) {
            Reset();
            completed?.Invoke();
            return;
        }
        Play(MotionPreferences.StandardDuration, _window.Opacity, 1, CurrentScale(), 1, new CubicEaseOut(), completed);
    }

    public void CloseAfterTransition(Action close) {
        if (!MotionPreferences.AnimationsEnabled) {
            Reset();
            close();
            return;
        }
        Play(MotionPreferences.FastDuration, _window.Opacity, 0, CurrentScale(), 0.985, new CubicEaseIn(), close);
    }

    public void Cancel() {
        _animation?.Cancel();
        _animation = null;
    }

    public void Reset(double opacity = 1, double scale = 1) {
        Cancel();
        _window.Opacity = opacity;
        SetScale(scale);
    }

    private void Play(
        TimeSpan duration,
        double fromOpacity,
        double toOpacity,
        double fromScale,
        double toScale,
        IEasing easing,
        Action? completed) {
        _animation?.Cancel();

        _window.Opacity = fromOpacity;
        SetScale(fromScale);

        _animation = new FrameAnimation(_window, duration, easing, progress => {
            _window.Opacity = fromOpacity + ((toOpacity - fromOpacity) * progress);
            SetScale(fromScale + ((toScale - fromScale) * progress));
        }, () => {
            _animation = null;
            completed?.Invoke();
        });
        _animation.Start();
    }

    private double CurrentScale() => _shell.RenderTransform is ScaleTransform scale ? scale.ScaleX : 1;

    private void SetScale(double value) {
        if (_shell.RenderTransform is ScaleTransform scale) {
            scale.ScaleX = value;
            scale.ScaleY = value;
        }
    }
}
