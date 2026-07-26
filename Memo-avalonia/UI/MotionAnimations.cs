using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using System;
using System.Runtime.CompilerServices;

namespace Memo.UI;

internal static class MotionAnimations {
    private sealed class Slot {
        public FrameAnimation? Animation;
    }

    private static readonly ConditionalWeakTable<object, Slot> Slots = new();

    public static void Start(
        object channel,
        TopLevel topLevel,
        TimeSpan duration,
        IEasing easing,
        Action<double> frame,
        Action? completed = null) {
        var slot = Slots.GetOrCreateValue(channel);
        slot.Animation?.Cancel();
        FrameAnimation? animation = null;
        animation = new FrameAnimation(topLevel, duration, easing, frame, () => {
            if (ReferenceEquals(slot.Animation, animation)) slot.Animation = null;
            completed?.Invoke();
        });
        slot.Animation = animation;
        slot.Animation.Start();
    }

    public static void Cancel(object channel) {
        if (!Slots.TryGetValue(channel, out var slot)) return;
        slot.Animation?.Cancel();
        slot.Animation = null;
    }

    public static void AnimateRotation(
        PathIcon icon,
        TopLevel topLevel,
        double targetAngle,
        bool animate = true,
        Action? completed = null) {
        if (icon.RenderTransform is not RotateTransform transform) return;
        var from = transform.Angle;
        if (!animate || Math.Abs(targetAngle - from) < 0.01) {
            Cancel(icon);
            transform.Angle = targetAngle;
            completed?.Invoke();
            return;
        }

        Start(icon, topLevel, MotionPreferences.StandardDuration, new CubicEaseOut(),
            progress => transform.Angle = from + ((targetAngle - from) * progress), completed);
    }

    public static double Lerp(double from, double to, double progress) =>
        from + ((to - from) * progress);
}
