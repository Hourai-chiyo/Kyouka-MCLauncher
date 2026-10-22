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
    /// <summary>从 Modrinth 搜索并安装整合包（.mrpack），复用本地整合包安装器。</summary>
    public partial class ModpackWindow : Window
    {
        private readonly string _baseDir;
        private readonly Action _onInstalled;
        private List<ModHit> _hits = new List<ModHit>();

        public ModpackWindow(string baseDir, Action onInstalled = null)
        {
            InitializeComponent();
            _baseDir = baseDir;
            _onInstalled = onInstalled;
            Loaded += (s, e) =>
            {
                StatusText.Text = "输入关键词搜索整合包（数据来自 Modrinth）";
                SearchBox.Focus();
            };
        }

        /// <summary>给截图用。</summary>
        public void PreloadSearch(string query)
        {
            SearchBox.Text = query ?? "";
            Search_Click(this, new RoutedEventArgs());
        }

        private void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) Search_Click(sender, new RoutedEventArgs());
        }

        private void Search_Click(object sender, RoutedEventArgs e)
        {
            string q = SearchBox.Text.Trim();
            if (q.Length == 0) { StatusText.Text = "先输入关键词"; return; }

            StatusText.Text = "搜索中…";
            PackList.Items.Clear();

            var t = new System.Threading.Tasks.Task(() =>
            {
                string err;
                var hits = Modrinth.Search(q, null, null, 30, "modpack", out err);
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!string.IsNullOrEmpty(err)) { StatusText.Text = "搜索失败：" + err; return; }
                    _hits = hits;
                    PackList.Items.Clear();
                    foreach (var h in hits) PackList.Items.Add(MakeItem(h));
                    StatusText.Text = "找到 " + hits.Count + " 个整合包";
                    if (PackList.Items.Count > 0) PackList.SelectedIndex = 0;
                }));
            });
            t.Start();
        }

        private ListBoxItem MakeItem(ModHit h)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel();
            var title = new TextBlock
            {
                Text = h.Title,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            title.SetResourceReference(TextBlock.ForegroundProperty, "Text");

            var sub = new TextBlock
            {
                Text = string.Format("{0} · 下载 {1}", h.Author, h.DownloadsText()),
                FontSize = 10.5,
                Margin = new Thickness(0, 2, 0, 0),
            };
            sub.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");

            var desc = new TextBlock
            {
                Text = h.Description,
                FontSize = 11,
                Margin = new Thickness(0, 3, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
            };
            desc.SetResourceReference(TextBlock.ForegroundProperty, "TextSub");

            left.Children.Add(title);
            left.Children.Add(sub);
            left.Children.Add(desc);
            Grid.SetColumn(left, 0);
            grid.Children.Add(left);
            return new ListBoxItem { Content = grid, Tag = h };
        }

        private ModHit Selected()
        {
            var item = PackList.SelectedItem as ListBoxItem;
            return item == null ? null : item.Tag as ModHit;
        }

        private void PackList_MouseDoubleClick(object sender, MouseButtonEventArgs e) { InstallSelected(); }

        private void Install_Click(object sender, RoutedEventArgs e) { InstallSelected(); }

        private void InstallSelected()
        {
            var h = Selected();
            if (h == null) { StatusText.Text = "先选一个整合包"; return; }

            InstallBtn.IsEnabled = false;
            StatusText.Text = "正在查询 " + h.Title + " 的最新版本…";

            var t = new System.Threading.Tasks.Task(() =>
            {
                string err;
                var versions = Modrinth.Versions(h.ProjectId, null, null, out err);
                if (!string.IsNullOrEmpty(err) || versions.Count == 0)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        InstallBtn.IsEnabled = true;
                        StatusText.Text = "取版本失败：" + (err ?? "没有可用版本");
                    }));
                    return;
                }

                object first = versions[0];
                var files = Version.AsList(J.Get(first, "files"));
                object file = files != null && files.Count > 0 ? files[0] : null;
                string url = file == null ? null : J.Str(file, "url");
                string filename = file == null ? null : J.Str(file, "filename");
                if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(filename))
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        InstallBtn.IsEnabled = true;
                        StatusText.Text = "这个版本没有 .mrpack 文件";
                    }));
                    return;
                }

                string tmpDir = Path.Combine(Path.GetTempPath(), "kyouka-modpacks");
                Directory.CreateDirectory(tmpDir);
                string dest = Path.Combine(tmpDir, filename);

                Dispatcher.BeginInvoke(new Action(() => StatusText.Text = "下载中：" + filename));
                string info;
                string sha1 = J.Str(J.Dict(file, "hashes"), "sha1");
                string status = Http.Download(url, dest, string.IsNullOrEmpty(sha1) ? null : sha1, out info, false);
                if (status == "fail")
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        InstallBtn.IsEnabled = true;
                        StatusText.Text = "下载失败：" + info;
                    }));
                    return;
                }

                Dispatcher.BeginInvoke(new Action(() => StatusText.Text = "安装中（解压 + 补依赖）…"));
                int code = 1;
                try { code = Packs.Install(new[] { "install", dest, "--dir", _baseDir }); }
                catch (Exception ex) { info = ex.Message; }

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    InstallBtn.IsEnabled = true;
                    StatusText.Text = code == 0
                        ? "安装完成：" + filename + "（回「整合包」页刷新即可）"
                        : "安装失败（退出码 " + code + "）：" + info;
                    if (code == 0 && _onInstalled != null) { try { _onInstalled(); } catch { } }
                }));
            });
            t.Start();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }

        private void Close_Click(object sender, RoutedEventArgs e) { Close(); }
    }
}
