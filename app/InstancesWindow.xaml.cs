using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Mcroute
{
    /// <summary>多实例管理：每个实例独立 mods / saves / config，基于某个 MC 版本。</summary>
    public partial class InstancesWindow : Window
    {
        private readonly string _baseDir;
        private List<InstanceInfo> _items = new List<InstanceInfo>();

        public InstancesWindow(string baseDir)
        {
            InitializeComponent();
            _baseDir = baseDir;
            Loaded += (s, e) => Reload();
        }

        private void Reload()
        {
            _items = Instances.List(_baseDir);
            InstList.Items.Clear();
            foreach (var i in _items) InstList.Items.Add(MakeItem(i));
            StatusText.Text = "共 " + _items.Count + " 个实例";
            if (InstList.Items.Count > 0) InstList.SelectedIndex = 0;

            VersionBox.Items.Clear();
            foreach (string v in Instances.VersionCandidates(_baseDir)) VersionBox.Items.Add(v);
            if (VersionBox.Items.Count > 0) VersionBox.SelectedIndex = 0;
        }

        private ListBoxItem MakeItem(InstanceInfo i)
        {
            var panel = new StackPanel();
            var title = new TextBlock
            {
                Text = i.Name,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
            };
            title.SetResourceReference(TextBlock.ForegroundProperty, "Text");
            var sub = new TextBlock
            {
                Text = (string.IsNullOrEmpty(i.Version) ? "未记录版本" : "基于 " + i.Version) +
                       (string.IsNullOrEmpty(i.Created) ? "" : " · 创建于 " + i.Created),
                FontSize = 10,
                Margin = new Thickness(0, 2, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            sub.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
            panel.Children.Add(title);
            panel.Children.Add(sub);
            var item = new ListBoxItem { Content = panel, Tag = i };
            try { item.ToolTip = i.Dir; } catch { }
            return item;
        }

        private InstanceInfo Selected()
        {
            var item = InstList.SelectedItem as ListBoxItem;
            return item == null ? null : item.Tag as InstanceInfo;
        }

        private void InstList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var i = Selected();
            if (i == null) return;
            if (!string.IsNullOrEmpty(i.Name) && string.IsNullOrWhiteSpace(NameBox.Text)) NameBox.Text = i.Name;
        }

        private void Create_Click(object sender, RoutedEventArgs e)
        {
            string name = NameBox.Text.Trim();
            string ver = VersionBox.SelectedItem as string;
            string err;
            if (!Instances.Create(_baseDir, name, ver, out err))
            {
                MessageBox.Show(err, "创建失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            NameBox.Clear();
            Reload();
            StatusText.Text = "已创建实例「" + name + "」（基于 " + ver + "）";
        }

        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            var src = Selected();
            string name = NameBox.Text.Trim();
            if (src == null) { StatusText.Text = "先选一个实例"; return; }
            string err;
            if (!Instances.Copy(_baseDir, src.Name, name, out err))
            {
                MessageBox.Show(err, "复制失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            Reload();
            StatusText.Text = "已复制为「" + name + "」";
        }

        private void Rename_Click(object sender, RoutedEventArgs e)
        {
            var src = Selected();
            string name = NameBox.Text.Trim();
            if (src == null) { StatusText.Text = "先选一个实例"; return; }
            if (name == src.Name) { StatusText.Text = "名字没变"; return; }
            string err;
            if (!Instances.Rename(_baseDir, src.Name, name, out err))
            {
                MessageBox.Show(err, "重命名失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            Reload();
            StatusText.Text = "已重命名为「" + name + "」";
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            var i = Selected();
            if (i == null) { StatusText.Text = "先选一个实例"; return; }
            if (MessageBox.Show("删除实例「" + i.Name + "」？\n\n目录 " + i.Dir + "\n会被整个删除（含 mods / 存档）。",
                    "删除实例", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

            string err;
            if (!Instances.Delete(_baseDir, i.Name, out err))
            {
                MessageBox.Show(err, "删除失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            Reload();
            StatusText.Text = "已删除「" + i.Name + "」";
        }

        private void Launch_Click(object sender, MouseButtonEventArgs e) { LaunchSelected(); }

        private void Launch_Click(object sender, RoutedEventArgs e) { LaunchSelected(); }

        private void LaunchSelected()
        {
            var i = Selected();
            if (i == null) { StatusText.Text = "先选一个实例"; return; }

            string ver = i.Version;
            if (string.IsNullOrEmpty(ver))
            {
                var info = Instances.Load(_baseDir, i.Name);
                ver = info != null ? info.Version : null;
            }
            if (string.IsNullOrEmpty(ver))
            {
                MessageBox.Show("这个实例没有记录 MC 版本（instance.json 里没有 version）。", "启动失败");
                return;
            }

            LaunchBtn.IsEnabled = false;
            StatusText.Text = "正在启动 " + i.Name + " …";

            var t = new System.Threading.Tasks.Task(() =>
            {
                string log = "";
                int code = -1;
                try
                {
                    code = Commands.Launch(new[] { "launch", ver, "--dir", _baseDir, "--game-dir", i.Dir, "--memory", "6G" });
                    log = "退出码 " + code;
                }
                catch (Exception ex) { log = ex.Message; }

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    LaunchBtn.IsEnabled = true;
                    StatusText.Text = "启动结束：" + log;
                }));
            });
            t.Start();
        }

        private void OpenDir_Click(object sender, RoutedEventArgs e)
        {
            var i = Selected();
            if (i == null) { StatusText.Text = "先选一个实例"; return; }
            try
            {
                if (ShellOpen.TryOpenAndSelect(i.Dir)) return;
                new FileBrowserWindow(i.Dir) { Owner = this }.ShowDialog();
            }
            catch (Exception ex) { MessageBox.Show("打开失败：" + ex.Message, "提示"); }
        }

        private void Refresh_Click(object sender, RoutedEventArgs e) { Reload(); }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }

        private void Close_Click(object sender, RoutedEventArgs e) { Close(); }
    }
}
