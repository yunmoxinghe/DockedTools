using System;
using System.Runtime.InteropServices;

namespace DockedTools.Features.AppEntry
{
    /// <summary>
    /// 应用入口阶段用到的最小 Win32 接口集。
    /// 这里主要做的是“发现已有实例后，把它叫出来并切到前台”。
    /// </summary>
    internal static partial class AppEntryWin32Api
    {
        // 改窗口显示状态。
        // 这里主要用来把已存在实例的窗口恢复出来，或者把保活窗口藏起来。
        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);

        // 尝试把某个窗口切到前台。
        // 人话就是“让用户眼前看到它，并把输入焦点给它”。
        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetForegroundWindow(IntPtr hWnd);

        // 普通显示/还原窗口。
        internal const int SW_SHOWNORMAL = 1;
        // 隐藏窗口。
        internal const int SW_HIDE = 0;

        // 取当前进程的伪句柄。
        [LibraryImport("kernel32.dll")]
        internal static partial IntPtr GetCurrentProcess();

        // 强制结束进程（不跑 finalizer、不等线程收尾，内核直接终止）。
        //
        // 【为什么不能用 Process.GetCurrentProcess().Kill() 代替】
        // Environment.Exit(0) 一旦被 finalizer 拖住，进程已经处在 .NET 的退出流程里，
        // 此时 Process.Kill() 会因为「进程正在退出」抛异常 —— 而调用处是 catch {}，
        // 异常被静默吞掉，进程就那么永久挂着，单实例 Mutex 也跟着不放。
        // TerminateProcess 绕开 .NET 的全部状态检查，是这种情况下唯一可靠的收尾手段。
        [LibraryImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool TerminateProcess(IntPtr hProcess, uint uExitCode);

        /// <summary>
        /// 硬终止自己。返回 false 表示连这一步都失败了（极罕见，一般意味着句柄已失效）。
        /// </summary>
        internal static bool HardKillSelf(uint exitCode = 0)
        {
            try
            {
                return TerminateProcess(GetCurrentProcess(), exitCode);
            }
            catch
            {
                return false;
            }
        }
    }
}
