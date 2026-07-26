using Avalonia;
using Avalonia.Threading;
using Memo.Models;
using Microsoft.Win32;
using System;
using System.Runtime.InteropServices;

namespace Memo.UI;

internal static class MotionPreferences {
    private const uint SpiGetClientAreaAnimation = 0x1042;
    private static readonly TimeSpan FastDurationValue = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan StandardDurationValue = TimeSpan.FromMilliseconds(190);
    private static readonly TimeSpan DockDurationValue = TimeSpan.FromMilliseconds(220);
    private static Application? _application;
    private static bool _initialized;

    public static event EventHandler? Changed;

    public static MotionMode Mode { get; private set; } = MotionMode.AlwaysOn;
    public static bool SystemAnimationsEnabled { get; private set; } = true;
    public static bool AnimationsEnabled => Mode switch {
        MotionMode.AlwaysOn => true,
        MotionMode.FollowSystem => SystemAnimationsEnabled,
        MotionMode.Off => false,
        _ => true,
    };

    public static TimeSpan FastDuration => Effective(FastDurationValue);
    public static TimeSpan StandardDuration => Effective(StandardDurationValue);
    public static TimeSpan DockDuration => Effective(DockDurationValue);

    public static void Initialize(Application application) {
        if (_initialized) Shutdown();
        _application = application;
        _initialized = true;
        RefreshSystemPreference(notify: false);
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        UpdateResources();
    }

    public static void ApplyMode(MotionMode mode) {
        if (!Enum.IsDefined(mode)) mode = MotionMode.AlwaysOn;
        var oldEnabled = AnimationsEnabled;
        var modeChanged = Mode != mode;
        Mode = mode;
        if (mode == MotionMode.FollowSystem) RefreshSystemPreference(notify: false);
        UpdateResources();
        if (modeChanged || oldEnabled != AnimationsEnabled) Changed?.Invoke(null, EventArgs.Empty);
    }

    public static TimeSpan Effective(TimeSpan duration) =>
        AnimationsEnabled ? duration : TimeSpan.Zero;

    public static TimeSpan AdaptiveDuration(
        double distance,
        double distanceForMaximum = 400,
        double minimumMilliseconds = 120,
        double maximumMilliseconds = 220) {
        var ratio = Math.Clamp(distance / Math.Max(1, distanceForMaximum), 0, 1);
        return Effective(TimeSpan.FromMilliseconds(
            minimumMilliseconds + ((maximumMilliseconds - minimumMilliseconds) * ratio)));
    }

    public static void RefreshSystemPreference() => RefreshSystemPreference(notify: true);

    public static void Shutdown() {
        if (!_initialized) return;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _initialized = false;
        _application = null;
        Changed = null;
    }

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) {
        if (e.Category is UserPreferenceCategory.Accessibility
            or UserPreferenceCategory.General
            or UserPreferenceCategory.VisualStyle) {
            Dispatcher.UIThread.Post(() => RefreshSystemPreference(notify: true));
        }
    }

    private static void RefreshSystemPreference(bool notify) {
        var previous = SystemAnimationsEnabled;
        var enabled = true;
        try {
            if (OperatingSystem.IsWindows()
                && !SystemParametersInfo(SpiGetClientAreaAnimation, 0, out enabled, 0)) {
                enabled = true;
            }
        }
        catch {
            enabled = true;
        }

        SystemAnimationsEnabled = enabled;
        UpdateResources();
        if (notify && previous != enabled) Changed?.Invoke(null, EventArgs.Empty);
    }

    private static void UpdateResources() {
        if (_application == null) return;
        _application.Resources["MotionFastDuration"] = FastDuration;
        _application.Resources["MotionStandardDuration"] = StandardDuration;
        _application.Resources["MotionDockDuration"] = DockDuration;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(
        uint uiAction,
        uint uiParam,
        [MarshalAs(UnmanagedType.Bool)] out bool pvParam,
        uint fWinIni);
}
