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
    /// Mod 管理（本地列表 + 联网识别合在一处）：
    /// 左半边看本地文件，右半边显示 Modrinth 识别结果（可更新 / 最新 / 未匹配 / 缺依赖）。
    /// </summary>
    public partial class ModsWindow : Window
    {
        private readonly string _baseDir;
        private readonly string _version;
        private readonly string _mc;
        private readonly string _loader;

        private List<ModInfo> _mods = new List<ModInfo>();
        private List<ModMatch> _matches = new List<ModMatch>();
        private bool _checking;

        public ModsWindow(string baseDir, string version)
            : this(baseDir, version, null, null)
        {
        }

        public ModsWindow(string baseDir, string version, string mcVersion, string loader)
        {
            InitializeComponent();
            _baseDir = baseDir;
            _version = version;

            if (string.IsNullOrEmpty(mcVersion) || string.IsNullOrEmpty(loader))
            {
                string mc, ld, lv;
                Version.Describe(baseDir, version, out mc, out ld, out lv);
                _mc = string.IsNullOrEmpty(mcVersion) ? mc : mcVersion;
                _loader = string.IsNullOrEmpty(loader) ? ld : loader;
            }
            else
            {
                _mc = mcVersion;
                _loader = loader;
            }

            TitleText.Text = "Mod 管理 — " + version +
                             (string.IsNullOrEmpty(_mc) ? "" : "（" + _mc +
                              (string.IsNullOrEmpty(_loader) ? "" : " / " + _loader) + "）");
            Loaded += (s, e) =>
            {
                LoadLocal();
                CheckOnline();   // 打开就顺手查一次
            };
        }

        // ---------------- 本地列表 ----------------

        private void LoadLocal()
        {
            try
            {
                _mods = Mods.Scan(_baseDir, _version);
                Rebuild();
            }
            catch (Exception ex)
            {
                MessageBox.Show("读取 mods 失败：" + ex.Message, "提示");
            }
        }

        private void Rebuild()
        {
            // InitializeComponent 期间 TextChanged/SelectionChanged 会先触发，
            // 这时 ListBox/SummaryText 还没创建，直接跳过
            if (ModList == null || SummaryText == null) return;

            ModList.Items.Clear();
            string filter = SearchBox == null ? "" : SearchBox.Text.Trim();
            string kind = FilterBox == null || FilterBox.SelectedItem == null ? "全部"
                        : ((ComboBoxItem)FilterBox.SelectedItem).Content as string;

            int shown = 0;
            foreach (var m in _mods)
            {
                var match = _matches.FirstOrDefault(x => string.Equals(x.Local.FilePath, m.FilePath,
                                                                     StringComparison.OrdinalIgnoreCase));
                if (!MatchesFilter(m, match, filter, kind)) continue;
                ModList.Items.Add(MakeItem(m, match));
                shown++;
            }

            int upd = _matches.Count(x => x.HasUpdate);
            int matched = _matches.Count(x => !string.IsNullOrEmpty(x.ProjectId));
            int on = _mods.Count(x => !x.Disabled);
            SummaryText.Text = string.Format("共 {0} · 启用 {1} · 可更新 {2} · 已识别 {3}（显示 {4}）",
                _mods.Count, on, upd, matched, shown);
            UpdateButtons();
        }

        private static bool MatchesFilter(ModInfo m, ModMatch match, string text, string kind)
        {
            if (!string.IsNullOrEmpty(text))
            {
                bool hit = (m.Name ?? "").IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0 ||
                           (m.FileName ?? "").IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0 ||
                           (match != null && (match.Title ?? "").IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0);
                if (!hit) return false;
            }

            switch (kind)
            {
                case "可更新": return match != null && match.HasUpdate;
                case "未匹配": return match == null || string.IsNullOrEmpty(match.ProjectId);
                case "已禁用": return m.Disabled;
                default: return true;
            }
        }

        private ListBoxItem MakeItem(ModInfo m, ModMatch match)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });

            // 名称：识别到就显示项目名，否则本地名；副标题放文件名
            var namePanel = new StackPanel();
            var name = new TextBlock
            {
                Text = match != null && !string.IsNullOrEmpty(match.Title) ? match.Title : m.Name,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            name.SetResourceReference(TextBlock.ForegroundProperty, "Text");
            if (match == null || string.IsNullOrEmpty(match.ProjectId)) name.Opacity = 0.65;

            var sub = new TextBlock
            {
                Text = m.FileName + " · " + m.Loader + " · " + m.SizeText(),
                FontSize = 10,
                Margin = new Thickness(0, 2, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            sub.SetResourceReference(TextBlock.ForegroundProperty, "TextFaint");

            namePanel.Children.Add(name);
            namePanel.Children.Add(sub);
            Grid.SetColumn(namePanel, 0);
            grid.Children.Add(namePanel);

            var local = new TextBlock
            {
                Text = string.IsNullOrEmpty(m.Version) ? "—" : m.Version,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            local.SetResourceReference(TextBlock.ForegroundProperty, "TextSub");
            Grid.SetColumn(local, 1);
            grid.Children.Add(local);

            var latest = new TextBlock
            {
                Text = match == null || string.IsNullOrEmpty(match.LatestVersion) ? "—" : match.LatestVersion,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            latest.SetResourceReference(TextBlock.ForegroundProperty,
                match != null && match.HasUpdate ? "Primary" : "TextMuted");
            Grid.SetColumn(latest, 2);
            grid.Children.Add(latest);

            string status = m.Disabled ? "已禁用"
                          : (match == null ? "未检查" : match.Status);
            var st = new TextBlock
            {
                Text = status,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = match != null && match.HasUpdate ? FontWeights.SemiBold : FontWeights.Normal,
            };
            st.SetResourceReference(TextBlock.ForegroundProperty,
                match != null && match.HasUpdate ? "Primary"
                : (status == "未匹配" ? "TextFaint" : "TextMuted"));
            Grid.SetColumn(st, 3);
            grid.Children.Add(st);

            var deps = new TextBlock
            {
                Text = match == null ? "" : match.MissingText,
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x6A, 0x00)),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(deps, 4);
            grid.Children.Add(deps);

            var item = new ListBoxItem { Content = grid, Tag = m };
            item.ToolTip = (match != null && !string.IsNullOrEmpty(match.Url)) ? match.Url : m.FilePath;
            return item;
        }

        private ModInfo Selected()
        {
            var item = ModList.SelectedItem as ListBoxItem;
            return item == null ? null : item.Tag as ModInfo;
        }

        private ModMatch SelectedMatch()
        {
            var m = Selected();
            if (m == null) return null;
            return _matches.FirstOrDefault(x => string.Equals(x.Local.FilePath, m.FilePath,
                                                              StringComparison.OrdinalIgnoreCase));
        }

        private void UpdateButtons()
        {
            var m = Selected();
            var match = SelectedMatch();
            if (EnableBtn != null) EnableBtn.IsEnabled = m != null && m.Disabled;
            if (DisableBtn != null) DisableBtn.IsEnabled = m != null && !m.Disabled;
            if (UpdateBtn != null) UpdateBtn.IsEnabled = match != null && match.HasUpdate;
            if (ProjectBtn != null) ProjectBtn.IsEnabled = m != null;
        }

        private void ModList_SelectionChanged(object sender, SelectionChangedEventArgs e) { UpdateButtons(); }

        // ---------------- 联网识别 ----------------

        private void CheckOnline_Click(object sender, RoutedEventArgs e) { CheckOnline(); }

        private void CheckOnline()
        {
            if (_checking) return;
            _checking = true;
            CheckBtn.IsEnabled = false;
            SummaryText.Text = "正在联网识别（算 SHA1 + 反查 Modrinth）…";

            var t = new System.Threading.Tasks.Task(() =>
            {
                List<ModMatch> rows;
                string err = null;
                try { rows = ModIdentity.Match(_baseDir, _version, _mc, _loader, out err); }
                catch (Exception ex) { rows = new List<ModMatch>(); err = ex.Message; }

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _checking = false;
                    CheckBtn.IsEnabled = true;
                    _matches = rows ?? new List<ModMatch>();
                    Rebuild();
                    if (!string.IsNullOrEmpty(err)) HintText.Text = "提示：" + err;
                }));
            });
            t.Start();
        }

        // ---------------- 操作 ----------------

        private void ModList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var m = Selected();
            if (m != null) Toggle(m);
        }

        private void Toggle(ModInfo m)
        {
            string error;
            if (!Mods.SetEnabled(m, m.Disabled, out error))
            {
                MessageBox.Show("操作失败：" + error, "提示");
                return;
            }
            LoadLocal();
        }

        private void Enable_Click(object sender, RoutedEventArgs e)
        {
            var m = Selected();
            if (m == null) return;
            string error;
            if (!Mods.SetEnabled(m, true, out error)) MessageBox.Show("启用失败：" + error, "提示");
            LoadLocal();
        }

        private void Disable_Click(object sender, RoutedEventArgs e)
        {
            var m = Selected();
            if (m == null) return;
            string error;
            if (!Mods.SetEnabled(m, false, out error)) MessageBox.Show("禁用失败：" + error, "提示");
            LoadLocal();
        }

        private void Update_Click(object sender, RoutedEventArgs e)
        {
            var m = Selected();
            var match = SelectedMatch();
            if (m == null || match == null) { HintText.Text = "先选一个已识别的 mod"; return; }
            if (!match.HasUpdate) { HintText.Text = "这个已经是最新（或无法判断）"; return; }

            UpdateBtn.IsEnabled = false;
            HintText.Text = "正在更新 " + (match.Title ?? m.Name) + " → " + match.LatestVersion + " …";

            var t = new System.Threading.Tasks.Task(() =>
            {
                string msg = null;
                bool ok = false;
                try { ok = Modrinth.Install(_baseDir, _version, match.ProjectId, _mc, _loader, out msg); }
                catch (Exception ex) { msg = ex.Message; }

                if (ok)
                {
                    // 旧文件移走，避免两个版本同时加载
                    try
                    {
                        if (!string.IsNullOrEmpty(m.FilePath) && File.Exists(m.FilePath))
                        {
                            string oldDir = Path.Combine(Path.GetDirectoryName(m.FilePath), ".old");
                            Directory.CreateDirectory(oldDir);
                            File.Move(m.FilePath, Path.Combine(oldDir,
                                Path.GetFileNameWithoutExtension(m.FilePath) + "-" +
                                DateTime.Now.ToString("yyyyMMddHHmmss") + ".jar.old"));
                        }
                    }
                    catch { }
                }

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    HintText.Text = ok ? "更新完成（旧文件已移到 mods/.old/）" : "更新失败：" + (msg ?? "未知原因");
                    LoadLocal();
                    CheckOnline();
                }));
            });
            t.Start();
        }

        private void Project_Click(object sender, RoutedEventArgs e)
        {
            var m = Selected();
            var match = SelectedMatch();
            string url = match == null ? null : match.Url;
            if (string.IsNullOrEmpty(url))
            {
                new McmodWindow(m == null ? null : m.Name) { Owner = this }.ShowDialog();
                return;
            }
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true,
                });
                HintText.Text = "已打开：" + url;
            }
            catch
            {
                try { Clipboard.SetText(url); } catch { }
                HintText.Text = "没调起浏览器，链接已复制";
            }
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            var m = Selected();
            if (m == null) return;
            var r = MessageBox.Show("确定删除这个 mod 文件？\n\n" + m.FileName + "\n（不进回收站，删了要重新下）",
                                    "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes) return;

            string error;
            if (!Mods.Delete(m, out error)) MessageBox.Show("删除失败：" + error, "提示");
            LoadLocal();
        }

        private void Refresh_Click(object sender, RoutedEventArgs e) { LoadLocal(); }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) { Rebuild(); }

        private void Filter_Changed(object sender, SelectionChangedEventArgs e) { Rebuild(); }

        private void OpenDir_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string dir = Mods.ModsDir(_baseDir, _version);
                if (!Directory.Exists(dir))
                {
                    MessageBox.Show("这个版本还没有 mods 文件夹：\n" + dir, "提示");
                    return;
                }

                IntPtr pidl = ShellOpen.ILCreateFromPath(dir);
                if (pidl != IntPtr.Zero)
                {
                    try
                    {
                        if (ShellOpen.SHOpenFolderAndSelectItems(pidl, 0, null, 0) == 0) return;
                    }
                    finally { ShellOpen.ILFree(pidl); }
                }
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = dir,
                    UseShellExecute = true,
                    Verb = "open",
                });
            }
            catch
            {
                try { new FileBrowserWindow(Mods.ModsDir(_baseDir, _version)) { Owner = this }.ShowDialog(); }
                catch (Exception ex2) { MessageBox.Show("打开失败：" + ex2.Message, "提示"); }
            }
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }

        private void Close_Click(object sender, RoutedEventArgs e) { Close(); }
    }
}
