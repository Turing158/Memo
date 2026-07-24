using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Memo.Platform.Windows;

/// <summary>
/// 单实例检测：通过命名互斥体保证同一时间只运行一个进程。
/// 若已有实例在运行，则通过系统广播注册消息通知已有实例显示主窗口，随后退出当前进程。
/// </summary>
public static class SingleInstance
{
    // 命名互斥体在整个进程生命周期内由静态字段持有，避免被 GC 导致互斥释放后又被第二个进程抢到。
    private static Mutex? _mutex;

    private const string MutexName = "MemoAppSingleInstance";

    // WM_BROADCAST 目标：向所有顶层窗口广播。
    private static readonly IntPtr HwndBroadcast = new(0xffff);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private static readonly string RestoreMessageString = "Memo.RestoreInstance.v1";
    private static uint _restoreMessageId;

    /// <summary>
    /// 尝试成为唯一运行实例。成功返回 true；若已被其他实例占用，通知它并返回 false。
    /// </summary>
    public static bool TryAcquire(out Mutex mutex)
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        mutex = _mutex;
        return createdNew;
    }

    public static void Release()
    {
        try
        {
            _mutex?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // 当前线程并未持有互斥体时忽略。
        }
        _mutex?.Dispose();
        _mutex = null;
    }

    /// <summary>
    /// 向正在运行的老实例发送“显示主窗口”广播消息。
    /// </summary>
    public static void NotifyExistingInstance()
    {
        if (_restoreMessageId == 0)
        {
            _restoreMessageId = RegisterWindowMessage(RestoreMessageString);
        }
        if (_restoreMessageId == 0) return;

        PostMessage(HwndBroadcast, _restoreMessageId, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>
    /// 获取“显示主窗口”广播消息的注册 ID，供应用内 NativeWindow 监听。
    /// </summary>
    public static uint GetRestoreMessageId()
    {
        if (_restoreMessageId == 0)
        {
            _restoreMessageId = RegisterWindowMessage(RestoreMessageString);
        }
        return _restoreMessageId;
    }
}
