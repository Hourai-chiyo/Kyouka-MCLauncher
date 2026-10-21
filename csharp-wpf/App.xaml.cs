using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Mcroute.Wpf
{
    public partial class App : Application
    {
        /// <summary>截图模式：不要弹任何对话框（否则 CI/文档出图会卡住）。</summary>
        public static bool ShotMode;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 注意：生成的 Main() 调用的是 app.Run()，此时 e.Args 可能为空，
            // 所以直接读命令行（Environment.GetCommandLineArgs）。
            string[] args = Environment.GetCommandLineArgs();
            if (args.Length > 1) args = args.Skip(1).ToArray();
            else args = new string[0];

            // 文档截图模式：mcroute-wpf.exe --shot <输出.png> [宽 高 页码]
            // 不依赖窗口显示，用 RenderTargetBitmap 直接渲染（也方便 CI 出图）
            if (args.Length >= 2 && args[0] == "--shot")
            {
                ShotMode = true;
            }

            // 文件浏览窗口截图：--shot-browser <目录> <输出.png> [宽 高]
            if (args.Length >= 3 && args[0] == "--shot-browser")
            {
                ShotMode = true;
                try
                {
                    try { Mcroute.Theme.Apply(Environment.GetEnvironmentVariable("MCROUTE_THEME") ?? "auto"); } catch { }   // shot-browser 主题
                    int w = args.Length >= 4 ? int.Parse(args[3]) : 820;
                    int h = args.Length >= 5 ? int.Parse(args[4]) : 580;
                    var browserWin = new FileBrowserWindow(args[1])
                    {
                        WindowStartupLocation = WindowStartupLocation.Manual,
                        Left = -10000, Top = -10000, Width = w, Height = h, ShowInTaskbar = false,
                    };
                    browserWin.Show();
                    browserWin.UpdateLayout();
                    Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
                    var visual = (Visual)browserWin.Content;
                    var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                    rtb.Render(visual);
                    string outDir = Path.GetDirectoryName(Path.GetFullPath(args[2]));
                    if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(rtb));
                    using (var fs = File.Create(args[2])) enc.Save(fs);
                    browserWin.Close();
                }
                catch (Exception ex) { ShotTrace("shot-browser 异常: " + ex); }
                Shutdown(0);
                return;
            }

            // Mods 窗口截图：--shot-mods <版本> <输出.png> [宽 高]
            if (args.Length >= 3 && args[0] == "--shot-mods")
            {
                ShotMode = true;
                try
                {
                    string baseDir = Environment.GetEnvironmentVariable("MCROUTE_DATA");
                    if (string.IsNullOrEmpty(baseDir)) baseDir = Mcroute.Version.BaseDir(null);
                    int w = args.Length >= 4 ? int.Parse(args[3]) : 820;
                    int h = args.Length >= 5 ? int.Parse(args[4]) : 560;
                    ShotMods(baseDir, args[1], args[2], w, h);
                }
                catch (Exception ex) { ShotTrace("shot-mods 异常: " + ex); }
                Shutdown(0);
                return;
            }

            if (args.Length >= 2 && args[0] == "--shot")
            {
                try
                {
                    int w = args.Length >= 3 ? int.Parse(args[2]) : 1180;
                    int h = args.Length >= 4 ? int.Parse(args[3]) : 760;
                    int page = args.Length >= 5 ? int.Parse(args[4]) : 0;
                    Shot(args[1], w, h, page);
                }
                catch (Exception ex)
                {
                    ShotTrace("异常: " + ex);
                    try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "mcroute-wpf-crash.log"), ex.ToString()); } catch { }
                }
                Shutdown(0);
                return;
            }

            GlobalHandlers();

            var win = new MainWindow();
            MainWindow = win;
            win.Show();
        }

        private static void ShotTrace(string msg)
        {
            try
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "mcroute-shot.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + "\r\n");
            }
            catch { }
        }

        private void Shot(string outputPath, int width, int height, int page = 0)
        {
            ShotTrace("Shot 开始 output=" + outputPath + " " + width + "x" + height + " page=" + page);
            var win = new MainWindow
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000,      // 移到屏幕外，避免闪一下
                Top = -10000,
                Width = width,
                Height = height,
                ShowInTaskbar = false,
            };
            ShotTrace("窗口创建成功");
            win.Show();
            ShotTrace("窗口已 Show");
            win.SetPage(page);
            win.UpdateLayout();
            Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);

            ShotTrace("布局完成");
            var visual = (Visual)win.Content;
            ShotTrace("visual=" + (visual == null ? "null" : visual.GetType().Name));
            var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);

            string dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            ShotTrace("渲染完成，开始写文件");
            using (var fs = File.Create(outputPath)) encoder.Save(fs);
            ShotTrace("已保存");

            win.Close();
        }

        private void ShotMods(string baseDir, string version, string outputPath, int width, int height)
        {
            // 截图模式下主窗口没跑，主题不会自动应用，这里补一次
            try { Mcroute.Theme.Apply(Environment.GetEnvironmentVariable("MCROUTE_THEME") ?? "auto"); } catch { }

            ShotTrace("ShotMods " + version + " -> " + outputPath + " " + width + "x" + height);
            var win = new ModsWindow(baseDir, version)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000,
                Top = -10000,
                Width = width,
                Height = height,
                ShowInTaskbar = false,
            };
            win.Show();
            win.UpdateLayout();
            Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);

            var visual = (Visual)win.Content;
            var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);

            string dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = File.Create(outputPath)) encoder.Save(fs);
            ShotTrace("ShotMods 已保存");

            win.Close();
        }

        private void GlobalHandlers()
        {
            DispatcherUnhandledException += (s, ex) =>
            {
                LogCrash(ex.Exception);
                MessageBox.Show("出错了：" + ex.Exception.Message + "\n\n详情：" + CrashLogPath(),
                    "mcroute WPF 原型", MessageBoxButton.OK, MessageBoxImage.Error);
                ex.Handled = true;
            };
        }

        private static string CrashLogPath()
        {
            return Path.Combine(Path.GetTempPath(), "mcroute-wpf-crash.log");
        }

        private static void LogCrash(Exception ex)
        {
            try
            {
                File.AppendAllText(CrashLogPath(),
                    "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "]\r\n" + ex + "\r\n\r\n");
            }
            catch { }
        }
    }
}
