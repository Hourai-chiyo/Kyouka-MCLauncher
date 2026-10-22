using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace Mcroute
{
    public static class Program
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int processId);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool AllocConsole();

        [STAThread]
        /// <summary>启动追踪：设置 MCROUTE_TRACE=1 时写 %TEMP%\mcroute_trace.log。</summary>
        public static void Trace(string msg)
        {
            try
            {
                if (Environment.GetEnvironmentVariable("MCROUTE_TRACE") != "1") return;
                string f = Path.Combine(Path.GetTempPath(), "mcroute_trace.log");
                File.AppendAllText(f, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + "\r\n");
            }
            catch { }
        }

        public static int Main(string[] args)
        {
            Trace("Main 进入, 参数 " + args.Length);
            // 任何未处理异常都要写日志 + 弹窗，避免"双击没反应"
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s2, e2) => ReportCrash(e2.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s2, e2) => ReportCrash(e2.ExceptionObject as Exception);
            // 文档截图模式：mcroute.exe --shot <目录>
            if (args.Length >= 2 && args[0] == "--shot")
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                var form = new MainForm();
                form.Shown += (s2, e2) => form.CaptureTabs(args[1]);
                Application.Run(form);
                return 0;
            }

            // 双击（无参数）-> GUI；带参数 -> 命令行（WinExe 默认没有控制台，挂到父控制台）
            if (args.Length == 0)
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
                return 0;
            }

            // WinExe 默认没有控制台：
            //  · 从命令行/管道调用 -> 把 stdout 接到真实句柄
            //  · 从控制台调用     -> AttachConsole 挂上去
            try
            {
                var stdout = Console.OpenStandardOutput();
                if (stdout != null && stdout.CanWrite)
                    Console.SetOut(new System.IO.StreamWriter(stdout, new UTF8Encoding(false)) { AutoFlush = true });
            }
            catch { }
            // --parallel：打开分段并行下载（默认关闭，慢链路/单连接被限速时可能有帮助）
            if (Array.IndexOf(args, "--parallel") >= 0) Http.AllowParallel = true;
            if (Array.IndexOf(args, "--no-parallel") >= 0) Http.AllowParallel = false;

            // --no-mirror：不碰任何大学镜像，只走官方源（镜像失效演练）
            if (Array.IndexOf(args, "--no-mirror") >= 0)
                Environment.SetEnvironmentVariable("MCROUTE_NO_MIRROR", "1");

            if (!AttachConsole(-1)) { try { AllocConsole(); } catch { } }
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }

            string cmd = args[0].ToLowerInvariant();
            try
            {
                if (cmd == "--selftest-security") return SelfTest.Security();
                if (cmd == "--selftest-download")
                    return SelfTest.DownloadTest(args.Length > 1 ? args[1] : null,
                                                 args.Length > 2 ? args[2] : null);

                if (cmd == "--selftest-parallel")
                    return SelfTest.ParallelTest(args.Length > 1 ? args[1] : null,
                                                 args.Length > 2 ? args[2] : null);

                if (cmd == "--selftest-shell")
                    return SelfTest.ShellTest(args.Length > 1 ? args[1] : null);

                if (cmd == "--selftest-config")
                    return SelfTest.ConfigDump(args.Length > 1 ? args[1] : null);

                if (cmd == "--selftest")
                {
                    Console.WriteLine("构造窗口 ...");
                    try
                    {
                        var f = new MainForm();
                        f.CreateControl();
                        Console.WriteLine("OK: 窗口构造成功");
                        f.Dispose();
                        return 0;
                    }
                    catch (Exception se)
                    {
                        Console.WriteLine("FAIL: " + se);
                        return 9;
                    }
                }

                switch (cmd)
                {
                    case "manifest": return Commands.Manifest(args);
                    case "fetch": return Commands.Fetch(args);
                    case "build": return Commands.Build(args);
                    case "launch": return Commands.Launch(args);
                    case "loader": return Loader.Run(args);
                    case "verify": return ExtraCommands.Verify(args);
                    case "test": return ExtraCommands.Test(args);
                    case "mkbat": return ExtraCommands.Mkbat(args);
                    case "install": return Packs.Install(args);
                    case "java": return JavaCommand.Run(args);
                    case "--selftest-authlib":
                    {
                        string url = args.Length > 1 ? args[1] : "https://littleskin.cn/api/yggdrasil/";
                        string dir = Version.BaseDir(ArgOf(args, "--dir"));
                        Console.WriteLine("  服务器: " + Authlib.NormalizeRoot(url));
                        Console.WriteLine("  服务器名: " + (Authlib.ServerName(url) ?? "(读不到)"));
                        string err;
                        string jar = Authlib.EnsureInjector(dir, out err);
                        if (jar == null) { Console.WriteLine("[FAIL] " + err); return 1; }
                        Console.WriteLine("  injector: " + jar + "  " + (new System.IO.FileInfo(jar).Length / 1024) + " KB");
                        Console.WriteLine("[PASS] 外置登录准备就绪");
                        return 0;
                    }

                    case "--selftest-mcmod":
                    {
                        string q = args.Length > 1 ? args[1] : "sodium";
                        string err;
                        var hits = Mcmod.Search(q, 5, out err);
                        Console.WriteLine("  搜索: " + q);
                        if (!string.IsNullOrEmpty(err)) Console.WriteLine("  提示: " + err);
                        Console.WriteLine("  结果数: " + hits.Count);
                        for (int i = 0; i < hits.Count && i < 3; i++)
                        {
                            var h = hits[i];
                            Console.WriteLine("   · " + h.Name + "  [" + h.Snapshot + "]");
                            Console.WriteLine("     " + h.Url);
                            Console.WriteLine("     " + h.Summary);
                        }
                        if (hits.Count == 0) { Console.WriteLine("[FAIL] 没解析到结果"); return 1; }
                        Console.WriteLine("[PASS] mcmod 搜索可用（" + hits.Count + " 条）");
                        return 0;
                    }

                    case "--selftest-orphan":
                    {
                        string dir = Version.BaseDir(ArgOf(args, "--dir"));
                        var pids = OrphanCleaner.FindLeftovers(dir);
                        Console.WriteLine("  数据目录: " + dir);
                        Console.WriteLine("  找到残留进程: " + (pids.Count == 0 ? "(无)" : string.Join(", ", pids)));
                        if (Array.IndexOf(args, "--kill") >= 0 && pids.Count > 0)
                        {
                            int n = OrphanCleaner.Kill(pids);
                            Console.WriteLine("  已结束 " + n + " 个");
                        }
                        Console.WriteLine("[PASS] 残留检测可用（当前 " + pids.Count + " 个）");
                        return 0;
                    }

                    case "--selftest-proctree":
                    {
                        string self = System.Reflection.Assembly.GetEntryAssembly().Location;
                        Console.WriteLine("  用 cmd.exe 当父进程，里面起一个自己的界面实例当子进程…");
                        var cmdProc = System.Diagnostics.Process.Start("cmd.exe", "/c \"" + self + "\"");
                        if (cmdProc == null) { Console.WriteLine("[FAIL] 起不来 cmd"); return 1; }
                        System.Threading.Thread.Sleep(3000);

                        var desc = ProcessTree.Descendants(cmdProc.Id);
                        Console.WriteLine("  父 PID " + cmdProc.Id + " 的子孙: " + (desc.Count == 0 ? "(无)" : string.Join(", ", desc)));
                        if (desc.Count == 0) { try { cmdProc.Kill(); } catch { } Console.WriteLine("[FAIL] 没找到子进程"); return 1; }

                        int sent = ProcessTree.CloseWindowsInTree(cmdProc.Id);
                        Console.WriteLine("  进程树里共发送 WM_CLOSE 到 " + sent + " 个窗口");
                        System.Threading.Thread.Sleep(2500);

                        var alive = ProcessTree.AliveInTree(cmdProc.Id);
                        if (alive.Count > 0)
                        {
                            Console.WriteLine("  仍有存活: " + string.Join(", ", alive) + " → 强杀整棵树");
                            ProcessTree.KillTree(cmdProc.Id);
                            System.Threading.Thread.Sleep(1200);
                            alive = ProcessTree.AliveInTree(cmdProc.Id);
                        }

                        if (alive.Count == 0 && sent > 0) { Console.WriteLine("[PASS] 进程树关闭成功（父+子全部退出）"); return 0; }
                        Console.WriteLine("[FAIL] 残留 " + alive.Count + " 个进程（sent=" + sent + "）");
                        return 1;
                    }

                    case "--selftest-close":
                    {
                        // 用自己（WinForms 界面）当目标：记事本在 Win11 是商店应用，PID 会换
                        string self = System.Reflection.Assembly.GetEntryAssembly().Location;
                        Console.WriteLine("  启动第二个 " + System.IO.Path.GetFileName(self) + " 作为测试目标…");
                        var proc = System.Diagnostics.Process.Start(self);
                        if (proc == null) { Console.WriteLine("[FAIL] 起不来记事本"); return 1; }
                        System.Threading.Thread.Sleep(2500);
                        Console.WriteLine("  目标 PID " + proc.Id + "  可见窗口=" + WindowCloser.HasVisibleWindow(proc.Id));
                        int sent = WindowCloser.CloseAll(proc.Id);
                        Console.WriteLine("  已发送 WM_CLOSE 到 " + sent + " 个窗口");
                        bool exited = proc.WaitForExit(5000);
                        if (!exited) { try { proc.Kill(); } catch { } }
                        if (exited && sent > 0) { Console.WriteLine("[PASS] 优雅关闭成功（进程已退出）"); return 0; }
                        Console.WriteLine("[FAIL] 记事本没退出（sent=" + sent + "）");
                        return 1;
                    }

                    case "--selftest-instances":
                    {
                        string tmp = ArgOf(args, "--dir");
                        if (string.IsNullOrEmpty(tmp)) tmp = System.IO.Path.Combine(Environment.CurrentDirectory, "_insttest");
                        try { if (System.IO.Directory.Exists(tmp)) System.IO.Directory.Delete(tmp, true); } catch { }
                        // 底板版本
                        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(tmp, "versions", "1.20.1"));
                        System.IO.File.WriteAllText(System.IO.Path.Combine(tmp, "versions", "1.20.1", "1.20.1.json"),
                            "{\"id\":\"1.20.1\",\"type\":\"release\"}");
                        Console.WriteLine("  版本候选: " + string.Join(", ", Instances.VersionCandidates(tmp)));

                        string err;
                        if (!Instances.Create(tmp, "生存", "1.20.1", out err)) { Console.WriteLine("[FAIL] 创建: " + err); return 1; }
                        if (!Instances.Create(tmp, "创造", "1.20.1", out err)) { Console.WriteLine("[FAIL] 创建2: " + err); return 1; }

                        // 关键：必须是 versions/<名字>/ 且带 <名字>.json（主流启动器可识别）
                        string vdir = System.IO.Path.Combine(tmp, "versions", "生存");
                        if (!System.IO.Directory.Exists(vdir)) { Console.WriteLine("[FAIL] 不在 versions/ 下"); return 1; }
                        if (!System.IO.File.Exists(System.IO.Path.Combine(vdir, "生存.json"))) { Console.WriteLine("[FAIL] 缺 生存.json"); return 1; }
                        string vjson = System.IO.File.ReadAllText(System.IO.Path.Combine(vdir, "生存.json"));
                        if (!vjson.Contains("\"inheritsFrom\":\"1.20.1\"")) { Console.WriteLine("[FAIL] inheritsFrom 不对: " + vjson); return 1; }
                        Console.WriteLine("  版本 json: " + vjson);

                        var list = Instances.List(tmp);
                        if (list.Count != 2) { Console.WriteLine("[FAIL] 列表数量 " + list.Count); return 1; }
                        Console.WriteLine("  列表: " + string.Join(", ", list.ConvertAll(i => i.Name + "@" + i.Version)));

                        if (!Instances.Copy(tmp, "生存", "生存-备份", out err)) { Console.WriteLine("[FAIL] 复制: " + err); return 1; }
                        if (!System.IO.File.Exists(System.IO.Path.Combine(tmp, "versions", "生存-备份", "生存-备份.json")))
                        { Console.WriteLine("[FAIL] 复制后缺 json"); return 1; }
                        if (!Instances.Rename(tmp, "生存-备份", "生存-改名", out err)) { Console.WriteLine("[FAIL] 重命名: " + err); return 1; }
                        if (!Instances.Delete(tmp, "生存-改名", out err)) { Console.WriteLine("[FAIL] 删除: " + err); return 1; }

                        var after = Instances.List(tmp);
                        Console.WriteLine("  最终: " + string.Join(", ", after.ConvertAll(i => i.Name)));
                        Console.WriteLine("[PASS] 实例（versions/ 结构，主流启动器可识别）全部通过");
                        try { System.IO.Directory.Delete(tmp, true); } catch { }
                        return 0;
                    }

                    case "--selftest-cleanroom":
                    {
                        string ver = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : null;
                        if (string.IsNullOrEmpty(ver)) { Console.WriteLine("用法: --selftest-cleanroom <版本> [--dir <数据目录>] [--dry]"); return 1; }
                        string dir = Version.BaseDir(ArgOf(args, "--dir"));
                        bool dry = Array.IndexOf(args, "--dry") >= 0;
                        Console.WriteLine("  实例: " + ver + "  dir=" + dir + (dry ? "  (预演)" : ""));
                        Console.WriteLine("  是否 Cleanroom: " + CleanroomSetup.Detect(dir, ver));
                        string report;
                        bool ok = CleanroomSetup.Ensure(dir, ver, dry, out report);
                        Console.WriteLine((ok ? "[PASS] " : "[FAIL] ") + report);
                        return ok ? 0 : 1;
                    }

                    case "--selftest-packs":
                    {
                        string tmp = ArgOf(args, "--dir");
                        if (string.IsNullOrEmpty(tmp)) tmp = System.IO.Path.Combine(Environment.CurrentDirectory, "_packtest");
                        try { if (System.IO.Directory.Exists(tmp)) System.IO.Directory.Delete(tmp, true); } catch { }
                        string rp = System.IO.Path.Combine(tmp, "versions", "T", "resourcepacks");
                        System.IO.Directory.CreateDirectory(rp);
                        // 造一个最小资源包
                        string zipPath = System.IO.Path.Combine(rp, "demo.zip");
                        using (var fs = System.IO.File.Create(zipPath))
                        using (var zip = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create))
                        {
                            var e = zip.CreateEntry("pack.mcmeta");
                            using (var w = new System.IO.StreamWriter(e.Open()))
                                w.Write("{\"pack\":{\"pack_format\":15,\"description\":\"selftest pack\"}}");
                        }
                        var packs = LocalPacks.Scan(tmp, "T", LocalPacks.ResourcePacks);
                        Console.WriteLine("  扫描到 " + packs.Count + " 个资源包");
                        if (packs.Count != 1) { Console.WriteLine("[FAIL] 扫描数量不对"); return 1; }
                        var p0 = packs[0];
                        Console.WriteLine("  名称=" + p0.Title + "  格式=" + p0.PackFormat + "  描述=" + p0.Description);
                        if (p0.PackFormat != 15 || p0.Description != "selftest pack") { Console.WriteLine("[FAIL] 元数据解析不对"); return 1; }

                        string err;
                        if (!LocalPacks.SetEnabled(p0, false, out err)) { Console.WriteLine("[FAIL] 禁用失败: " + err); return 1; }
                        if (!System.IO.File.Exists(zipPath + ".disabled")) { Console.WriteLine("[FAIL] 禁用后文件不存在"); return 1; }
                        var packs2 = LocalPacks.Scan(tmp, "T", LocalPacks.ResourcePacks);
                        if (packs2.Count != 1 || !packs2[0].Disabled) { Console.WriteLine("[FAIL] 禁用状态未生效"); return 1; }

                        if (!LocalPacks.SetEnabled(packs2[0], true, out err)) { Console.WriteLine("[FAIL] 启用失败: " + err); return 1; }
                        if (!System.IO.File.Exists(zipPath)) { Console.WriteLine("[FAIL] 启用后文件不存在"); return 1; }

                        Console.WriteLine("[PASS] 资源包扫描 / 元数据 / 启停 全部通过");
                        try { System.IO.Directory.Delete(tmp, true); } catch { }
                        return 0;
                    }

                    case "--selftest-update":
                    {
                        Console.WriteLine("当前版本: " + Update.CurrentVersion());
                        string tag, url, err;
                        bool ok = Update.CheckLatest(out tag, out url, out err);
                        if (!ok) { Console.WriteLine("[FAIL] 检查失败: " + err); return 1; }
                        Console.WriteLine("GitHub 最新: " + tag);
                        Console.WriteLine("下载地址: " + (url ?? "(无)"));
                        bool newer = Update.IsNewer(tag, Update.CurrentVersion());
                        Console.WriteLine(newer ? "[INFO] 有新版本" : "[PASS] 已是最新");
                        return 0;
                    }

                    case "mods-search":
                    {
                        string q = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : "";
                        string mc = ArgOf(args, "--mc");
                        string ld = ArgOf(args, "--loader");
                        string src = ArgOf(args, "--source") ?? "modrinth";
                        int lim = 10;
                        string ls = ArgOf(args, "--limit");
                        if (!string.IsNullOrEmpty(ls)) int.TryParse(ls, out lim);

                        if (src.Equals("cf", StringComparison.OrdinalIgnoreCase) ||
                            src.Equals("curseforge", StringComparison.OrdinalIgnoreCase))
                        {
                            string dir = Version.BaseDir(ArgOf(args, "--dir"));
                            string err;
                            var hits = CurseForge.Search(dir, q, mc, ld, lim, out err);
                            if (!string.IsNullOrEmpty(err)) { Console.WriteLine("[错误] " + err); return 1; }
                            Console.WriteLine("搜索结果 " + hits.Count + " 条（CurseForge）");
                            int i = 0;
                            foreach (var h in hits)
                            {
                                i++;
                                Console.WriteLine(string.Format("  {0}. {1}  [id={2}]  下载 {3}", i, h.Name, h.ModId, h.DownloadsText()));
                                Console.WriteLine("     " + (h.Summary ?? ""));
                            }
                            return 0;
                        }
                        return Modrinth.CommandSearch(q, mc, ld, lim);
                    }

                    case "mods-install-url":
                    {
                        string u = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : null;
                        if (string.IsNullOrEmpty(u))
                        {
                            Console.WriteLine("用法: mods-install-url <直链> --version <实例> [--dir <数据目录>]");
                            return 1;
                        }
                        string dir = Version.BaseDir(ArgOf(args, "--dir"));
                        string verName = ArgOf(args, "--version");
                        string msg;
                        bool ok = Mods.InstallFromUrl(dir, verName, u, out msg);
                        Console.WriteLine((ok ? "[完成] " : "[失败] ") + msg);
                        return ok ? 0 : 1;
                    }

                    case "mods-install-cf":
                    {
                        string idText = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : null;
                        long modId;
                        if (string.IsNullOrEmpty(idText) || !long.TryParse(idText, out modId))
                        {
                            Console.WriteLine("用法: mods-install-cf <modId> --mc 1.20.1 --loader fabric --version <实例> [--dir <数据目录>]");
                            return 1;
                        }
                        string dir = Version.BaseDir(ArgOf(args, "--dir"));
                        string verName = ArgOf(args, "--version");
                        string mc = ArgOf(args, "--mc");
                        string ld = ArgOf(args, "--loader");
                        string msg;
                        bool ok = CurseForge.Install(dir, verName, modId, mc, ld, out msg);
                        Console.WriteLine((ok ? "[完成] " : "[失败] ") + msg);
                        return ok ? 0 : 1;
                    }

                    case "mods-install":
                    {
                        string proj = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : null;
                        if (string.IsNullOrEmpty(proj)) { Console.WriteLine("用法: mods-install <项目id或slug> --mc <版本> --loader <加载器> [--dir <数据目录>] [--version <实例名>]"); return 1; }
                        string dir = Version.BaseDir(ArgOf(args, "--dir"));
                        string verName = ArgOf(args, "--version");
                        string mc = ArgOf(args, "--mc");
                        string ld = ArgOf(args, "--loader");
                        string msg;
                        bool ok = Modrinth.Install(dir, verName, proj, mc, ld, out msg);
                        Console.WriteLine((ok ? "[完成] " : "[失败] ") + msg);
                        return ok ? 0 : 1;
                    }

                    case "mods":
                    {
                        int di = Array.IndexOf(args, "--dir");
                        string sDir = (di >= 0 && di + 1 < args.Length) ? args[di + 1] : null;
                        return Mods.Command(Version.BaseDir(sDir), args.Length > 1 ? args[1] : null);
                    }

                    case "sources":
                    {
                        int di = Array.IndexOf(args, "--dir");
                        string sDir = (di >= 0 && di + 1 < args.Length) ? args[di + 1] : null;
                        bool probe = Array.IndexOf(args, "--no-probe") < 0;
                        return Mirrors.Command(Version.BaseDir(sDir), probe);
                    }
                    case "help":
                    case "--help":
                    case "-h": Guide(); return 0;
                    default:
                        Console.WriteLine("[错误] 未知命令: " + cmd);
                        Guide();
                        return 1;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[异常] " + ex.Message);
                return 3;
            }
        }

        /// <summary>写崩溃日志（多路径兜底），并尽量弹个窗。</summary>
        private static void ReportCrash(Exception ex)
        {
            string message = ex == null ? "未知错误（null）" : ex.ToString();

            // 拖拽注册这类异常不影响使用，只记日志，别打扰用户
            if (ex is InvalidOperationException && ex.Message != null &&
                (ex.Message.Contains("DragDrop") || ex.Message.Contains("拖放")))
            {
                try
                {
                    File.AppendAllText(Path.Combine(Path.GetTempPath(), "mcroute-crash.log"),
                        "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] 无害(拖拽): " + ex.Message + "\r\n");
                }
                catch { }
                return;
            }
            var candidates = new List<string>
            {
                Path.Combine(Path.GetTempPath(), "mcroute-crash.log"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "mcroute", "crash.log"),
                Path.Combine(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location), "crash.log"),
            };
            string logPath = null;
            foreach (string f in candidates)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(f));
                    File.AppendAllText(f, "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "]\r\n" + message + "\r\n\r\n");
                    logPath = f;
                    break;
                }
                catch { }
            }

            try
            {
                MessageBox.Show(
                    "mcroute 启动时出错了：\n\n" +
                    (ex == null ? "未知错误" : ex.GetType().Name + ": " + ex.Message) + "\n\n" +
                    (logPath == null ? "（连日志都写不了，请把这个窗口截屏）" : "详细信息：\n" + logPath),
                    "mcroute 启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
        }

        private static string ArgOf(string[] a, string name)
        {
            int i = Array.IndexOf(a, name);
            return (i >= 0 && i + 1 < a.Length) ? a[i + 1] : null;
        }

        private static void Guide()
        {
            Console.WriteLine("mcroute " + "(C# 版) —— Minecraft Java 启动器");
            Console.WriteLine();
            Console.WriteLine("用法：");
            Console.WriteLine("  mcroute manifest [版本]             查看版本清单");
            Console.WriteLine("  mcroute fetch <版本> [--no-assets]  下载完整闭包");
            Console.WriteLine("  mcroute build <版本>                只拼参数、检查缺什么");
            Console.WriteLine("  mcroute launch <版本> [--wait]      启动游戏");
            Console.WriteLine("  mcroute loader <fabric|quilt|legacyfabric|forge|neoforge> <版本>  安装加载器");
            Console.WriteLine("  mcroute verify <版本> [--fix]       体检 / 补全");
            Console.WriteLine("  mcroute test <版本> [--timeout 90]  测试启动");
            Console.WriteLine("  mcroute mkbat <版本>                生成双击启动器 launch.bat");
            Console.WriteLine("  mcroute install <整合包>            装 mrpack / MultiMC / CurseForge 包");
            Console.WriteLine("  mcroute java list|install <版本>    查看 / 自动下载 Java（Adoptium→Azul→Corretto）");
            Console.WriteLine("  mcroute sources [--no-probe]        列出镜像源并测速（配置文件 <数据目录>/mirrors.json）");
            Console.WriteLine("  mcroute mods <版本>                 列出该版本本地 mods（名称/版本/加载器/启用状态）");
            Console.WriteLine("  mcroute mods-search <关键词> [--mc 1.20.1] [--loader fabric]   搜索 Modrinth");
            Console.WriteLine("  mcroute mods-install <项目id> --mc 1.20.1 --loader fabric --version <实例>");
            Console.WriteLine();
            Console.WriteLine("公共参数：--dir <数据目录>  --java <java.exe>  --game-dir <游戏目录>");
            Console.WriteLine("演练参数：--no-mirror  只走官方源（模拟镜像失效）");
            Console.WriteLine("自测参数：--selftest-security  --selftest-download <url> [sha1]");
            Console.WriteLine();
            Console.WriteLine("示例：");
            Console.WriteLine("  mcroute fetch 1.21.1");
            Console.WriteLine("  mcroute launch 1.21.1");
        }
    }
}
