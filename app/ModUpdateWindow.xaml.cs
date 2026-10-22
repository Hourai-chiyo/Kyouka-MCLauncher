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
    /// Mod 识别与更新：本地 jar 算 SHA1 → Modrinth 反查 → 显示项目名/最新版本/缺依赖。
    /// </summary>
    public partial class ModUpdateWindow : Window
    {
        private readonly string _baseDir;
        private readonly string _ver;
        private readonly string _mc;
        private readonly string _loader;
        private List<ModMatch> _rows = new List<ModMatch>();

        public ModUpdateWindow(string baseDir, string ver, string mcVersion, string loader)
        {
            InitializeComponent();
            _baseDir = baseDir;
            _ver = ver;
            _mc = mcVersion;
            _loader = loader;

            HeaderText.Text = "Mod 识别与更新 · " + ver +
                              (string.IsNullOrEmpty(mcVersion) ? "" : "（" + mcVersion +
                               (string.IsNullOrEmpty(loader) ? "" : " / " + loader) + "）");
            Loaded += (s, e) => Check();
        }

        private void Check_Click(object sender, RoutedEventArgs e) { Check(); }

        private void Check()
        {
            CheckBtn.IsEnabled = false;
            UpdateBtn.IsEnabled = false;
            StatusText.Text = "正在识别（算哈希 + 反查 Modrinth）…";
            RowList.Items.Clear();

            var t = new System.Threading.Tasks.Task(() =>
            {
                string err;
                List<ModMatch> rows;
                try { rows = ModIdentity.Match(_baseDir, _ver, _mc, _loader, out err); }
                catch (Exception ex) { rows = new List<ModMatch>(); err = ex.Message; }

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    CheckBtn.IsEnabled = true;
                    _rows = rows ?? new List<ModMatch>();
                    RowList.Items.Clear();
                    foreach (var r in _rows) RowList.Items.Add(MakeRow(r));
                    if (RowList.Items.Count > 0) RowList.SelectedIndex = 0;

                    int upd = _rows.Count(r => r.HasUpdate);
                    int matched = _rows.Count(r => !string.IsNullOrEmpty(r.ProjectId));
                    int unmatched = _rows.Count - matched;
                    StatusText.Text = string.Format("共 {0} 个 · 已识别 {1} · 可更新 {2} · 未匹配 {3}",
                        _rows.Count, matched, upd, unmatched);
                    HintText.Text = string.IsNullOrEmpty(err)
                        ? "SHA1 反查 Modrinth；「未匹配」通常是自改版、CurseForge 独占或还没上传到 Modrinth。"
                        : "提示：" + err;
                }));
            });
            t.Start();
        }

        private ListBoxItem MakeRow(ModMatch r)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(95) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });

            var name = new TextBlock
            {
                Text = r.Title ?? r.Local.Name,
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            name.SetResourceReference(TextBlock.ForegroundProperty, "Text");
            if (string.IsNullOrEmpty(r.ProjectId))
                name.Opacity = 0.6;
            Grid.SetColumn(name, 0);
            grid.Children.Add(name);

            var local = new TextBlock
            {
                Text = string.IsNullOrEmpty(r.Local.Version) ? "—" : r.Local.Version,
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            local.SetResourceReference(TextBlock.ForegroundProperty, "TextSub");
            Grid.SetColumn(local, 1);
            grid.Children.Add(local);

            var latest = new TextBlock
            {
                Text = string.IsNullOrEmpty(r.LatestVersion) ? "—" : r.LatestVersion,
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            latest.SetResourceReference(TextBlock.ForegroundProperty, r.HasUpdate ? "Primary" : "TextMuted");
            Grid.SetColumn(latest, 2);
            grid.Children.Add(latest);

            var status = new TextBlock
            {
                Text = r.Status,
                FontSize = 11,
                FontWeight = r.HasUpdate ? FontWeights.SemiBold : FontWeights.Normal,
            };
            status.SetResourceReference(TextBlock.ForegroundProperty,
                r.HasUpdate ? "Primary" : (r.Status == "未匹配" ? "TextFaint" : "TextMuted"));
            Grid.SetColumn(status, 3);
            grid.Children.Add(status);

            var deps = new TextBlock
            {
                Text = r.MissingText,
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x6A, 0x00)),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(deps, 4);
            grid.Children.Add(deps);

            var item = new ListBoxItem { Content = grid, Tag = r };
            item.ToolTip = (r.Local.FilePath ?? "") + (string.IsNullOrEmpty(r.Url) ? "" : "\n" + r.Url);
            return item;
        }

        private ModMatch Selected()
        {
            var item = RowList.SelectedItem as ListBoxItem;
            return item == null ? null : item.Tag as ModMatch;
        }

        private void RowList_MouseDoubleClick(object sender, MouseButtonEventArgs e) { OpenProject(); }

        private void Update_Click(object sender, RoutedEventArgs e)
        {
            var r = Selected();
            if (r == null) { StatusText.Text = "先选一个"; return; }
            if (string.IsNullOrEmpty(r.ProjectId)) { StatusText.Text = "这个没识别出项目，不能自动更新"; return; }
            if (!r.HasUpdate) { StatusText.Text = "这个已经是最新（或无法判断）"; return; }

            UpdateBtn.IsEnabled = false;
            StatusText.Text = "正在更新 " + (r.Title ?? r.Local.Name) + " → " + r.LatestVersion + " …";

            var t = new System.Threading.Tasks.Task(() =>
            {
                string msg = null;
                bool ok = false;
                try
                {
                    ok = Modrinth.Install(_baseDir, _ver, r.ProjectId, _mc, _loader, out msg);
                }
                catch (Exception ex) { msg = ex.Message; }

                // 旧文件挪到 mods/.old/，避免两个版本同时加载
                if (ok && !string.IsNullOrEmpty(r.Local.FilePath) && File.Exists(r.Local.FilePath))
                {
                    try
                    {
                        string oldDir = Path.Combine(Path.GetDirectoryName(r.Local.FilePath), ".old");
                        Directory.CreateDirectory(oldDir);
                        string dest = Path.Combine(oldDir,
                            Path.GetFileNameWithoutExtension(r.Local.FilePath) + "-" +
                            DateTime.Now.ToString("yyyyMMddHHmmss") + ".jar.old");
                        File.Move(r.Local.FilePath, dest);
                    }
                    catch { }
                }

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    UpdateBtn.IsEnabled = true;
                    if (ok) { StatusText.Text = "更新完成：" + (r.Title ?? r.Local.Name) + "（旧文件已移到 mods/.old/）"; Check(); }
                    else StatusText.Text = "更新失败：" + (msg ?? "未知原因");
                }));
            });
            t.Start();
        }

        private void OpenProject_Click(object sender, RoutedEventArgs e) { OpenProject(); }

        private void OpenProject()
        {
            var r = Selected();
            if (r == null) { StatusText.Text = "先选一个"; return; }
            string url = r.Url;
            if (string.IsNullOrEmpty(url))
            {
                new McmodWindow(r.Local.Name) { Owner = this }.ShowDialog();
                return;
            }
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true,
                });
                StatusText.Text = "已打开：" + url;
            }
            catch
            {
                try { Clipboard.SetText(url); } catch { }
                StatusText.Text = "没调起浏览器，链接已复制";
            }
        }

        private void OpenMcmod_Click(object sender, RoutedEventArgs e)
        {
            var r = Selected();
            new McmodWindow(r == null ? null : (r.Title ?? r.Local.Name)) { Owner = this }.ShowDialog();
        }

        private void OpenModsDir_Click(object sender, RoutedEventArgs e)
        {
            string dir = Path.Combine(_baseDir, "versions", _ver, "mods");
            try
            {
                if (Directory.Exists(dir)) System.Diagnostics.Process.Start("explorer.exe", "\"" + dir + "\"");
                else { Directory.CreateDirectory(dir); System.Diagnostics.Process.Start("explorer.exe", "\"" + dir + "\""); }
            }
            catch (Exception ex) { StatusText.Text = "打不开目录：" + ex.Message; }
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }

        private void Close_Click(object sender, RoutedEventArgs e) { Close(); }
    }
}
