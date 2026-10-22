using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Mcroute
{
    /// <summary>找 Java：按版本 json 要求的大版本匹配。</summary>
    public static class Java
    {
        private static readonly Dictionary<string, int> Cache = new Dictionary<string, int>();

        public static string Find(string baseDir, int required, string explicitPath, out int foundMajor)
        {
            foundMajor = 0;

            if (!string.IsNullOrEmpty(explicitPath) && File.Exists(explicitPath))
            {
                foundMajor = Major(explicitPath);
                return explicitPath;
            }

            var candidates = new List<string>();
            // 1) PATH
            string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string dir in pathEnv.Split(';'))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                string exe = Path.Combine(dir.Trim().Trim('"'), "java.exe");
                if (File.Exists(exe)) candidates.Add(exe);
            }
            // 2) 常见安装目录
            var roots = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Eclipse Adoptium"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Java"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft", "runtime"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ModrinthApp", "meta", "java_versions"),
                Path.Combine(baseDir, "java_versions"),
            };
            foreach (string root in roots)
            {
                if (!Directory.Exists(root)) continue;
                foreach (string sub in Directory.GetDirectories(root))
                {
                    string exe = Path.Combine(sub, "bin", "java.exe");
                    if (File.Exists(exe)) candidates.Add(exe);
                    string exe2 = Path.Combine(sub, "jre", "bin", "java.exe");
                    if (File.Exists(exe2)) candidates.Add(exe2);
                }
            }

            var found = new List<Tuple<string, int>>();
            foreach (string exe in candidates.Distinct())
            {
                int major = Major(exe);
                if (major > 0) found.Add(Tuple.Create(exe, major));
            }

            if (found.Count == 0) { foundMajor = 0; return "java"; }

            var exact = found.FirstOrDefault(t => t.Item2 == required);
            if (exact != null) { foundMajor = exact.Item2; return exact.Item1; }

            var closest = found.OrderBy(t => Math.Abs(t.Item2 - required)).First();
            foundMajor = closest.Item2;
            return closest.Item1;
        }


        /// <summary>
        /// 找「至少 minMajor」的 Java（Cleanroom 要 17+），返回 javaw.exe 路径。
        /// 优先 21（Cleanroom 官方测试版），其次 25，再其次任意 >= minMajor。
        /// </summary>
        public static string FindBestAtLeast(string baseDir, int minMajor)
        {
            try
            {
                var found = new List<Tuple<string, int>>();
                foreach (string exe in Candidates(baseDir))
                {
                    int major = Major(exe);
                    if (major >= minMajor) found.Add(Tuple.Create(exe, major));
                }
                if (found.Count == 0) return null;

                var best = found
                    .OrderBy(t => t.Item2 == 21 ? 0 : (t.Item2 == 25 ? 1 : 2))
                    .ThenBy(t => Math.Abs(t.Item2 - 21))
                    .First();

                string javaw = best.Item1.Replace("java.exe", "javaw.exe");
                return File.Exists(javaw) ? javaw : best.Item1;
            }
            catch { return null; }
        }

        /// <summary>候选 java.exe 列表（PATH + 常见安装目录 + 数据目录）。</summary>
        private static List<string> Candidates(string baseDir)
        {
            var candidates = new List<string>();
            string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string dir in pathEnv.Split(';'))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                string exe = Path.Combine(dir.Trim().Trim('"'), "java.exe");
                if (File.Exists(exe)) candidates.Add(exe);
            }
            var roots = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Eclipse Adoptium"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Java"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft", "runtime"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ModrinthApp", "meta", "java_versions"),
                Path.Combine(baseDir ?? ".", "java_versions"),
            };
            foreach (string root in roots)
            {
                if (!Directory.Exists(root)) continue;
                foreach (string sub in Directory.GetDirectories(root))
                {
                    string exe = Path.Combine(sub, "bin", "java.exe");
                    if (File.Exists(exe)) candidates.Add(exe);
                    string exe2 = Path.Combine(sub, "jre", "bin", "java.exe");
                    if (File.Exists(exe2)) candidates.Add(exe2);
                }
            }
            return candidates.Distinct().ToList();
        }

        /// <summary>跑一次 java -version 解析主版本号（结果缓存）。</summary>
        public static int Major(string exe)
        {
            int cached;
            if (Cache.TryGetValue(exe, out cached)) return cached;

            int major = 0;
            try
            {
                var psi = new ProcessStartInfo(exe, "-version")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                using (var p = Process.Start(psi))
                {
                    string text = p.StandardError.ReadToEnd() + p.StandardOutput.ReadToEnd();
                    p.WaitForExit(15000);
                    var m = Regex.Match(text, "version \"(\\d+)");
                    if (m.Success)
                    {
                        int first = int.Parse(m.Groups[1].Value);
                        major = first == 1 ? 8 : first;
                    }
                }
            }
            catch { major = 0; }

            Cache[exe] = major;
            return major;
        }
    }
}
