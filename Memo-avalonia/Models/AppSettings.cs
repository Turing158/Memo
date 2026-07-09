namespace Memo.Models;

public class AppSettings {
    public CloseButtonAction CloseButtonAction { get; set; } = CloseButtonAction.MinimizeToTray;
    public bool HasAskedCloseButtonAction { get; set; }
    public HotkeySetting ToggleTopmostHotkey { get; set; } = new() { Key = "T", Ctrl = true, Alt = true };
    public HotkeySetting MinimizeHotkey { get; set; } = new() { Key = "M", Ctrl = true, Alt = true };
    public HotkeySetting ShowWindowHotkey { get; set; } = new() { Key = "N", Ctrl = true, Alt = true };
    public HotkeySetting QuickMemoHotkey { get; set; } = new() { Key = "C", Ctrl = true, Alt = true };
    public bool QuickMemoEnabled { get; set; } = true;
    /// <summary>重复便签：关闭时如果已存在相同备忘录的窗体则移动位置，开启时总是创建新窗体。</summary>
    public bool DuplicateMemoEnabled { get; set; }

    public static AppSettings CreateDefault() => new();

    public AppSettings Clone() => new() {
        CloseButtonAction = CloseButtonAction,
        HasAskedCloseButtonAction = HasAskedCloseButtonAction,
        ToggleTopmostHotkey = ToggleTopmostHotkey.Clone(),
        MinimizeHotkey = MinimizeHotkey.Clone(),
        ShowWindowHotkey = ShowWindowHotkey.Clone(),
        QuickMemoHotkey = QuickMemoHotkey.Clone(),
        QuickMemoEnabled = QuickMemoEnabled,
        DuplicateMemoEnabled = DuplicateMemoEnabled,
    };
}
