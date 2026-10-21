using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Mcroute
{
    /// <summary>
    /// Mod 下载窗口（一期：Modrinth）。
    /// CurseForge 需要用户自己的 API Key，二期再接。
    /// </summary>
    public partial class ModsBrowserWindow : Window
    {
        private readonly string _baseDir;
        private readonly string _version;
        private List<ModHit> _hits = new List<ModHit>();

        public ModsBrowserWindow(string baseDir, string version, string mcVersion, string loader)
        {
            InitializeComponent();
            _baseDir = baseDir;
            _version = version;

            McBox.Text = mcVersion ?? "";
            SelectLoader(loader);

            Loaded += (s, e) =>
            {
                StatusText.Text = "输入关键词后回车搜索";
                SearchBox.Focus();
            };
        }

        private void SelectLoader(string loader)
        {
            string id = Modrinth.LoaderId(loader) ?? "fabric";
            foreach (var item in LoaderBox.Items.OfType<ComboBoxItem>())
                if (string.Equals(item.Content as string, id, StringComparison.OrdinalIgnoreCase))
                    item.IsSelected = true;
        }

        private string CurrentLoader()
        {
            var item = LoaderBox.SelectedItem as ComboBoxItem;
            return item == null ? null : item.Content as string;
        }

        /// <summary>给截图用：填关键词并直接搜索。</summary>
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
            string query = SearchBox.Text.Trim();
            if (query.Length == 0) { StatusText.Text = "先输入关键词"; return; }

            string mc = McBox.Text.Trim();
            string loader = CurrentLoader();
            StatusText.Text = "搜索中…";
            HitList.Items.Clear();

            var task = new System.Threading.Tasks.Task(() =>
            {
                string err;
                var hits = Modrinth.Search(query, mc, loader, 30, out err);
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!string.IsNullOrEmpty(err))
                    {
                        StatusText.Text = "搜索失败：" + err;
                        return;
                    }
                    _hits = hits;
                    HitList.Items.Clear();
                    foreach (var h in hits) HitList.Items.Add(MakeItem(h));
                    StatusText.Text = string.Format("找到 {0} 个 mod（{1}{2}）", hits.Count,
                        string.IsNullOrEmpty(mc) ? "任意版本" : mc,
                        string.IsNullOrEmpty(loader) ? "" : " · " + loader);
                    if (HitList.Items.Count > 0) HitList.SelectedIndex = 0;
                }));
            });
            task.Start();
        }

        private ListBoxItem MakeItem(ModHit h)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // 图标（加载失败就留空）
            var iconHost = new Border
            {
                Width = 32,
                Height = 32,
                CornerRadius = new CornerRadius(7),
                VerticalAlignment = VerticalAlignment.Top,
            };
            iconHost.SetResourceReference(Border.BackgroundProperty, "PrimaryDim");
            try
            {
                if (!string.IsNullOrEmpty(h.IconUrl))
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(h.IconUrl, UriKind.Absolute);
                    bmp.DecodePixelWidth = 32;
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                    bmp.EndInit();
                    iconHost.Child = new Image { Source = bmp, Stretch = Stretch.UniformToFill };
                }
            }
            catch { }
            Grid.SetColumn(iconHost, 0);
            grid.Children.Add(iconHost);

            var left = new StackPanel { Margin = new Thickness(10, 0, 0, 0) };
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
                Text = string.Format("{0} · 下载 {1} · {2}", h.Author, h.DownloadsText(), h.Categories ?? ""),
                FontSize = 10.5,
                Margin = new Thickness(0, 2, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
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
            Grid.SetColumn(left, 1);
            grid.Children.Add(left);

            var install = new Button
            {
                Content = "安装",
                Width = 60,
                Height = 26,
                Style = (Style)FindResource("GhostButton"),
                Tag = h,
                VerticalAlignment = VerticalAlignment.Center,
            };
            install.Click += (s, e) => { Install(h); };
            Grid.SetColumn(install, 2);
            grid.Children.Add(install);

            return new ListBoxItem { Content = grid, Tag = h };
        }

        private ModHit SelectedHit()
        {
            var item = HitList.SelectedItem as ListBoxItem;
            return item == null ? null : item.Tag as ModHit;
        }

        private void HitList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var h = SelectedHit();
            if (h != null) Install(h);
        }

        private void Install_Click(object sender, RoutedEventArgs e)
        {
            var h = SelectedHit();
            if (h == null) { StatusText.Text = "先选一个 mod"; return; }
            Install(h);
        }

        private void Install(ModHit h)
        {
            string mc = McBox.Text.Trim();
            string loader = CurrentLoader();
            StatusText.Text = "正在安装 " + h.Title + " …";
            InstallBtn.IsEnabled = false;

            var task = new System.Threading.Tasks.Task(() =>
            {
                string msg;
                bool ok = Modrinth.Install(_baseDir, _version, h.ProjectId, mc, loader, out msg);
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    InstallBtn.IsEnabled = true;
                    StatusText.Text = msg.Replace("\n", "  ");
                    if (!ok)
                        MessageBox.Show(msg, "安装失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                }));
            });
            task.Start();
        }

        private void OpenModsDir_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string dir = Mods.ModsDir(_baseDir, _version);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                if (ShellOpen.TryOpenAndSelect(dir)) return;
                new FileBrowserWindow(dir) { Owner = this }.ShowDialog();
            }
            catch (Exception ex) { MessageBox.Show("打开失败：" + ex.Message, "提示"); }
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }

        private void Close_Click(object sender, RoutedEventArgs e) { Close(); }
    }
}
