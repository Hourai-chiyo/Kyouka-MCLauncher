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
    /// Mod 下载窗口。两家来源**不合并**：Modrinth / CurseForge 各查各的，
    /// 同名 mod 在两边版本可能不同，混在一起容易装错。
    /// CurseForge 需要用户自己的 API Key（&lt;数据目录&gt;/cf_api_key.txt）。
    /// </summary>
    public partial class ModsBrowserWindow : Window
    {
        private readonly string _baseDir;
        private readonly string _version;
        private List<Item> _items = new List<Item>();
        private readonly string _kind;   // mod / resourcepack / shader

        /// <summary>两家结果的统一展示模型。</summary>
        private class Item
        {
            public string Source;
            public string ProjectId;
            public long CfId;
            public string Title;
            public string Author;
            public string Description;
            public string IconUrl;
            public long Downloads;
            public string Categories;

            public string DownloadsText()
            {
                if (Downloads >= 1000000) return (Downloads / 1000000.0).ToString("F1") + "M";
                if (Downloads >= 1000) return (Downloads / 1000.0).ToString("F0") + "K";
                return Downloads.ToString();
            }
        }

        public ModsBrowserWindow(string baseDir, string version, string mcVersion, string loader, string kind = "mod")
        {
            _kind = string.IsNullOrEmpty(kind) ? "mod" : kind;
            InitializeComponent();
            _baseDir = baseDir;
            _version = version;

            McBox.Text = mcVersion ?? "";
            SelectLoader(loader);

            if (_kind == "resourcepack") HeaderText.Text = "下载资源包（Modrinth）";
            else if (_kind == "shader") HeaderText.Text = "下载光影（Modrinth）";
            if (_kind != "mod")
            {
                // 资源包/光影没有加载器；CurseForge 与百科也只做 mod
                if (LoaderBox != null) LoaderBox.Visibility = Visibility.Collapsed;
                if (WikiButton != null) WikiButton.Visibility = Visibility.Collapsed;
                foreach (var it in SourceBox.Items.OfType<ComboBoxItem>())
                    if (string.Equals(it.Content as string, "CurseForge", StringComparison.OrdinalIgnoreCase))
                        it.Visibility = Visibility.Collapsed;
                if (SourceBox.Items.Count > 0) SourceBox.SelectedIndex = 0;
            }

            Loaded += (s, e) =>
            {
                StatusText.Text = CurseForge.HasKey(baseDir)
                    ? "输入关键词后回车搜索（CurseForge 已配置 Key）"
                    : "输入关键词后回车搜索；CurseForge 需要 Key（官方控制台只能 Google 登录）";
                SearchBox.Focus();
                UpdateKeyPanel();
            };
        }

        private string CurrentSource()
        {
            var it = SourceBox.SelectedItem as ComboBoxItem;
            string s = it == null ? "Modrinth" : (it.Content as string);
            return string.Equals(s, "CurseForge", StringComparison.OrdinalIgnoreCase) ? "cf" : "modrinth";
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
            // 截图用：MCROUTE_SHOT_SOURCE=cf 可以把来源切到 CurseForge
            string src = Environment.GetEnvironmentVariable("MCROUTE_SHOT_SOURCE");
            if (!string.IsNullOrEmpty(src))
            {
                foreach (var it in SourceBox.Items.OfType<ComboBoxItem>())
                    if (string.Equals((it.Content as string) ?? "", src, StringComparison.OrdinalIgnoreCase))
                        it.IsSelected = true;
                UpdateKeyPanel();
            }
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
            string source = CurrentSource();

            if (source == "cf" && _kind != "mod")
            {
                StatusText.Text = "CurseForge 只用于 Mod";
                return;
            }
            if (source == "cf" && !CurseForge.HasKey(_baseDir))
            {
                UpdateKeyPanel();
                return;
            }
            KeyPanel.Visibility = Visibility.Collapsed;

            StatusText.Text = "搜索中…";
            HitList.Items.Clear();

            var task = new System.Threading.Tasks.Task(() =>
            {
                var got = new List<Item>();
                string err = null;

                if (source == "cf")
                {
                    var hits = CurseForge.Search(_baseDir, query, mc, loader, 30, out err);
                    foreach (var h in hits)
                        got.Add(new Item
                        {
                            Source = "cf",
                            CfId = h.ModId,
                            Title = h.Name,
                            Author = h.Author,
                            Description = h.Summary,
                            IconUrl = h.LogoUrl,
                            Downloads = h.Downloads,
                            Categories = h.Categories,
                        });
                }
                else
                {
                    var hits = Modrinth.Search(query, mc, loader, 30,
                        _kind == "resourcepack" ? "resourcepack" : (_kind == "shader" ? "shader" : "mod"), out err);
                    foreach (var h in hits)
                        got.Add(new Item
                        {
                            Source = "modrinth",
                            ProjectId = h.ProjectId,
                            Title = h.Title,
                            Author = h.Author,
                            Description = h.Description,
                            IconUrl = h.IconUrl,
                            Downloads = h.Downloads,
                            Categories = h.Categories,
                        });
                }

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!string.IsNullOrEmpty(err))
                    {
                        StatusText.Text = "搜索失败：" + err;
                        return;
                    }
                    _items = got;
                    HitList.Items.Clear();
                    foreach (var it in got) HitList.Items.Add(MakeItem(it));
                    StatusText.Text = string.Format("找到 {0} 个 mod（{1}{2} · {3}）", got.Count,
                        string.IsNullOrEmpty(mc) ? "任意版本" : mc,
                        string.IsNullOrEmpty(loader) ? "" : " · " + loader,
                        source == "cf" ? "CurseForge" : "Modrinth");
                    if (HitList.Items.Count > 0) HitList.SelectedIndex = 0;
                }));
            });
            task.Start();
        }

        private ListBoxItem MakeItem(Item h)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

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
                VerticalAlignment = VerticalAlignment.Center,
            };
            install.Click += (s, e) => Install(h);
            Grid.SetColumn(install, 2);
            grid.Children.Add(install);

            return new ListBoxItem { Content = grid, Tag = h };
        }

        private Item SelectedItem()
        {
            var item = HitList.SelectedItem as ListBoxItem;
            return item == null ? null : item.Tag as Item;
        }

        private void HitList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var h = SelectedItem();
            if (h != null) Install(h);
        }

        private void Install_Click(object sender, RoutedEventArgs e)
        {
            var h = SelectedItem();
            if (h == null) { StatusText.Text = "先选一个 mod"; return; }
            Install(h);
        }

        private void Install(Item h)
        {
            string mc = McBox.Text.Trim();
            string loader = CurrentLoader();
            StatusText.Text = "正在安装 " + h.Title + " …";
            InstallBtn.IsEnabled = false;

            var task = new System.Threading.Tasks.Task(() =>
            {
                string msg;
                bool ok = h.Source == "cf"
                    ? CurseForge.Install(_baseDir, _version, h.CfId, mc, loader, out msg)
                    : Modrinth.InstallToKind(_baseDir, _version, h.ProjectId, mc, loader, _kind, out msg);
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

        private void InstallFromLink_Click(object sender, RoutedEventArgs e)
        {
            string url = LinkBox.Text.Trim();
            if (url.Length == 0) { StatusText.Text = "先把直链粘到下面输入框"; return; }
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                StatusText.Text = "链接要以 http:// 或 https:// 开头";
                return;
            }

            StatusText.Text = "正在从链接安装…";
            InstallBtn.IsEnabled = false;

            var task = new System.Threading.Tasks.Task(() =>
            {
                string msg;
                bool ok = Mods.InstallFromUrl(_baseDir, _version, url, out msg);
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    InstallBtn.IsEnabled = true;
                    StatusText.Text = msg;
                    if (ok) LinkBox.Clear();
                    else MessageBox.Show(msg, "安装失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                }));
            });
            task.Start();
        }

        private const string CfConsoleUrl = "https://console.curseforge.com/";

        private void OpenCfConsole_Click(object sender, RoutedEventArgs e)
        {
            // 说明：从受限环境（沙箱 / 受限 token）启动时，打开浏览器可能被系统拒绝，
            // 所以这里把「复制链接」和「尝试打开」绑在一起，保证一定给用户反馈。
            try { Clipboard.SetText(CfConsoleUrl); } catch { }

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = CfConsoleUrl,
                    UseShellExecute = true,
                });
                StatusText.Text = "链接已复制（" + CfConsoleUrl + "）；浏览器没弹出来的话，手动粘到地址栏即可";
            }
            catch (Exception ex)
            {
                StatusText.Text = "打不开浏览器，链接已复制到剪贴板";
                MessageBox.Show(
                    "启动器没能调起浏览器（当前环境的系统调用被限制）。\n\n" +
                    "链接已经复制到剪贴板，手动粘到浏览器即可：\n" + CfConsoleUrl + "\n\n" +
                    "详细错误：" + ex.Message,
                    "获取 API Key");
            }
        }

        private void SaveCfKey_Click(object sender, RoutedEventArgs e)
        {
            string key = KeyBox.Text.Trim();
            if (key.Length < 20) { MessageBox.Show("Key 看起来太短了，请完整复制", "提示"); return; }
            try
            {
                string path = System.IO.Path.Combine(_baseDir, "cf_api_key.txt");
                System.IO.Directory.CreateDirectory(_baseDir);
                System.IO.File.WriteAllText(path, key);
                KeyPanel.Visibility = Visibility.Collapsed;
                StatusText.Text = "已保存 CurseForge Key，可以搜索了";
                KeyBox.Clear();
            }
            catch (Exception ex) { MessageBox.Show("保存失败：" + ex.Message, "提示"); }
        }

        private void SourceBox_Changed(object sender, SelectionChangedEventArgs e)
        {
            UpdateKeyPanel();
        }

        /// <summary>切到 CurseForge 且没有 Key 时，显示输入面板。</summary>
        private void UpdateKeyPanel()
        {
            if (KeyPanel == null) return;
            bool needKey = CurrentSource() == "cf" && !CurseForge.HasKey(_baseDir);
            KeyPanel.Visibility = needKey ? Visibility.Visible : Visibility.Collapsed;
            if (needKey) StatusText.Text = "CurseForge 需要 API Key（官方控制台只能 Google 登录，国内难申请）——可切回 Modrinth，或用底部「从链接安装」";
        }

        /// <summary>用浏览器打开 MC百科搜索（不抓取，尊重站点规则）。</summary>
        private void OpenMcmodSearch_Click(object sender, RoutedEventArgs e)
        {
            string q = SearchBox.Text.Trim();
            new McmodWindow(string.IsNullOrEmpty(q) ? null : q) { Owner = this }.ShowDialog();
        }

        private void OpenUrlSafe(string url)
        {
            try { Clipboard.SetText(url); } catch { }
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true,
                });
                StatusText.Text = "已打开浏览器（链接也复制到剪贴板）";
            }
            catch (Exception ex)
            {
                StatusText.Text = "打不开浏览器，链接已复制";
                MessageBox.Show("没调起浏览器，链接已复制到剪贴板：\n" + url + "\n\n" + ex.Message, "MC百科");
            }
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
