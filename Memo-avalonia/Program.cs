using Avalonia;
using Memo.Platform.Windows;
using System;

namespace Memo;

class Program {
    [STAThread]
    public static void Main(string[] args) {
        // 单实例检测：若已有实例正在运行，则通知它显示主窗口并退出，避免重复启动。
        if (!SingleInstance.TryAcquire(out var mutex))
        {
            SingleInstance.NotifyExistingInstance();
            return;
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            SingleInstance.Release();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
