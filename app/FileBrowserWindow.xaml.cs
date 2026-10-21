using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Mcroute
{
    /// <summary>
    /// 应用内文件浏览：不依赖系统资源管理器 / Shell API。
    ///
    /// 用途：某些受限环境（沙箱、受限 token）里 SHOpenFolderAndSelectItems 会被拒绝，
    /// 这时「打开目录」自动退回到这个窗口，用户仍然能看到文件。
    /// </summary>
    public partial class FileBrowserWindow : Window
    {
        private string _dir;

        public FileBrowserWindow(string dir)
        {
            InitializeComponent();
            Loaded += (s, e) => Load(dir);
        }

        private void Load(string dir)
        {
            try
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                {
                    PathText.Text = dir ?? "（目录不存在）";
                    FileList.Items.Clear();
                    return;
                }

                _dir = Path.GetFullPath(dir);
                PathText.Text = _dir;

                var items = new List<Entry>();

                try
                {
                    foreach (var d in new DirectoryInfo(_dir).GetDirectories().OrderBy(x => x.Name))
                    {
                        // 跳过系统隐藏目录，避免一堆噪音
                        try
                        {
                            if ((d.Attributes & FileAttributes.Hidden) != 0) continue;
                        }
                        catch { }
                        items.Add(new Entry { IsDir = true, Name = d.Name, Path = d.FullName, Info = "" });
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    HintText.Text = "这个目录没有读取权限";
                }

                try
                {
                    foreach (var f in new DirectoryInfo(_dir).GetFiles().OrderBy(x => x.Name))
                    {
                        try
                        {
                            if ((f.Attributes & FileAttributes.Hidden) != 0) continue;
                        }
                        catch { }
                        items.Add(new Entry
                        {
                            IsDir = false,
                            Name = f.Name,
                            Path = f.FullName,
                            Info = SizeText(f.Length) + " · " + f.LastWriteTime.ToString("MM-dd HH:mm"),
                        });
                    }
                }
                catch { }

                FileList.Items.Clear();
                foreach (var it in items) FileList.Items.Add(MakeItem(it));

                HintText.Text = string.Format("{0} 个文件夹 · {1} 个文件（双击进入 / 打开）",
                    items.Count(x => x.IsDir), items.Count(x => !x.IsDir));
            }
            catch (Exception ex)
            {
                HintText.Text = "读取失败：" + ex.Message;
            }
        }

        private static string SizeText(long bytes)
        {
            if (bytes >= 1024 * 1024) return (bytes / 1024.0 / 1024.0).ToString("F1") + " MB";
            if (bytes >= 1024) return (bytes / 1024.0).ToString("F0") + " KB";
            return bytes + " B";
        }

        private static ListBoxItem MakeItem(Entry e)
        {
            var panel = new Grid();
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var name = new TextBlock
            {
                Text = (e.IsDir ? "📁 " : "📄 ") + e.Name,
                FontSize = 12.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            name.SetResourceReference(TextBlock.ForegroundProperty, e.IsDir ? "Text" : "TextSub");
            Grid.SetColumn(name, 0);
            panel.Children.Add(name);

            var info = new TextBlock
            {
                Text = e.Info,
                FontSize = 11,
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            info.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
            Grid.SetColumn(info, 1);
            panel.Children.Add(info);

            return new ListBoxItem { Content = panel, Tag = e };
        }

        private Entry Selected()
        {
            var item = FileList.SelectedItem as ListBoxItem;
            return item == null ? null : item.Tag as Entry;
        }

        private void FileList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var entry = Selected();
            if (entry == null) return;

            if (entry.IsDir)
            {
                Load(entry.Path);
                return;
            }

            // 文件：用系统默认程序打开（这种一般不受 shell 文件夹限制影响）
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = entry.Path,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                try { Clipboard.SetText(entry.Path); } catch { }
                MessageBox.Show("打不开这个文件：" + ex.Message + "\n\n路径已复制到剪贴板。", "提示");
            }
        }

        private void Up_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (string.IsNullOrEmpty(_dir)) return;
                var parent = Directory.GetParent(_dir);
                if (parent != null) Load(parent.FullName);
            }
            catch (Exception ex) { HintText.Text = "无法返回上级：" + ex.Message; }
        }

        private void CopyPath_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string target = _dir;
                var entry = Selected();
                if (entry != null) target = entry.Path;
                Clipboard.SetText(target);
                HintText.Text = "路径已复制：" + target;
            }
            catch (Exception ex) { HintText.Text = "复制失败：" + ex.Message; }
        }

        private void Refresh_Click(object sender, RoutedEventArgs e) { Load(_dir); }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }

        private void Close_Click(object sender, RoutedEventArgs e) { Close(); }

        private class Entry
        {
            public bool IsDir;
            public string Name;
            public string Path;
            public string Info;
        }
    }
}
