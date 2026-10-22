using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Mcroute
{
    /// <summary>
    /// 残留游戏进程清理。
    ///
    /// 场景：启动器自己崩溃/被强杀，或游戏窗口被关但进程没退干净，
    /// 于是后台留下一个没有窗口的 `javaw.exe`（标题界面的音乐就是它放的），
    /// 而它已经不在这棵进程树里了（父进程没了）。
    ///
    /// 判据：命令行里出现我们的数据目录 + Minecraft 特征（-cp / --gameDir / Foundation / forge）。
    /// </summary>
    public static class OrphanCleaner
    {
        private static readonly string[] Signs =
        {
            "minecraft", "Foundation", "cleanroom", "forge", "fabric", "--gameDir", "-cp "
        };

        /// <summary>找出看起来是「我们的数据目录下、正在跑的 Minecraft」的 java 进程。</summary>
        public static List<int> FindLeftovers(string baseDir)
        {
            var result = new List<int>();
            if (string.IsNullOrEmpty(baseDir)) return result;

            string root;
            try { root = Path.GetFullPath(baseDir).TrimEnd('\\'); }
            catch { return result; }

            try
            {
                foreach (string name in new[] { "java.exe", "javaw.exe" })
                {
                    foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(name)))
                    {
                        try
                        {
                            string cmd = GetCommandLine(p.Id);
                            if (string.IsNullOrEmpty(cmd)) continue;
                            if (cmd.IndexOf(root, StringComparison.OrdinalIgnoreCase) < 0) continue;

                            bool hasSign = Signs.Any(s => cmd.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0);
                            if (!hasSign) continue;

                            result.Add(p.Id);
                        }
                        catch { }
                        finally { try { p.Dispose(); } catch { } }
                    }
                }
            }
            catch { }
            return result.Distinct().ToList();
        }

        /// <summary>结束这些残留进程（含各自子进程树）。返回结束的数量。</summary>
        public static int Kill(List<int> pids)
        {
            int n = 0;
            foreach (int pid in pids)
            {
                try
                {
                    ProcessTree.KillTree(pid);
                    n++;
                }
                catch { }
            }
            return n;
        }

        private static string GetCommandLine(int pid)
        {
            try
            {
                using (var searcher = new System.Management.ManagementObjectSearcher(
                    "SELECT CommandLine FROM Win32_Process WHERE ProcessId=" + pid))
                {
                    foreach (System.Management.ManagementObject obj in searcher.Get())
                    {
                        object v = obj["CommandLine"];
                        return v == null ? null : v.ToString();
                    }
                }
            }
            catch { }
            return null;
        }
    }
}
