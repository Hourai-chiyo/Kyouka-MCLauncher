using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Mcroute
{
    /// <summary>
    /// 进程树工具。
    ///
    /// 为什么要它：Cleanroom/Forge 的启动链是 `启动器 → java.exe → javaw.exe（真正游戏）`，
    /// 我们拿到的是第一段 java.exe。只关/杀它，会把 javaw 留在后台继续放音乐。
    /// 所以关闭游戏必须按「整棵树」处理。
    /// </summary>
    public static class ProcessTree
    {
        private const uint TH32CS_SNAPPROCESS = 0x00000002;

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private struct PROCESSENTRY32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr hObject);

        /// <summary>pid 的所有子孙进程 id（不含自己）。</summary>
        public static List<int> Descendants(int pid)
        {
            var children = new Dictionary<int, List<int>>();
            try
            {
                IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
                if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return new List<int>();

                var entry = new PROCESSENTRY32();
                entry.dwSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(PROCESSENTRY32));
                if (Process32First(snap, ref entry))
                {
                    do
                    {
                        int child = (int)entry.th32ProcessID;
                        int parent = (int)entry.th32ParentProcessID;
                        if (!children.ContainsKey(parent)) children[parent] = new List<int>();
                        children[parent].Add(child);
                        entry.dwSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(PROCESSENTRY32));
                    } while (Process32Next(snap, ref entry));
                }
            }
            catch { }
            finally { }

            var result = new List<int>();
            var queue = new Queue<int>();
            queue.Enqueue(pid);
            var seen = new HashSet<int> { pid };
            while (queue.Count > 0)
            {
                int cur = queue.Dequeue();
                List<int> kids;
                if (!children.TryGetValue(cur, out kids)) continue;
                foreach (int k in kids)
                {
                    if (seen.Add(k)) { result.Add(k); queue.Enqueue(k); }
                }
            }
            return result;
        }

        /// <summary>整棵树（自己 + 子孙）。</summary>
        public static List<int> Tree(int pid)
        {
            var list = new List<int> { pid };
            list.AddRange(Descendants(pid));
            return list;
        }

        public static bool IsAlive(int pid)
        {
            try
            {
                using (var p = Process.GetProcessById(pid)) return !p.HasExited;
            }
            catch { return false; }
        }

        /// <summary>树里还有多少个活着的进程。</summary>
        public static List<int> AliveInTree(int pid)
        {
            return Tree(pid).Where(IsAlive).ToList();
        }

        /// <summary>给整棵树的所有窗口发 WM_CLOSE，返回发送成功的窗口数。</summary>
        public static int CloseWindowsInTree(int pid)
        {
            int sent = 0;
            foreach (int p in Tree(pid))
            {
                try { sent += WindowCloser.CloseAll(p); } catch { }
            }
            return sent;
        }

        /// <summary>强杀整棵树（taskkill /T /F，最可靠）。</summary>
        public static void KillTree(int pid)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "taskkill",
                    Arguments = "/PID " + pid + " /T /F",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                };
                var p = Process.Start(psi);
                if (p != null) p.WaitForExit(5000);
            }
            catch { }

            // 兜底：逐个 Kill
            foreach (int id in Tree(pid))
            {
                try { using (var p = Process.GetProcessById(id)) p.Kill(); } catch { }
            }
        }

        /// <summary>尽量优雅地让整棵树退出：taskkill /T（不带 /F）。</summary>
        public static void SoftKillTree(int pid)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "taskkill",
                    Arguments = "/PID " + pid + " /T",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                };
                var p = Process.Start(psi);
                if (p != null) p.WaitForExit(3000);
            }
            catch { }
        }
    }
}
