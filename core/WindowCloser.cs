using System;
using System.Runtime.InteropServices;

namespace Mcroute
{
    /// <summary>
    /// 给某个进程的所有顶层窗口发 WM_CLOSE（"优雅关闭"）。
    ///
    /// 为什么要枚举：Minecraft（LWJGL）的主窗口句柄有时拿不到
    /// （`Process.MainWindowHandle` 返回 0 或只命中启动器壳），
    /// 只发一个窗口等于没关，表现就是"界面没了但游戏还在跑"。
    /// </summary>
    public static class WindowCloser
    {
        private const uint WM_CLOSE = 0x0010;

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        /// <summary>给该 pid 的所有可见顶层窗口发 WM_CLOSE，返回发送成功的窗口数。</summary>
        public static int CloseAll(int pid)
        {
            int sent = 0;
            try
            {
                EnumWindows((hWnd, lParam) =>
                {
                    uint wpid;
                    GetWindowThreadProcessId(hWnd, out wpid);
                    if (wpid == (uint)pid && IsWindowVisible(hWnd))
                    {
                        try { if (PostMessage(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero)) sent++; } catch { }
                    }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
            return sent;
        }

        /// <summary>该进程还有没有可见窗口。</summary>
        public static bool HasVisibleWindow(int pid)
        {
            bool found = false;
            try
            {
                EnumWindows((hWnd, lParam) =>
                {
                    uint wpid;
                    GetWindowThreadProcessId(hWnd, out wpid);
                    if (wpid == (uint)pid && IsWindowVisible(hWnd)) { found = true; return false; }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
            return found;
        }
    }
}
