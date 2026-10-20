using System;
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
        public static int Main(string[] args)
        {
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
