namespace Memo.Models;

public class AppSettings {
    public CloseButtonAction CloseButtonAction { get; set; } = CloseButtonAction.MinimizeToTray;
    public bool HasAskedCloseButtonAction { get; set; }
    public HotkeySetting ToggleTopmostHotkey { get; set; } = new() { Key = "T", Ctrl = true, Alt = true };
    public HotkeySetting MinimizeHotkey { get; set; } = new() { Key = "M", Ctrl = true, Alt = true };
    public HotkeySetting ShowWindowHotkey { get; set; } = new() { Key = "N", Ctrl = true, Alt = true };

    public static AppSettings CreateDefault() => new();

    public AppSettings Clone() => new() {
        CloseButtonAction = CloseButtonAction,
        HasAskedCloseButtonAction = HasAskedCloseButtonAction,
        ToggleTopmostHotkey = ToggleTopmostHotkey.Clone(),
        MinimizeHotkey = MinimizeHotkey.Clone(),
        ShowWindowHotkey = ShowWindowHotkey.Clone(),
    };
}
