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

            // Mod 下载窗口截图：--shot-modbrowser <版本> <MC版本> <加载器> <关键词> <输出.png> [宽 高]
            if (args.Length >= 6 && args[0] == "--shot-modbrowser")
            {
                ShotMode = true;
                try
                {
                    try { Mcroute.Theme.Apply(Environment.GetEnvironmentVariable("MCROUTE_THEME") ?? "auto"); } catch { }
                    string baseDir = Environment.GetEnvironmentVariable("MCROUTE_DATA");
                    if (string.IsNullOrEmpty(baseDir)) baseDir = Mcroute.Version.BaseDir(null);
                    int w = args.Length >= 7 ? int.Parse(args[6]) : 900;
                    int h = args.Length >= 8 ? int.Parse(args[7]) : 640;

                    var mw = new ModsBrowserWindow(baseDir, args[1], args[2], args[3])
                    {
                        WindowStartupLocation = WindowStartupLocation.Manual,
                        Left = -10000, Top = -10000, Width = w, Height = h, ShowInTaskbar = false,
                    };
                    mw.Show();
                    mw.UpdateLayout();
                    Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);

                    // 填关键词并触发搜索，等结果回来
                    mw.PreloadSearch(args[4]);
                    PumpFor(TimeSpan.FromSeconds(4));

                    var visual = (Visual)mw.Content;
                    var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                    rtb.Render(visual);
                    string outDir = Path.GetDirectoryName(Path.GetFullPath(args[5]));
                    if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(rtb));
                    using (var fs = File.Create(args[5])) enc.Save(fs);
                    ShotTrace("shot-modbrowser saved");
                    mw.Close();
                }
                catch (Exception ex) { ShotTrace("shot-modbrowser 异常: " + ex); }
                Shutdown(0);
                return;
            }

            // 外置登录窗口截图：--shot-authlib <输出.png> [宽 高]
            if (args.Length >= 2 && args[0] == "--shot-authlib")
            {
                ShotMode = true;
                try
                {
                    try { Mcroute.Theme.Apply(Environment.GetEnvironmentVariable("MCROUTE_THEME") ?? "auto"); } catch { }
                    string baseDir = Environment.GetEnvironmentVariable("MCROUTE_DATA");
                    if (string.IsNullOrEmpty(baseDir)) baseDir = Mcroute.Version.BaseDir(null);
                    int w = args.Length >= 3 ? int.Parse(args[2]) : 520;
                    int h = args.Length >= 4 ? int.Parse(args[3]) : 470;

                    var authWin = new AuthlibWindow(baseDir)
                    {
                        WindowStartupLocation = WindowStartupLocation.Manual,
                        Left = -10000, Top = -10000, Width = w, Height = h, ShowInTaskbar = false,
                    };
                    authWin.Show();
                    authWin.UpdateLayout();
                    Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
                    PumpFor(TimeSpan.FromMilliseconds(600));

                    var visual = (Visual)authWin.Content;
                    var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                    rtb.Render(visual);
                    string dir = Path.GetDirectoryName(Path.GetFullPath(args[1]));
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(rtb));
                    using (var fs = File.Create(args[1])) enc.Save(fs);
                    ShotTrace("shot-authlib saved");
                    authWin.Close();
                }
                catch (Exception ex) { ShotTrace("shot-authlib 异常: " + ex); }
                Shutdown(0);
                return;
            }

            // Mod 管理窗口截图：--shot-modupdate <数据目录> <版本> <mc版本> <loader> <输出.png> [宽 高]
            if (args.Length >= 6 && args[0] == "--shot-modupdate")
            {
                ShotMode = true;
                try
                {
                    try { Mcroute.Theme.Apply(Environment.GetEnvironmentVariable("MCROUTE_THEME") ?? "auto"); } catch { }
                    int w = args.Length >= 7 ? int.Parse(args[6]) : 1000;
                    int h = args.Length >= 8 ? int.Parse(args[7]) : 640;

                    var uw = new ModsWindow(args[1], args[2], args[3], args[4])
                    {
                        WindowStartupLocation = WindowStartupLocation.Manual,
                        Left = -10000, Top = -10000, Width = w, Height = h, ShowInTaskbar = false,
                    };
                    uw.Show();
                    uw.UpdateLayout();
                    Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
                    PumpFor(TimeSpan.FromSeconds(10));

                    var visual = (Visual)uw.Content;
                    var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                    rtb.Render(visual);
                    string dir = Path.GetDirectoryName(Path.GetFullPath(args[5]));
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(rtb));
                    using (var fs = File.Create(args[5])) enc.Save(fs);
                    ShotTrace("shot-modupdate saved");
                    uw.Close();
                }
                catch (Exception ex) { ShotTrace("shot-modupdate 异常: " + ex); }
                Shutdown(0);
                return;
            }

            // MC百科窗口截图：--shot-mcmod <输出.png> [宽 高] [关键词]
            if (args.Length >= 2 && args[0] == "--shot-mcmod")
            {
                ShotMode = true;
                try
                {
                    try { Mcroute.Theme.Apply(Environment.GetEnvironmentVariable("MCROUTE_THEME") ?? "auto"); } catch { }
                    int w = args.Length >= 3 ? int.Parse(args[2]) : 900;
                    int h = args.Length >= 4 ? int.Parse(args[3]) : 620;
                    string q = args.Length >= 5 ? args[4] : "sodium";

                    var mw = new McmodWindow()
                    {
                        WindowStartupLocation = WindowStartupLocation.Manual,
                        Left = -10000, Top = -10000, Width = w, Height = h, ShowInTaskbar = false,
                    };
                    mw.Show();
                    mw.UpdateLayout();
                    Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
                    mw.SearchNow(q);
                    PumpFor(TimeSpan.FromSeconds(3));

                    var visual = (Visual)mw.Content;
                    var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                    rtb.Render(visual);
                    string dir = Path.GetDirectoryName(Path.GetFullPath(args[1]));
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(rtb));
                    using (var fs = File.Create(args[1])) enc.Save(fs);
                    ShotTrace("shot-mcmod saved");
                    mw.Close();
                }
                catch (Exception ex) { ShotTrace("shot-mcmod 异常: " + ex); }
                Shutdown(0);
                return;
            }

            // 整合包下载窗口截图：--shot-modpack <输出.png> [宽 高] [关键词]
            if (args.Length >= 2 && args[0] == "--shot-modpack")
            {
                ShotMode = true;
                try
                {
                    try { Mcroute.Theme.Apply(Environment.GetEnvironmentVariable("MCROUTE_THEME") ?? "auto"); } catch { }
                    string baseDir = Environment.GetEnvironmentVariable("MCROUTE_DATA");
                    if (string.IsNullOrEmpty(baseDir)) baseDir = Mcroute.Version.BaseDir(null);
                    int w = args.Length >= 3 ? int.Parse(args[2]) : 900;
                    int h = args.Length >= 4 ? int.Parse(args[3]) : 640;
                    string q = args.Length >= 5 ? args[4] : "sky";

                    var mpWin = new ModpackWindow(baseDir)
                    {
                        WindowStartupLocation = WindowStartupLocation.Manual,
                        Left = -10000, Top = -10000, Width = w, Height = h, ShowInTaskbar = false,
                    };
                    mpWin.Show();
                    mpWin.UpdateLayout();
                    Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
                    mpWin.PreloadSearch(q);
                    PumpFor(TimeSpan.FromSeconds(3));

                    var visual = (Visual)mpWin.Content;
                    var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                    rtb.Render(visual);
                    string dir = Path.GetDirectoryName(Path.GetFullPath(args[1]));
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(rtb));
                    using (var fs = File.Create(args[1])) enc.Save(fs);
                    ShotTrace("shot-modpack saved");
                    mpWin.Close();
                }
                catch (Exception ex) { ShotTrace("shot-modpack 异常: " + ex); }
                Shutdown(0);
                return;
            }

            // 实例窗口截图：--shot-instances <输出.png> [宽 高]
            if (args.Length >= 2 && args[0] == "--shot-instances")
            {
                ShotMode = true;
                try
                {
                    try { Mcroute.Theme.Apply(Environment.GetEnvironmentVariable("MCROUTE_THEME") ?? "auto"); } catch { }
                    string baseDir = Environment.GetEnvironmentVariable("MCROUTE_DATA");
                    if (string.IsNullOrEmpty(baseDir)) baseDir = Mcroute.Version.BaseDir(null);
                    int w = args.Length >= 3 ? int.Parse(args[2]) : 860;
                    int h = args.Length >= 4 ? int.Parse(args[3]) : 620;

                    var instWin = new InstancesWindow(baseDir)
                    {
                        WindowStartupLocation = WindowStartupLocation.Manual,
                        Left = -10000, Top = -10000, Width = w, Height = h, ShowInTaskbar = false,
                    };
                    instWin.Show();
                    instWin.UpdateLayout();
                    Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
                    PumpFor(TimeSpan.FromMilliseconds(700));

                    var visual = (Visual)instWin.Content;
                    var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                    rtb.Render(visual);
                    string dir = Path.GetDirectoryName(Path.GetFullPath(args[1]));
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(rtb));
                    using (var fs = File.Create(args[1])) enc.Save(fs);
                    ShotTrace("shot-instances saved");
                    instWin.Close();
                }
                catch (Exception ex) { ShotTrace("shot-instances 异常: " + ex); }
                Shutdown(0);
                return;
            }

            // 资源包/光影窗口截图：--shot-packs <版本> <输出.png> [宽 高] [shaders]
            if (args.Length >= 3 && args[0] == "--shot-packs")
            {
                ShotMode = true;
                try
                {
                    try { Mcroute.Theme.Apply(Environment.GetEnvironmentVariable("MCROUTE_THEME") ?? "auto"); } catch { }
                    string baseDir = Environment.GetEnvironmentVariable("MCROUTE_DATA");
                    if (string.IsNullOrEmpty(baseDir)) baseDir = Mcroute.Version.BaseDir(null);
                    int w = args.Length >= 4 ? int.Parse(args[3]) : 880;
                    int h = args.Length >= 5 ? int.Parse(args[4]) : 620;
                    bool shaders = args.Length >= 6 && args[5] == "shaders";

                    var packWin = new PacksWindow(baseDir, args[1])
                    {
                        WindowStartupLocation = WindowStartupLocation.Manual,
                        Left = -10000, Top = -10000, Width = w, Height = h, ShowInTaskbar = false,
                    };
                    packWin.Show();
                    packWin.UpdateLayout();
                    Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
                    packWin.PreloadTab(shaders);
                    PumpFor(TimeSpan.FromMilliseconds(700));

                    var visual = (Visual)packWin.Content;
                    var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                    rtb.Render(visual);
                    string dir = Path.GetDirectoryName(Path.GetFullPath(args[2]));
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(rtb));
                    using (var fs = File.Create(args[2])) enc.Save(fs);
                    ShotTrace("shot-packs saved");
                    packWin.Close();
                }
                catch (Exception ex) { ShotTrace("shot-packs 异常: " + ex); }
                Shutdown(0);
                return;
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
                    try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "KyoukaMCLauncher-crash.log"), ex.ToString()); } catch { }
                }
                Shutdown(0);
                return;
            }

            GlobalHandlers();

            var win = new MainWindow();
            MainWindow = win;
            win.Show();
            try { win.SilentUpdateCheck(); } catch { }
        }

        private static void ShotTrace(string msg)
        {
            try
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "KyoukaMCLauncher-shot.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + "\r\n");
            }
            catch { }
        }

        /// <summary>截图用：把设置页滚到底，露出「关于与更新」。</summary>
        private static void ScrollSettingsToEnd(MainWindow win)
        {
            try
            {
                var page = win.FindName("PageSettings") as DependencyObject;
                if (page == null) return;
                var sv = FindChild<System.Windows.Controls.ScrollViewer>(page);
                if (sv != null) sv.ScrollToEnd();
            }
            catch { }
        }

        private static T FindChild<T>(DependencyObject parent) where T : DependencyObject
        {
            int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < n; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                var hit = child as T;
                if (hit != null) return hit;
                var deeper = FindChild<T>(child);
                if (deeper != null) return deeper;
            }
            return null;
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
            if (page == 4) { ScrollSettingsToEnd(win); PumpFor(TimeSpan.FromMilliseconds(500)); }
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

        /// <summary>让 WPF 消息循环继续跑一段时间（等异步结果回来再截图）。</summary>
        private static void PumpFor(TimeSpan duration)
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            var timer = new System.Windows.Threading.DispatcherTimer(
                System.Windows.Threading.DispatcherPriority.Background)
            {
                Interval = duration,
            };
            timer.Tick += (s2, e2) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            System.Windows.Threading.Dispatcher.PushFrame(frame);
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
                    "Kyouka MCLauncher", MessageBoxButton.OK, MessageBoxImage.Error);
                ex.Handled = true;
            };
        }

        private static string CrashLogPath()
        {
            return Path.Combine(Path.GetTempPath(), "KyoukaMCLauncher-crash.log");
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
