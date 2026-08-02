using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using System.Linq;

namespace Memo.Utils;

internal static class TitleBarDragHelper {
    public static bool CanStartDrag(Window window, PointerPressedEventArgs e) {
        if (!e.GetCurrentPoint(window).Properties.IsLeftButtonPressed) return false;
        if (e.Source is not Visual source) return true;

        return !source.GetVisualAncestors()
            .Append(source)
            .OfType<Button>()
            .Any();
    }
}
