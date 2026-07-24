using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using System.Linq;

namespace Memo.Utils;

/// <summary>
/// 窗体缩放手柄的辅助：给带 Tag 的边缘/四角透明 Border 设置对应方向的光标。
/// 8 个手柄统一靠 Tag（Top/Bottom/Left/Right/TopLeft/TopRight/BottomLeft/BottomRight）区分方向。
/// Cursor.Parse 仅识别 StandardCursorType 枚举名（TopSide、TopLeftCorner 等），所以这里按 Tag
/// 映射到确切枚举值，避免手写字符串出错导致启动崩溃。
///
/// 注意：必须在可视化树就绪后再调用本方法（例如窗口的 Loaded 事件里），否则 GetVisualDescendants
/// 遍历不到任何 Border，光标会静默赋值 0 次（这是静默逻辑失误，不会抛异常）。
/// </summary>
public static class ResizeHandleHelper {
    public static void AssignResizeCursors(this Control owner) {
        foreach (var child in owner.GetVisualDescendants().OfType<Border>()) {
            if (child.Tag is not string tag) continue;
            child.Cursor = tag switch {
                "Top"         => new Cursor(StandardCursorType.TopSide),
                "Bottom"      => new Cursor(StandardCursorType.BottomSide),
                "Left"        => new Cursor(StandardCursorType.LeftSide),
                "Right"       => new Cursor(StandardCursorType.RightSide),
                "TopLeft"     => new Cursor(StandardCursorType.TopLeftCorner),
                "TopRight"    => new Cursor(StandardCursorType.TopRightCorner),
                "BottomLeft"  => new Cursor(StandardCursorType.BottomLeftCorner),
                "BottomRight" => new Cursor(StandardCursorType.BottomRightCorner),
                _             => child.Cursor
            };
        }
    }
}
