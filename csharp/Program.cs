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
            if (!AttachConsole(-1)) { try { AllocConsole(); } catch { } }
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }

            string cmd = args[0].ToLowerInvariant();
            try
            {
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
            Console.WriteLine();
            Console.WriteLine("公共参数：--dir <数据目录>  --java <java.exe>  --game-dir <游戏目录>");
            Console.WriteLine();
            Console.WriteLine("示例：");
            Console.WriteLine("  mcroute fetch 1.21.1");
            Console.WriteLine("  mcroute launch 1.21.1");
        }
    }
}
