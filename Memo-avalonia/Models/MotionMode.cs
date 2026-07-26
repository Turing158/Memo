namespace Memo.Models;

/// <summary>Controls how decorative motion is applied throughout the application.</summary>
public enum MotionMode {
    // Keep AlwaysOn as zero so settings files created by older versions retain
    // the application's existing motion behaviour when the property is absent.
    AlwaysOn = 0,
    FollowSystem = 1,
    Off = 2,
}
