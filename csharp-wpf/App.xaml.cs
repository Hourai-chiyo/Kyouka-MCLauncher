using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Mcroute.Wpf
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 文档截图模式：mcroute-wpf.exe --shot <输出.png> [宽 高]
            // 不依赖窗口显示，用 RenderTargetBitmap 直接渲染（也方便 CI 出图）
            if (e.Args.Length >= 2 && e.Args[0] == "--shot")
            {
                try { Shot(e.Args[1], e.Args.Length >= 3 ? int.Parse(e.Args[2]) : 1180,
                                           e.Args.Length >= 4 ? int.Parse(e.Args[3]) : 760); }
                catch (Exception ex)
                {
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

        private void Shot(string outputPath, int width, int height)
        {
            var win = new MainWindow
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000,      // 移到屏幕外，避免闪一下
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
