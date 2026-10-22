using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace Mcroute
{
    /// <summary>资源包 / 光影 的本地管理窗口（列出 / 启停 / 导入 / 删除）。</summary>
    public partial class PacksWindow : Window
    {
        private readonly string _baseDir;
        private readonly string _version;
        private List<LocalPack> _packs = new List<LocalPack>();
        private string _kind = LocalPacks.ResourcePacks;

        public PacksWindow(string baseDir, string version)
        {
            InitializeComponent();
            _baseDir = baseDir;
            _version = version;
            TitleText.Text = "资源包 / 光影 · " + version;

            Loaded += (s, e) => Reload();
        }

        /// <summary>给截图/外部调用：切到指定页签。</summary>
        public void PreloadTab(bool shaders)
        {
            if (shaders) TabShaders.IsChecked = true; else TabPacks.IsChecked = true;
            Reload();
        }

        private void Reload()
        {
            try
            {
                _packs = LocalPacks.Scan(_baseDir, _version, _kind);
                PackList.Items.Clear();
                foreach (var p in _packs) PackList.Items.Add(MakeItem(p));

                int enabled = 0;
                foreach (var p in _packs) if (!p.Disabled) enabled++;
                HintText.Text = _kind == LocalPacks.ResourcePacks
                    ? "放在 versions\\" + _version + "\\resourcepacks"
                    : "放在 versions\\" + _version + "\\shaderpacks";
                StatusText.Text = string.Format("共 {0} 个（启用 {1} / 禁用 {2}）", _packs.Count, enabled, _packs.Count - enabled);
                if (PackList.Items.Count > 0) PackList.SelectedIndex = 0;
            }
            catch (Exception ex)
            {
                StatusText.Text = "读取失败：" + ex.Message;
            }
        }

        private ListBoxItem MakeItem(LocalPack p)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel();
            var title = new TextBlock
            {
                Text = p.Title,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            title.SetResourceReference(TextBlock.ForegroundProperty, "Text");

            string meta = p.SizeText();
            if (p.PackFormat > 0) meta += " · 格式 " + p.PackFormat;
            if (!string.IsNullOrEmpty(p.Description)) meta += " · " + p.Description;
            var sub = new TextBlock
            {
                Text = meta,
                FontSize = 10.5,
                Margin = new Thickness(0, 2, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            sub.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");

            left.Children.Add(title);
            left.Children.Add(sub);
            Grid.SetColumn(left, 0);
            grid.Children.Add(left);

            var state = new TextBlock
            {
                Text = p.Disabled ? "已禁用" : "启用中",
                FontSize = 10.5,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0),
            };
            state.SetResourceReference(TextBlock.ForegroundProperty, p.Disabled ? "TextFaint" : "Primary");
            Grid.SetColumn(state, 1);
            grid.Children.Add(state);

            return new ListBoxItem { Content = grid, Tag = p };
        }

        private LocalPack Selected()
        {
            var item = PackList.SelectedItem as ListBoxItem;
            return item == null ? null : item.Tag as LocalPack;
        }

        private void Tab_Checked(object sender, RoutedEventArgs e)
        {
            if (PackList == null) return;
            _kind = (TabShaders != null && TabShaders.IsChecked == true)
                ? LocalPacks.ShaderPacks : LocalPacks.ResourcePacks;
            Reload();
        }

        private void PackList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var p = Selected();
            if (ToggleBtn != null)
                ToggleBtn.Content = (p != null && p.Disabled) ? "启用" : "禁用";
        }

        private void PackList_MouseDoubleClick(object sender, MouseButtonEventArgs e) { Toggle(); }

        private void Toggle_Click(object sender, RoutedEventArgs e) { Toggle(); }

        private void Toggle()
        {
            var p = Selected();
            if (p == null) { StatusText.Text = "先选一个"; return; }
            string err;
            if (!LocalPacks.SetEnabled(p, p.Disabled, out err))
            {
                MessageBox.Show("操作失败：" + err, "提示");
                return;
            }
            Reload();
            StatusText.Text = p.Disabled ? ("已禁用 " + p.Title) : ("已启用 " + p.Title);
        }

        private void SearchModrinth_Click(object sender, RoutedEventArgs e)
        {
            string kind = _kind == LocalPacks.ResourcePacks ? "resourcepack" : "shader";
            new ModsBrowserWindow(_baseDir, _version, "", "", kind) { Owner = this }.ShowDialog();
            Reload();
        }

        private void Import_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = _kind == LocalPacks.ResourcePacks ? "选择资源包（.zip）" : "选择光影包（.zip）",
                Filter = "压缩包 (*.zip;*.jar)|*.zip;*.jar|所有文件 (*.*)|*.*",
                Multiselect = true,
            };
            if (dlg.ShowDialog(this) != true) return;

            int ok = 0;
            string last = "";
            foreach (string file in dlg.FileNames)
            {
                string msg;
                if (LocalPacks.Import(_baseDir, _version, _kind, file, out msg)) ok++;
                last = msg;
            }
            Reload();
            StatusText.Text = "导入 " + ok + " 个" + (ok > 0 ? "：" + last : "（" + last + "）");
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            var p = Selected();
            if (p == null) return;
            if (MessageBox.Show("删除 " + p.Title + "？\n\n文件会被直接删除，不能恢复。", "删除",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

            string err;
            if (!LocalPacks.Delete(p, out err)) { MessageBox.Show("删除失败：" + err, "提示"); return; }
            Reload();
            StatusText.Text = "已删除 " + p.Title;
        }

        private void OpenDir_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string dir = LocalPacks.PackDir(_baseDir, _version, _kind);
                Directory.CreateDirectory(dir);
                if (ShellOpen.TryOpenAndSelect(dir)) return;
                new FileBrowserWindow(dir) { Owner = this }.ShowDialog();
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
