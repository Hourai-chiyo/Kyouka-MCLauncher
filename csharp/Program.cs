using System;
using System.Text;

namespace Mcroute
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;

            if (args.Length == 0)
            {
                Guide();
                return 0;
            }

            string cmd = args[0].ToLowerInvariant();
            try
            {
                switch (cmd)
                {
                    case "manifest": return Commands.Manifest(args);
                    case "fetch": return Commands.Fetch(args);
                    case "build": return Commands.Build(args);
                    case "launch": return Commands.Launch(args);
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
            Console.WriteLine();
            Console.WriteLine("公共参数：--dir <数据目录>  --java <java.exe>  --game-dir <游戏目录>");
            Console.WriteLine();
            Console.WriteLine("示例：");
            Console.WriteLine("  mcroute fetch 1.21.1");
            Console.WriteLine("  mcroute launch 1.21.1");
        }
    }
}
