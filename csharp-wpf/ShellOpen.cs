using System;
using System.Runtime.InteropServices;

namespace Mcroute
{
    /// <summary>
    /// 打开文件夹用的 Shell API（不 spawn explorer.exe —— 某些机器上 explorer 二次启动会 0xc0000142）。
    /// </summary>
    public static class ShellOpen
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr ILCreateFromPath(string pszPath);

        [DllImport("shell32.dll")]
        public static extern void ILFree(IntPtr pidl);

        [DllImport("shell32.dll")]
        public static extern int SHOpenFolderAndSelectItems(IntPtr pidlFolder, uint cidl, IntPtr[] apidl, uint dwFlags);

        /// <summary>尝试在资源管理器里打开并选中；成功返回 true。</summary>
        public static bool TryOpenAndSelect(string dir)
        {
            try
            {
                IntPtr pidl = ILCreateFromPath(dir);
                if (pidl == IntPtr.Zero) return false;
                try { return SHOpenFolderAndSelectItems(pidl, 0, null, 0) == 0; }
                finally { ILFree(pidl); }
            }
            catch { return false; }
        }
    }
}
