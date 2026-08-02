namespace Memo.Models;

public class AppSettings {
    public const int MinimumMainWindowDockSize = 30;
    public const int MaximumMainWindowDockSize = 75;
    public const int DefaultMainWindowDockSize = 44;

    public ThemeMode ThemeMode { get; set; } = ThemeMode.FollowSystem;
    public MotionMode MotionMode { get; set; } = MotionMode.AlwaysOn;
    public CloseButtonAction CloseButtonAction { get; set; } = CloseButtonAction.MinimizeToTray;
    public bool HasAskedCloseButtonAction { get; set; }
    public HotkeySetting ToggleTopmostHotkey { get; set; } = new() { Key = "T", Ctrl = true, Alt = true };
    public HotkeySetting MinimizeHotkey { get; set; } = new() { Key = "M", Ctrl = true, Alt = true };
    public HotkeySetting ShowWindowHotkey { get; set; } = new() { Key = "N", Ctrl = true, Alt = true };
    public HotkeySetting QuickMemoHotkey { get; set; } = new() { Key = "C", Ctrl = true, Alt = true };
    public bool QuickMemoEnabled { get; set; } = true;
    /// <summary>重复便签：关闭时如果已存在相同备忘录的窗体则移动位置，开启时总是创建新窗体。</summary>
    public bool DuplicateMemoEnabled { get; set; }
    /// <summary>托盘图标单击显示主界面。默认 false，使用双击显示（与旧版行为一致）。</summary>
    public bool TraySingleClickToShow { get; set; }
    /// <summary>快速添加后自动显示便签：依赖 QuickMemoEnabled，仅在启用快速粘贴时才生效。</summary>
    public bool QuickMemoShowPopoutAfterAdd { get; set; }

    public bool MainWindowDockEnabled { get; set; } = true;
    public bool MainWindowDocked { get; set; }
    public int MainWindowDockSize { get; set; } = DefaultMainWindowDockSize;
    public MainWindowDockEdge MainWindowDockEdge { get; set; } = MainWindowDockEdge.Left;
    public double MainWindowDockPosition { get; set; } = 0.5;
    public int MainWindowDockWorkAreaX { get; set; }
    public int MainWindowDockWorkAreaY { get; set; }
    public int MainWindowDockWorkAreaWidth { get; set; }
    public int MainWindowDockWorkAreaHeight { get; set; }
    public bool MainWindowHasExpandedBounds { get; set; }
    public int MainWindowExpandedX { get; set; }
    public int MainWindowExpandedY { get; set; }
    public double MainWindowExpandedWidth { get; set; } = 420;
    public double MainWindowExpandedHeight { get; set; } = 680;
    public bool MainWindowTopmost { get; set; }

    public static AppSettings CreateDefault() => new() { MotionMode = MotionMode.AlwaysOn };

    public AppSettings Clone() => new() {
        ThemeMode = ThemeMode,
        MotionMode = MotionMode,
        CloseButtonAction = CloseButtonAction,
        HasAskedCloseButtonAction = HasAskedCloseButtonAction,
        ToggleTopmostHotkey = ToggleTopmostHotkey.Clone(),
        MinimizeHotkey = MinimizeHotkey.Clone(),
        ShowWindowHotkey = ShowWindowHotkey.Clone(),
        QuickMemoHotkey = QuickMemoHotkey.Clone(),
        QuickMemoEnabled = QuickMemoEnabled,
        DuplicateMemoEnabled = DuplicateMemoEnabled,
        TraySingleClickToShow = TraySingleClickToShow,
        QuickMemoShowPopoutAfterAdd = QuickMemoShowPopoutAfterAdd,
        MainWindowDockEnabled = MainWindowDockEnabled,
        MainWindowDocked = MainWindowDocked,
        MainWindowDockSize = MainWindowDockSize,
        MainWindowDockEdge = MainWindowDockEdge,
        MainWindowDockPosition = MainWindowDockPosition,
        MainWindowDockWorkAreaX = MainWindowDockWorkAreaX,
        MainWindowDockWorkAreaY = MainWindowDockWorkAreaY,
        MainWindowDockWorkAreaWidth = MainWindowDockWorkAreaWidth,
        MainWindowDockWorkAreaHeight = MainWindowDockWorkAreaHeight,
        MainWindowHasExpandedBounds = MainWindowHasExpandedBounds,
        MainWindowExpandedX = MainWindowExpandedX,
        MainWindowExpandedY = MainWindowExpandedY,
        MainWindowExpandedWidth = MainWindowExpandedWidth,
        MainWindowExpandedHeight = MainWindowExpandedHeight,
        MainWindowTopmost = MainWindowTopmost,
    };
}
