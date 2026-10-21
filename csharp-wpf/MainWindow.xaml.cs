using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Mcroute.Wpf
{
    public partial class MainWindow : Window
    {
        private readonly string[] _navNames = { "启动", "下载", "整合包", "设置" };

        public MainWindow()
        {
            InitializeComponent();
            VersionList.SelectedIndex = 0;
        }

        // ---------- 窗口按钮 ----------
        private void Min_Click(object sender, RoutedEventArgs e) { WindowState = WindowState.Minimized; }

        private void Max_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        private void Close_Click(object sender, RoutedEventArgs e) { Close(); }

        // ---------- 导航 ----------
        private void Nav_Checked(object sender, RoutedEventArgs e)
        {
            if (PageHint == null) return;   // 初始化阶段
            var rb = sender as RadioButton;
            if (rb == null) return;
            string name = rb.Content as string ?? "";
            switch (name)
            {
                case "启动": PageHint.Text = "启动页 · 选版本然后点开始游戏"; break;
                case "下载": PageHint.Text = "下载页（原型）· 版本清单 / 加载器 / Java"; break;
                case "整合包": PageHint.Text = "整合包页（原型）· mrpack / MultiMC / CurseForge"; break;
                case "设置": PageHint.Text = "设置页（原型）· 数据目录 / 下载源 / 内存"; break;
            }
            StatusText.Text = "就绪 · 当前页面：" + name;
        }

        // ---------- 版本选择 ----------
        private void VersionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (StatusText == null) return;
            var item = VersionList.SelectedItem as ListBoxItem;
            if (item == null) return;
            string text = ExtractText(item);
            CardDetail.Text = text.Replace("  ", " ");
            StatusText.Text = "已选择：" + text;
        }

        private static string ExtractText(ListBoxItem item)
        {
            var panel = item.Content as StackPanel;
            if (panel == null) return "";
            string main = "", sub = "";
            foreach (var child in panel.Children)
            {
                var tb = child as TextBlock;
                if (tb == null) continue;
                if (main.Length == 0) main = tb.Text;
                else if (sub.Length == 0) sub = tb.Text;
            }
            return sub.Length > 0 ? main + " · " + sub : main;
        }

        // ---------- 假启动动画（原型用：演示进度反馈） ----------
        private void Play_Click(object sender, RoutedEventArgs e)
        {
            CardTitle.Text = "正在启动";
            CardDetail.Text = "校验文件 → 下载缺失 → 解压 natives → 启动 JVM";
            StatusText.Text = "启动中 …";

            double from = ProgressFill.Width <= 0 ? 0 : ProgressFill.Width;
            var anim = new DoubleAnimation
            {
                From = from,
                To = 372,
                Duration = TimeSpan.FromSeconds(2.6),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut },
            };
            anim.Completed += (s, a) =>
            {
                CardTitle.Text = "游戏已启动";
                CardDetail.Text = "窗口已交给 Minecraft（原型：这里只是动画）";
                StatusText.Text = "就绪 · 启动流程完成（演示）";
                // 3 秒后回到初始状态，方便重复演示
                var timer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(3)
                };
                timer.Tick += (s2, a2) =>
                {
                    timer.Stop();
                    ProgressFill.BeginAnimation(WidthProperty, null);
                    ProgressFill.Width = 120;
                    CardTitle.Text = "正在准备启动";
                    CardDetail.Text = "1.20.1 · Fabric 0.19.5 · Java 21 · 4 GB";
                    StatusText.Text = "就绪 · 镜像 mirrors.hit.edu.cn · 7.8 MB/s";
                };
                timer.Start();
            };
            ProgressFill.BeginAnimation(WidthProperty, anim);
        }
    }
}
