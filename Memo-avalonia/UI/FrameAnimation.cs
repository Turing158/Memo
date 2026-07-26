using Avalonia.Animation.Easings;
using Avalonia.Controls;
using System;

namespace Memo.UI;

/// <summary>
/// A small render-clock animation. Every start/cancel advances the generation so a
/// callback already queued by Avalonia can never mutate state after being replaced.
/// </summary>
internal sealed class FrameAnimation {
    private readonly TopLevel _topLevel;
    private readonly TimeSpan _duration;
    private readonly IEasing _easing;
    private readonly Action<double> _frame;
    private readonly Action? _completed;
    private long _generation;
    private TimeSpan? _startedAt;

    public FrameAnimation(
        TopLevel topLevel,
        TimeSpan duration,
        IEasing easing,
        Action<double> frame,
        Action? completed = null) {
        _topLevel = topLevel;
        _duration = duration;
        _easing = easing;
        _frame = frame;
        _completed = completed;
    }

    public bool IsRunning { get; private set; }

    public void Start() {
        if (IsRunning) Cancel();
        var generation = ++_generation;
        _startedAt = null;
        IsRunning = true;
        MotionPreferences.Changed += OnMotionPreferencesChanged;

        if (_duration <= TimeSpan.Zero) {
            Complete(generation);
            return;
        }

        _topLevel.RequestAnimationFrame(timestamp => OnFrame(timestamp, generation));
    }

    public void Cancel() {
        if (!IsRunning) return;
        ++_generation;
        IsRunning = false;
        _startedAt = null;
        MotionPreferences.Changed -= OnMotionPreferencesChanged;
    }

    private void OnFrame(TimeSpan timestamp, long generation) {
        if (!IsRunning || generation != _generation) return;
        _startedAt ??= timestamp;
        var elapsed = timestamp - _startedAt.Value;
        var progress = Math.Clamp(elapsed.TotalMilliseconds / _duration.TotalMilliseconds, 0, 1);
        if (progress >= 1) {
            Complete(generation);
            return;
        }

        _frame(_easing.Ease(progress));

        _topLevel.RequestAnimationFrame(next => OnFrame(next, generation));
    }

    private void Complete(long generation) {
        if (!IsRunning || generation != _generation) return;
        _frame(1);
        IsRunning = false;
        _startedAt = null;
        MotionPreferences.Changed -= OnMotionPreferencesChanged;
        _completed?.Invoke();
    }

    private void OnMotionPreferencesChanged(object? sender, EventArgs e) {
        if (IsRunning && !MotionPreferences.AnimationsEnabled) Complete(_generation);
    }
}
