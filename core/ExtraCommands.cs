using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Mcroute
{
    /// <summary>verify / test / mkbat 三个命令。</summary>
    public static class ExtraCommands
    {
        // ================= verify（体检 / 补全） =================
        public static int Verify(string[] a)
        {
            string ver = Commands.Positional(a, 1);
            if (string.IsNullOrEmpty(ver)) { Console.WriteLine("[错误] 用法: verify <版本> [--fix] [--deep]"); return 1; }
            string baseDir = Version.BaseDir(Commands.Arg(a, "--dir"));
            bool fix = Commands.Has(a, "--fix");

            var plan = Commands.PlanLaunch(baseDir, ver, Commands.Arg(a, "--java"), null);
            if (plan == null) return 1;

            Console.WriteLine(new string('=', 62));
            Console.WriteLine("体检报告：" + ver);
            Console.WriteLine(new string('=', 62));

            int jarMissing = plan.Missing.Count(m => m.StartsWith("[客户端 jar]"));
            int libMissing = plan.Missing.Count(m => m.StartsWith("[库]"));
            int natMissing = plan.Missing.Count(m => m.StartsWith("[natives]"));
            int libTotal = plan.Classpath.Count;

            Console.WriteLine("  客户端 jar : " + (jarMissing > 0 ? "缺失" : "OK"));
            Console.WriteLine("  库          : {0}/{1} 存在", libTotal - libMissing, libTotal);
            Console.WriteLine("  natives     : {0}/{1} 存在", plan.NativesJars.Count - natMissing, plan.NativesJars.Count);
            foreach (string m in plan.Missing.Where(x => x.StartsWith("[库]")).Take(8))
                Console.WriteLine("      - 缺 " + Path.GetFileName(m.Substring(5)));
            if (libMissing > 8) Console.WriteLine("      ...还有 " + (libMissing - 8) + " 个");

            // assets
            int assetTotal = 0, assetMissing = 0;
            string indexPath = Path.Combine(baseDir, "assets", "indexes", plan.AssetIndex + ".json");
            if (!string.IsNullOrEmpty(plan.AssetIndex))
            {
                if (!File.Exists(indexPath))
                {
                    Console.WriteLine("  assets     : 索引缺失（还没下过资源）");
                }
                else
                {
                    try
                    {
                        var index = J.ParseFile(indexPath);
                        var objects = J.Dict(index, "objects");
                        if (objects != null)
                        {
                            assetTotal = objects.Count;
                            foreach (var kv in objects)
                            {
                                string hash = J.Str(kv.Value, "hash");
                                if (hash.Length < 2) continue;
                                if (!File.Exists(Path.Combine(baseDir, "assets", "objects", hash.Substring(0, 2), hash)))
                                    assetMissing++;
                            }
                        }
                    }
                    catch { }
                    Console.WriteLine("  assets     : {0}/{1} 存在", assetTotal - assetMissing, assetTotal);
                }
            }

            int problem = plan.Missing.Count + assetMissing;
            Console.WriteLine();
            if (problem == 0)
            {
                Console.WriteLine("[结论] 文件齐全，可以直接启动。");
                return 0;
            }
            Console.WriteLine("[结论] 还缺 {0} 项（{1} 个库/jar + {2} 个资源对象）。",
                problem, plan.Missing.Count, assetMissing);
            Console.WriteLine("       补全命令: mcroute verify " + ver + " --fix");

            if (fix)
            {
                Console.WriteLine();
                Console.WriteLine("开始补全 ...");
                return Commands.Fetch(new[] { "fetch", ver, "--dir", baseDir });
            }
            return 2;
        }

        // ================= test（测试启动） =================
        public static int Test(string[] a)
        {
            string ver = Commands.Positional(a, 1);
            if (string.IsNullOrEmpty(ver)) { Console.WriteLine("[错误] 用法: test <版本> [--timeout 90]"); return 1; }
            string baseDir = Version.BaseDir(Commands.Arg(a, "--dir"));
            string timeoutArg = Commands.Arg(a, "--timeout");
            int timeout = string.IsNullOrEmpty(timeoutArg) ? 90 : int.Parse(timeoutArg);

            var plan = Commands.PlanLaunch(baseDir, ver, Commands.Arg(a, "--java"), null);
            if (plan == null) return 1;
            if (plan.Missing.Count > 0)
            {
                Console.WriteLine("[中止] 有 " + plan.Missing.Count + " 个文件缺失，先跑 verify --fix。");
                return 1;
            }

            Directory.CreateDirectory(plan.GameDir);
            Directory.CreateDirectory(plan.NativesDir);
            Commands.ExtractNativesPublic(plan);

            var argv = new List<string>();
            argv.AddRange(plan.JvmArgs);
            argv.Add(plan.MainClass);
            argv.AddRange(plan.GameArgs);
            string cmdline = string.Join(" ", argv.Select(Commands.Quote));

            Console.WriteLine();
            Console.WriteLine("[测试] 启动游戏，最长等 " + timeout + " 秒 ...");
            var sw = Stopwatch.StartNew();

            var psi = new ProcessStartInfo(plan.Java, cmdline)
            {
                WorkingDirectory = plan.GameDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true,
            };

            var goodMarkers = new[] { "Setting user", "LWJGL", "Backend library", "Forge Mod Loader has successfully loaded", "Reloading ResourceManager" };
            var badMarkers = new[] { "Game crashed", "Could not find or load main class", "NoClassDefFoundError", "UnsatisfiedLinkError", "Exception in thread" };
            var goodSeen = new List<string>();
            var badSeen = new List<string>();
            var tail = new List<string>();

            using (var p = Process.Start(psi))
            {
                p.OutputDataReceived += (s, e) => { if (e.Data != null) CheckLine(e.Data); };
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) CheckLine(e.Data); };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                while (sw.Elapsed.TotalSeconds < timeout)
                {
                    if (p.HasExited && goodSeen.Count == 0) break;
                    if (badSeen.Count > 0) break;
                    if (goodSeen.Count >= 2) break;
                    System.Threading.Thread.Sleep(300);
                }
                try { if (!p.HasExited) p.Kill(); } catch { }
                try { p.WaitForExit(5000); } catch { }
            }

            Console.WriteLine();
            Console.WriteLine(new string('=', 62));
            if (badSeen.Count > 0)
            {
                Console.WriteLine("[测试结果] 失败 —— 启动过程中出现错误：");
                foreach (string line in badSeen.Take(3)) Console.WriteLine("    " + line);
                return 1;
            }
            if (goodSeen.Count > 0)
            {
                Console.WriteLine("[测试结果] 通过 —— 游戏完成初始化（看到 {0} 个正常标志）", goodSeen.Count);
                Console.WriteLine("          耗时 {0:F1} 秒", sw.Elapsed.TotalSeconds);
                return 0;
            }
            Console.WriteLine("[测试结果] 不确定 —— 没看到明显成功或失败标志");
            return 2;

            void CheckLine(string line)
            {
                lock (tail)
                {
                    tail.Add(line);
                    if (tail.Count > 40) tail.RemoveAt(0);
                    if (goodMarkers.Any(m => line.Contains(m))) goodSeen.Add(line);
                    else if (badMarkers.Any(m => line.Contains(m))) badSeen.Add(line);
                }
            }
        }

        // ================= mkbat（生成双击启动器） =================
        public static int Mkbat(string[] a)
        {
            string ver = Commands.Positional(a, 1);
            if (string.IsNullOrEmpty(ver)) { Console.WriteLine("[错误] 用法: mkbat <版本>"); return 1; }
            string baseDir = Version.BaseDir(Commands.Arg(a, "--dir"));

            var plan = Commands.PlanLaunch(baseDir, ver, Commands.Arg(a, "--java"), null);
            if (plan == null) return 1;

            string vdir = Path.Combine(baseDir, "versions", ver);
            if (!Directory.Exists(vdir)) vdir = plan.GameDir;
            Directory.CreateDirectory(vdir);

            var argv = new List<string>();
            argv.Add(plan.Java);
            argv.AddRange(plan.JvmArgs);
            argv.Add(plan.MainClass);
            argv.AddRange(plan.GameArgs);
            string cmdline = string.Join(" ", argv.Select(Commands.Quote));

            string batPath = Path.Combine(vdir, "launch.bat");
            var gbk = Encoding.GetEncoding(936);
            var header = new[]
            {
                "@echo off",
                "chcp 936 >nul",
                "cd /d \"%~dp0\"",
                "rem 由 mcroute 生成：双击启动本版本（其它启动器不受影响）",
            };

            if (cmdline.Length < 7800)
            {
                var lines = new List<string>(header);
                lines.Add(cmdline);
                lines.Add("if errorlevel 1 pause");
                File.WriteAllLines(batPath, lines, gbk);
                foreach (string stale in new[] { "launch.ps1", "launch-args.txt" })
                {
                    string sp = Path.Combine(vdir, stale);
                    if (File.Exists(sp)) File.Delete(sp);
                }
                Console.WriteLine("[已生成] " + batPath);
                Console.WriteLine("  命令长度 {0} 字符，直接内嵌。", cmdline.Length);
                return 0;
            }

            // 太长：生成 ps1，bat 只负责调用
            string ps1 = Path.Combine(vdir, "launch.ps1");
            var ps = new List<string>
            {
                "# 由 mcroute 生成：classpath 太长，走 PowerShell 避免命令行长度限制",
                "$java = " + QuotePs(plan.Java),
                "$argv = @(",
            };
            foreach (string arg in argv.Skip(1)) ps.Add("    " + QuotePs(arg) + ",");
            ps.Add(")");
            ps.Add("& $java @argv");
            File.WriteAllLines(ps1, ps, gbk);

            var batLines = new List<string>(header);
            batLines.Add("powershell -NoProfile -ExecutionPolicy Bypass -File \"%~dp0launch.ps1\"");
            batLines.Add("if errorlevel 1 pause");
            File.WriteAllLines(batPath, batLines, gbk);
            Console.WriteLine("[已生成] " + batPath);
            Console.WriteLine("         " + ps1 + "（classpath 太长，{0} 字符）", cmdline.Length);
            return 0;
        }

        private static string QuotePs(string s)
        {
            return "\"" + s.Replace("\"", "`\"") + "\"";
        }
    }
}
