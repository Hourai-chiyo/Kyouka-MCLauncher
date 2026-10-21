using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Mcroute.Wpf
{
    public partial class MainWindow : Window
    {
        private string _baseDir;
        private readonly List<string> _versions = new List<string>();
        private readonly object _logLock = new object();

        public MainWindow()
        {
            InitializeComponent();

            _baseDir = ResolveDataDir();
            DataDirText.Text = _baseDir;
            SettingsDataDir.Text = _baseDir;
            MemoryBox.Text = LoadConfig("memory", "2G");

            Http.Progress = OnHttpProgress;

            Loaded += (s, e) =>
            {
                RefreshVersions();
                RefreshJava();
                Mirrors.ContextBaseDir = _baseDir;
                Mirrors.Invalidate();
                RefreshMirrors();
                LoadBackground();
                try
                {
                    var asm = typeof(MainWindow).Assembly;
                    var attr = asm.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                                  .FirstOrDefault() as System.Reflection.AssemblyInformationalVersionAttribute;
                    string shown = attr != null ? attr.InformationalVersion : asm.GetName().Version.ToString(3);
                    int plus = shown.IndexOf('+');
                    if (plus > 0) shown = shown.Substring(0, plus);
                    VersionText.Text = "v" + shown + " · WPF";
                    if (SideVersion != null) SideVersion.Text = "v" + shown;
                }
                catch { }

                AppendLog("数据目录：" + _baseDir);
                try
                {
                    var id = System.Security.Principal.WindowsIdentity.GetCurrent();
                    bool admin = new System.Security.Principal.WindowsPrincipal(id)
                        .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                    AppendLog("进程自检：用户=" + id.Name + "  管理员=" + admin +
                              "  会话=" + System.Diagnostics.Process.GetCurrentProcess().SessionId);
                }
                catch { }
                // 数据目录已经在 ResolveDataDir 里自动挑好了：
                //   · 有版本 -> 直接用（并写进配置，下次不再问）
                //   · 完全没版本 + 没配置 -> 才引导选目录
                if (!App.ShotMode) RestoreWindowState();

                try
                {
                    // 先把配置读进内存：设 ThemeBox 会触发 SelectionChanged，
                    // 那里会 SaveConfig，如果先设控件就会把还没读的 key 覆盖掉。
                    var cfg = LoadConfig();
                    string theme = J.Str(cfg, "theme", "auto");
                    string forced = Environment.GetEnvironmentVariable("MCROUTE_THEME");
                    if (!string.IsNullOrEmpty(forced)) theme = forced;   // 截图/调试用
                    int glass = J.Int(cfg, "glass", 0);

                    _initializing = true;
                    try { ThemeBox.SelectedIndex = theme == "dark" ? 2 : theme == "light" ? 1 : 0; }
                    finally { _initializing = false; }

                    Theme.Apply(theme);
                    ApplyGlass(glass == 1, true);

                    int bgAll = J.Int(cfg, "bg_all", 0);
                    _initializing = true;
                    try { if (BgAllPagesBox != null) BgAllPagesBox.IsChecked = bgAll == 1; }
                    finally { _initializing = false; }
                    ApplyBackgroundScope(bgAll == 1);

                    _initializing = true;
                    try
                    {
                        bool parallel = J.Int(cfg, "parallel", 0) == 1;
                        if (ParallelBox != null) ParallelBox.IsChecked = parallel;
                        Http.AllowParallel = parallel;
                    }
                    finally { _initializing = false; }
                }
                catch { }

                if (!App.ShotMode && string.IsNullOrEmpty(LoadConfigDir()) && _versions.Count > 0)
                {
                    SaveConfig();
                    AppendLog("已自动使用数据目录（以后不用再选）：" + _baseDir);
                }
                else if (!App.ShotMode && string.IsNullOrEmpty(LoadConfigDir()) && _versions.Count == 0)
                {
                    PickDataDir_Click(this, new RoutedEventArgs());
                }
                else if (_versions.Count == 0)
                {
                    ShowFirstRunGuide();
                }
            };
        }

        /// <summary>
        /// 选数据目录。顺序：
        ///   1) 配置文件里记住的
        ///   2) 已经装了版本的数据目录（exe 旁 / %LOCALAPPDATA%）
        ///   3) 便携模式：exe 旁边本来就有 mcdata 文件夹
        ///   4) 用户目录 %LOCALAPPDATA%\mcroute\mcdata
        /// </summary>
        private static string ResolveDataDir()
        {
            // 截图/自动化用：MCROUTE_DATA 直接指定
            string fromEnv = Environment.GetEnvironmentVariable("MCROUTE_DATA");
            if (!string.IsNullOrEmpty(fromEnv) && Directory.Exists(fromEnv)) return fromEnv;

            string fromConfig = LoadConfigDir();
            if (!string.IsNullOrEmpty(fromConfig) && Directory.Exists(fromConfig)) return fromConfig;

            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            string exeMc = Path.Combine(exeDir, "mcdata");
            string userMc = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "mcroute", "mcdata");

            foreach (string candidate in new[] { exeMc, userMc })
                if (Directory.Exists(Path.Combine(candidate, "versions"))) return candidate;

            // 便携版：exe 旁边已经有 mcdata 文件夹就用它
            if (Directory.Exists(exeMc)) return exeMc;

            // 否则用用户目录（不污染 exe 所在文件夹）
            try { Directory.CreateDirectory(userMc); return userMc; }
            catch { return Version.BaseDir(null); }
        }

        // ================= 配置 =================
        /// <summary>
        /// 配置写入位置（按顺序尝试）：%LOCALAPPDATA% → exe 旁 → %TEMP%。
        /// 受限环境（沙箱/只读目录）下前面的写不了就退到后面，避免设置全部丢失。
        /// </summary>
        private static string[] ConfigPaths()
        {
            return new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "mcroute", "wpf.json"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wpf.json"),
                Path.Combine(Path.GetTempPath(), "mcroute-wpf.json"),
            };
        }

        private static Dictionary<string, object> LoadConfig()
        {
            try
            {
                foreach (string p in ConfigPaths())
                {
                    if (!File.Exists(p)) continue;
                    return J.Obj(J.ParseFile(p)) ?? new Dictionary<string, object>();
                }
                return new Dictionary<string, object>();
            }
            catch { return new Dictionary<string, object>(); }
        }

        private static string LoadConfigDir()
        {
            try
            {
                string d = J.Str(LoadConfig(), "data_dir");
                return string.IsNullOrEmpty(d) ? null : d;
            }
            catch { return null; }
        }

        private static string LoadConfig(string key, string fallback)
        {
            try
            {
                string v = J.Str(LoadConfig(), key);
                return string.IsNullOrEmpty(v) ? fallback : v;
            }
            catch { return fallback; }
        }

        private void SaveConfig()
        {
            try
            {
                var bounds = WindowState == WindowState.Normal
                    ? new Rect(Left, Top, Width, Height)
                    : RestoreBounds;
                var data = LoadConfig();          // 保留已有键（bg_dim / bg_blur 等）
                if (data == null) data = new Dictionary<string, object>();
                data["data_dir"] = _baseDir;
                data["memory"] = MemoryBox.Text.Trim();
                data["win_w"] = (int)Math.Round(bounds.Width);
                data["win_h"] = (int)Math.Round(bounds.Height);
                data["win_l"] = (int)Math.Round(bounds.Left);
                data["win_t"] = (int)Math.Round(bounds.Top);
                data["page"] = _page;
                data["theme"] = CurrentTheme();
                data["glass"] = (GlassBox != null && GlassBox.IsChecked == true) ? 1 : 0;
                data["bg_all"] = (BgAllPagesBox != null && BgAllPagesBox.IsChecked == true) ? 1 : 0;
                data["parallel"] = (ParallelBox != null && ParallelBox.IsChecked == true) ? 1 : 0;

                foreach (string cfgPath in ConfigPaths())
                {
                    try
                    {
                        string dir = Path.GetDirectoryName(cfgPath);
                        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                        J.SaveFile(cfgPath, data);
                        return;      // 写成功一个就够了
                    }
                    catch { }
                }
            }
            catch { }
        }

        /// <summary>应用内文件浏览（系统 shell 不可用时的兜底）。</summary>
        private void OpenBrowser(string dir)
        {
            try
            {
                var w = new FileBrowserWindow(dir) { Owner = this };
                w.ShowDialog();
            }
            catch (Exception ex)
            {
                MessageBox.Show("连内置浏览也失败了：" + ex.Message + "\n\n路径已复制到剪贴板。\n" + dir, "提示");
            }
        }

        /// <summary>给截图/脚本用：切到指定页（0 启动 / 1 下载 / 2 整合包 / 3 设置）。</summary>
        public void SetPage(int index)
        {
            switch (index)
            {
                case 0: NavLaunch.IsChecked = true; break;
                case 1: NavDownload.IsChecked = true; break;
                case 2: NavPacks.IsChecked = true; break;
                case 3: NavManage.IsChecked = true; break;
                case 4: NavSettings.IsChecked = true; break;
            }
            UpdateLayout();
        }

        // ================= 窗口按钮 =================
        private void Min_Click(object sender, RoutedEventArgs e) { WindowState = WindowState.Minimized; }
        private void Max_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }
        private void Close_Click(object sender, RoutedEventArgs e) { SaveConfig(); Close(); }

        // ================= 导航 =================
        private void Nav_Checked(object sender, RoutedEventArgs e)
        {
            if (PageLaunch == null) return;

            PageLaunch.Visibility = Visibility.Collapsed;
            PageDownload.Visibility = Visibility.Collapsed;
            PagePacks.Visibility = Visibility.Collapsed;
            PageManage.Visibility = Visibility.Collapsed;
            PageSettings.Visibility = Visibility.Collapsed;

            var rb = sender as RadioButton;
            string name = rb == null ? "启动" : (rb.Content as string ?? "启动");
            switch (name)
            {
                case "启动": PageLaunch.Visibility = Visibility.Visible; break;
                case "下载": PageDownload.Visibility = Visibility.Visible; break;
                case "整合包": PagePacks.Visibility = Visibility.Visible; break;
                case "管理": PageManage.Visibility = Visibility.Visible; RefreshManageList(); break;
                case "设置": PageSettings.Visibility = Visibility.Visible; RefreshJava(); break;
            }

            _page = name == "启动" ? 0 : name == "下载" ? 1 : name == "整合包" ? 2 : name == "管理" ? 3 : 4;
            if (PageTitle != null) PageTitle.Text = name;
            StatusText.Text = "就绪 · " + name + " 页";
        }

        /// <summary>首次启动（数据目录里没有版本）时引导用户选已有目录。</summary>
        private void ShowFirstRunGuide()
        {
            var r = MessageBox.Show(
                "当前数据目录里还没有任何版本：\n" + _baseDir + "\n\n" +
                "如果以前用过别的启动器（PCL / HMCL / 官方启动器 / mcroute 另一个版本），\n" +
                "可以「是」把它的数据目录导进来，不用重新下载。\n\n" +
                "点「否」就留在这里，用「下载」页装一个新版本。",
                "mcroute — 第一次使用", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (r == MessageBoxResult.Yes) PickDataDir_Click(this, new RoutedEventArgs());
        }

        // ================= 版本列表 =================
        private void Refresh_Click(object sender, RoutedEventArgs e) { RefreshVersions(); }

        private void VersionSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (VersionSearch == null) return;
            string q = VersionSearch.Text ?? "";
            if (SearchHint != null)
                SearchHint.Visibility = string.IsNullOrEmpty(q) ? Visibility.Visible : Visibility.Collapsed;
            RefreshVersions(q);
        }

        private void RefreshVersions(string filter = null)
        {
            string q = (filter ?? "").Trim().ToLowerInvariant();
            _versions.Clear();
            VersionList.Items.Clear();

            string root = Path.Combine(_baseDir, "versions");
            if (!Directory.Exists(root))
            {
                AppendLog("数据目录里没有 versions 文件夹：" + root);
                VersionCount.Text = "0 个";
                return;
            }

            foreach (string dir in Directory.GetDirectories(root).OrderBy(x => Path.GetFileName(x)))
            {
                string name = Path.GetFileName(dir);
                // 过滤掉 versions 下的杂项目录（官方启动器会塞 libraries 之类的东西）
                string jsonPath = Path.Combine(dir, name + ".json");
                bool isPack = File.Exists(Path.Combine(dir, "instance.json"));
                bool hasJson = File.Exists(jsonPath);
                if (!hasJson && !isPack && !Directory.Exists(Path.Combine(dir, "mods")))
                    continue;
                bool hasMods = Directory.Exists(Path.Combine(dir, "mods"));

                string mc, loader, loaderVer;
                string tag = Version.Describe(_baseDir, name, out mc, out loader, out loaderVer);
                if (isPack) tag += " · 整合包";
                else if (hasMods && string.IsNullOrEmpty(loader)) tag += " · 外部";

                if (q.Length > 0 &&
                    name.ToLowerInvariant().IndexOf(q, StringComparison.Ordinal) < 0 &&
                    tag.ToLowerInvariant().IndexOf(q, StringComparison.Ordinal) < 0)
                    continue;

                _versions.Add(name);
                VersionList.Items.Add(MakeVersionItem(name, tag));
            }

            VersionCount.Text = _versions.Count + " 个";
            if (VersionList.Items.Count > 0) VersionList.SelectedIndex = 0;
            AppendLog(q.Length > 0
                ? "搜索「" + filter + "」→ " + _versions.Count + " 个"
                : "找到 " + _versions.Count + " 个本地版本");
        }

        private static ListBoxItem MakeVersionItem(string name, string tag)
        {
            var panel = new StackPanel();

            var title = new TextBlock
            {
                Text = name,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            // 用资源引用而不是抓当前画刷：切主题时会自动跟着变
            title.SetResourceReference(TextBlock.ForegroundProperty, "Text");

            var sub = new TextBlock
            {
                Text = tag,
                FontSize = 11,
                Margin = new Thickness(0, 3, 0, 0),
            };
            sub.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");

            panel.Children.Add(title);
            panel.Children.Add(sub);
            return new ListBoxItem { Content = panel, Tag = name };
        }

        private void VersionList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (SelectedVersion() != null) Play_Click(sender, new RoutedEventArgs());
        }

        private void VersionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CardDetail == null) return;
            string name = SelectedVersion();
            if (name == null) return;
            CardDetail.Text = name + " · " + MemoryBox.Text.Trim() + " 内存";
            StatusText.Text = "已选择：" + name;
        }

        private string SelectedVersion()
        {
            var item = VersionList.SelectedItem as ListBoxItem;
            return item == null ? null : (string)item.Tag;
        }

        // ================= 启动 =================
        private void Play_Click(object sender, RoutedEventArgs e)
        {
            string ver = SelectedVersion();
            if (ver == null) { MessageBox.Show("先在左边选一个版本", "提示"); return; }
            if (_running) { MessageBox.Show("还有任务在跑，等它结束", "提示"); return; }
            if (_gameRunning) { MessageBox.Show("游戏已经在运行了", "提示"); return; }

            // 启动前预检：先把「肯定起不来」的情况显示在界面上，而不是弹窗一闪而过
            try
            {
                var plan = Commands.PlanLaunch(_baseDir, ver, null, null);
                if (plan == null)
                {
                    CardTitle.Text = "启动失败";
                    CardDetail.Text = "版本文件缺失或损坏：versions\\" + ver + "\\" + ver + ".json";
                    StatusText.Text = "就绪 · 启动失败（版本文件有问题）";
                    AppendLog("[启动失败] 无法解析版本：" + ver);
                    return;
                }
                if (plan.Missing.Count > 0)
                {
                    CardTitle.Text = "缺少 " + plan.Missing.Count + " 个文件";
                    CardDetail.Text = string.Join("、", plan.Missing.Take(3)) +
                                      (plan.Missing.Count > 3 ? " 等" : "") + " —— 请到「下载」页补齐";
                    StatusText.Text = "就绪 · 缺少依赖文件";
                    AppendLog("[启动失败] 缺 " + plan.Missing.Count + " 个文件，先下载或体检补齐");
                    return;
                }
            }
            catch (Exception ex)
            {
                AppendLog("[预检异常] " + ex.Message);
            }

            string mem = MemoryBox.Text.Trim();
            CardTitle.Text = "正在启动";
            CardDetail.Text = ver + " · " + mem + " 内存";
            SetProgress(0);

            // 启动成功后 Core 会把进程回调回来，这里跟踪"运行中 / 已退出"
            Commands.OnProcessStarted = proc =>
            {
                proc.Exited += (s2, e2) =>
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        _gameRunning = false;
                        PlayButton.IsEnabled = true;
                        int code = -1;
                        try { code = proc.ExitCode; } catch { }
                        CardTitle.Text = "游戏已退出";
                        CardDetail.Text = "退出码 " + code + " · 日志：logs\\mcroute-launch.log";
                        StatusText.Text = "就绪 · 游戏已退出";
                    }));
                };

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _gameRunning = true;
                    PlayButton.IsEnabled = false;
                    CardTitle.Text = "游戏运行中";
                    CardDetail.Text = "PID " + proc.Id + " · 关掉游戏后这里会自动更新";
                    StatusText.Text = "游戏运行中（PID " + proc.Id + "）";
                }));
            };

            RunTask("启动 " + ver, () =>
                Commands.Launch(new[] { "launch", ver, "--dir", _baseDir, "--memory", mem }));
        }

        /// <summary>恢复上次的窗口大小/位置/页签（越界时夹回屏幕内）。</summary>
        private void RestoreWindowState()
        {
            try
            {
                var cfg = LoadConfig();
                int w = J.Int(cfg, "win_w", 0), h = J.Int(cfg, "win_h", 0);
                int l = J.Int(cfg, "win_l", 0), t = J.Int(cfg, "win_t", 0);

                if (w >= 900 && h >= 600)
                {
                    var wa = SystemParameters.WorkArea;
                    Left = Math.Max(wa.Left - 8, Math.Min(l, wa.Right - 240));
                    Top = Math.Max(wa.Top - 8, Math.Min(t, wa.Bottom - 160));
                    Width = Math.Min(w, wa.Width);
                    Height = Math.Min(h, wa.Height);
                }

                int page = J.Int(cfg, "page", 0);
                if (page > 0 && page <= 4) SetPage(page);
            }
            catch { }
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            FollowSystemTheme();
        }

        private void FollowSystemTheme()
        {
            try
            {
                if (CurrentTheme() != "auto") return;

                // 关键：只有系统主题"真的变了"才重刷调色板。
                // 否则每次窗口获得焦点都 Theme.Apply 一遍，会把玻璃面板的覆盖层冲掉
                //（表现：切出去截图再回来，卡片又变回纯白）。
                bool systemDark = Theme.IsSystemDark();
                bool currentDark = Theme.Effective == "dark";
                if (systemDark == currentDark) return;

                Theme.Apply("auto");
                ApplyGlass(_glassOn, false);     // 重刷后把玻璃盖回去
                AppendLog("系统主题变了 → " + (systemDark ? "深色" : "浅色"));
            }
            catch { }
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            try { SaveConfig(); } catch { }
            base.OnClosing(e);
        }

        // ================= 取消 =================
        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_running) return;
            Http.CancelRequested = true;
            _cancelReason = "已取消 · 临时文件已清理";
            StatusText.Text = "正在取消…（当前文件会中断并删除临时文件）";
        }

        // ================= 下载 / 加载器 / Java =================
        private void Fetch_Click(object sender, RoutedEventArgs e) { Fetch(false); }
        private void FetchNoAssets_Click(object sender, RoutedEventArgs e) { Fetch(true); }

        private void Fetch(bool noAssets)
        {
            string ver = DownloadVersion.Text.Trim();
            if (ver.Length == 0) { MessageBox.Show("先填版本号，例如 1.20.1", "提示"); return; }
            var args = noAssets
                ? new[] { "fetch", ver, "--dir", _baseDir, "--no-assets" }
                : new[] { "fetch", ver, "--dir", _baseDir };
            RunTask("下载 " + ver, () => Commands.Fetch(args), () => RefreshVersions());
        }

        private void Loader_Click(object sender, RoutedEventArgs e)
        {
            string kind = (LoaderKind.SelectedItem as ComboBoxItem)?.Content as string ?? "fabric";
            string ver = LoaderVersion.Text.Trim();
            if (ver.Length == 0) { MessageBox.Show("先填版本号", "提示"); return; }
            RunTask("安装 " + kind + " " + ver,
                () => Loader.Run(new[] { "loader", kind, ver, "--dir", _baseDir }), () => RefreshVersions());
        }

        private void InstallJava_Click(object sender, RoutedEventArgs e)
        {
            string majorText = (JavaMajor.SelectedItem as ComboBoxItem)?.Content as string ?? "17";
            int major = int.Parse(majorText);
            RunTask("安装 Java " + major, () =>
            {
                string exe = JavaSources.Install(major, _baseDir);
                return exe == null ? 1 : 0;
            }, RefreshJava);
        }

        // ================= 整合包 =================
        private void BrowsePack_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "整合包|*.mrpack;*.zip|所有文件|*.*",
                Title = "选择整合包",
            };
            if (dlg.ShowDialog() == true) PackPath.Text = dlg.FileName;
        }

        private void InstallPack_Click(object sender, RoutedEventArgs e)
        {
            string path = PackPath.Text.Trim();
            if (path.Length == 0) { MessageBox.Show("先选一个整合包文件", "提示"); return; }
            RunTask("安装 " + Path.GetFileName(path),
                () => Packs.Install(new[] { "install", path, "--dir", _baseDir }), () => RefreshVersions());
        }

        // ================= 管理页 =================
        private void RefreshManage_Click(object sender, RoutedEventArgs e) { RefreshManageList(); }

        private void RefreshManageList()
        {
            ManageList.Items.Clear();
            string root = Path.Combine(_baseDir, "versions");
            if (!Directory.Exists(root)) { ManageCount.Text = "0 个"; return; }

            foreach (string dir in Directory.GetDirectories(root).OrderBy(x => Path.GetFileName(x)))
            {
                string name = Path.GetFileName(dir);
                string jsonPath = Path.Combine(dir, name + ".json");
                if (!File.Exists(jsonPath) &&
                    !File.Exists(Path.Combine(dir, "instance.json")) &&
                    !Directory.Exists(Path.Combine(dir, "mods")))
                    continue;

                string mc, loader, loaderVer;
                string tag = Version.Describe(_baseDir, name, out mc, out loader, out loaderVer);
                if (File.Exists(Path.Combine(dir, "instance.json"))) tag += " · 整合包";
                ManageList.Items.Add(MakeVersionItem(name, tag));
            }
            ManageCount.Text = ManageList.Items.Count + " 个";
            if (ManageList.Items.Count > 0) ManageList.SelectedIndex = 0;
        }

        private string SelectedManageVersion()
        {
            var item = ManageList.SelectedItem as ListBoxItem;
            return item == null ? null : (string)item.Tag;
        }

        private void Verify_Click(object sender, RoutedEventArgs e) { RunManage("verify"); }
        private void Test_Click(object sender, RoutedEventArgs e) { RunManage("test"); }
        private void Mkbat_Click(object sender, RoutedEventArgs e) { RunManage("mkbat"); }

        private void RunManage(string cmd)
        {
            string ver = SelectedManageVersion();
            if (ver == null) { MessageBox.Show("先在列表里选一个版本", "提示"); return; }

            string title = cmd == "verify" ? "体检 " + ver
                         : cmd == "test" ? "测试启动 " + ver
                         : "生成启动器 " + ver;
            RunTask(title, () =>
            {
                if (cmd == "verify") return ExtraCommands.Verify(new[] { "verify", ver, "--dir", _baseDir });
                if (cmd == "test") return ExtraCommands.Test(new[] { "test", ver, "--dir", _baseDir });
                return ExtraCommands.Mkbat(new[] { "mkbat", ver, "--dir", _baseDir });
            });
        }

        private void OpenModsBrowser_Click(object sender, RoutedEventArgs e)
        {
            string ver = SelectedManageVersion();
            if (ver == null) { MessageBox.Show("先在列表里选一个版本", "提示"); return; }
            try
            {
                string mc, loader, loaderVer;
                Version.Describe(_baseDir, ver, out mc, out loader, out loaderVer);
                var w = new ModsBrowserWindow(_baseDir, ver, mc, loader) { Owner = this };
                w.ShowDialog();
            }
            catch (Exception ex) { MessageBox.Show("打开失败：" + ex.Message, "提示"); }
        }

        private void OpenMods_Click(object sender, RoutedEventArgs e)
        {
            string ver = SelectedManageVersion();
            if (ver == null) { MessageBox.Show("先在列表里选一个版本", "提示"); return; }
            try
            {
                var w = new ModsWindow(_baseDir, ver) { Owner = this };
                w.ShowDialog();
            }
            catch (Exception ex) { MessageBox.Show("打开失败：" + ex.Message, "提示"); }
        }

        private void CopyManagePath_Click(object sender, RoutedEventArgs e)
        {
            string ver = SelectedManageVersion();
            string dir = ver == null ? _baseDir : Path.Combine(_baseDir, "versions", ver);
            if (!Directory.Exists(dir)) dir = _baseDir;
            try
            {
                Clipboard.SetText(dir);
                StatusText.Text = "路径已复制：" + dir;
            }
            catch (Exception ex) { MessageBox.Show("复制失败：" + ex.Message, "提示"); }
        }

        private void OpenManageDir_Click(object sender, RoutedEventArgs e)
        {
            string ver = SelectedManageVersion();
            string dir = ver == null ? _baseDir : Path.Combine(_baseDir, "versions", ver);
            if (!Directory.Exists(dir)) dir = _baseDir;
            OpenFolderSafe(dir);
        }

        // ================= 设置 =================
        private void PickDataDir_Click(object sender, RoutedEventArgs e)
        {
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog())
            {
                dlg.Description = "选择数据目录（包含 versions / libraries / assets 的那一层）";
                dlg.SelectedPath = _baseDir;
                if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

                string chosen = dlg.SelectedPath;
                if (!Directory.Exists(Path.Combine(chosen, "versions")) &&
                    Directory.Exists(Path.Combine(chosen, "mcdata", "versions")))
                    chosen = Path.Combine(chosen, "mcdata");
                if (!Directory.Exists(Path.Combine(chosen, "versions")))
                {
                    MessageBox.Show("这个目录里没有 versions 文件夹，看起来不是启动器的数据目录。", "提示");
                    return;
                }

                _baseDir = chosen;
                DataDirText.Text = chosen;
                SettingsDataDir.Text = chosen;
                SaveConfig();
                RefreshVersions();
                AppendLog("已切换数据目录：" + chosen);
            }
        }

        private void OpenDir_Click(object sender, RoutedEventArgs e)
        {
            string ver = SelectedVersion();
            string dir = ver == null
                ? _baseDir
                : Path.Combine(_baseDir, "versions", ver);
            if (!Directory.Exists(dir)) dir = _baseDir;
            OpenFolderSafe(dir);
        }

        private void RefreshJava_Click(object sender, RoutedEventArgs e) { RefreshJava(); }

        // ================= 分段下载开关 =================
        private void ParallelBox_Changed(object sender, RoutedEventArgs e)
        {
            if (ParallelBox == null) return;
            bool on = ParallelBox.IsChecked == true;
            Http.AllowParallel = on;
            try { SaveConfig(); } catch { }
            AppendLog("分段下载：" + (on ? "开（实验性）" : "关"));
        }

        // ================= 下载源 =================
        private void RefreshMirrors()
        {
            try
            {
                var list = Mirrors.Current();
                var items = new List<string>();
                foreach (var m in list.OrderByDescending(x => x.SpeedMbps))
                {
                    string speed = m.SpeedMbps > 0 ? m.SpeedMbps.ToString("F2") + " MB/s" : "未测速";
                    items.Add(string.Format("{0,-16} {1,-10} {2}{3}",
                        m.Name, speed, m.Normalized(), m.Enabled ? "" : "（已禁用）"));
                }
                MirrorList.ItemsSource = items;
            }
            catch (Exception ex) { AppendLog("读取镜像配置失败：" + ex.Message); }
        }

        private void MirrorSpeedTest_Click(object sender, RoutedEventArgs e)
        {
            RunTask("镜像测速", () =>
            {
                var list = Mirrors.Current();
                foreach (var m in list)
                {
                    if (!m.Enabled) continue;
                    double mbps = Mirrors.SpeedTest(m);
                    AppendLog(string.Format("  {0}：{1}", m.Name,
                        m.LastOk ? mbps.ToString("F2") + " MB/s" : "不可用"));
                }
                Mirrors.Save(_baseDir, list);
                Mirrors.Invalidate();
                return 0;
            }, () => { RefreshMirrors(); AppendLog("镜像测速完成（已按速度排序，下载时优先用快的）"); });
        }

        private void OpenMirrorsCfg_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string cfgDir = Path.GetDirectoryName(Mirrors.ConfigPath(_baseDir));
                if (!Directory.Exists(cfgDir)) Directory.CreateDirectory(cfgDir);
                Mirrors.Load(_baseDir);            // 确保文件存在
                OpenFolderSafe(cfgDir);
            }
            catch (Exception ex) { MessageBox.Show("打开失败：" + ex.Message, "提示"); }
        }

        private void RefreshJava()
        {
            JavaList.Items.Clear();
            foreach (string root in new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Eclipse Adoptium"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Java"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft", "runtime"),
                Path.Combine(_baseDir, "java_versions"),
            })
            {
                if (!Directory.Exists(root)) continue;
                foreach (string sub in Directory.GetDirectories(root))
                {
                    string exe = Path.Combine(sub, "bin", "java.exe");
                    if (!File.Exists(exe)) continue;
                    int major = Java.Major(exe);
                    if (major <= 0) continue;
                    JavaList.Items.Add("Java " + major + "  " + exe);
                }
            }
            AppendLog("检测到 " + JavaList.Items.Count + " 个 Java");
        }

        // ================= 主题 =================
        private void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_initializing) return;
            try
            {
                var item = ThemeBox.SelectedItem as ComboBoxItem;
                if (item == null) return;
                string mode = (item.Tag as string) ?? "auto";
                Theme.Apply(mode);
                ApplyGlass(_glassOn, false);
                SaveConfig();
                AppendLog("主题：" + (mode == "auto" ? "跟随系统（当前 " + (Theme.Effective == "dark" ? "深色" : "浅色") + "）"
                                        : mode == "dark" ? "深色" : "浅色"));
            }
            catch { }
        }

        private string CurrentTheme()
        {
            try
            {
                var item = ThemeBox.SelectedItem as ComboBoxItem;
                return item == null ? "auto" : ((item.Tag as string) ?? "auto");
            }
            catch { return "auto"; }
        }

        // ================= 背景图显示范围 =================
        private bool _bgAll;

        /// <summary>非玻璃模式下页面底色：仅启动页=实色；所有页面=半透明。</summary>
        private void ApplyBackgroundScope(bool all)
        {
            _bgAll = all;
            if (_glassOn) return;
            try
            {
                bool dark = Theme.Effective == "dark";
                string hex = all ? (dark ? "#D910141A" : "#D9F7F8FA")
                                 : (dark ? "#10141A" : "#F7F8FA");
                var res = Application.Current.Resources;
                if (res != null) res["PageBg"] = Brush(hex);
            }
            catch { }
        }

        private void BgAllPages_Changed(object sender, RoutedEventArgs e)
        {
            if (BgAllPagesBox == null) return;
            ApplyBackgroundScope(BgAllPagesBox.IsChecked == true);
            try { SaveConfig(); } catch { }
        }

        // ================= 玻璃面板 =================
        private bool _glassOn;

        /// <summary>
        /// 玻璃面板：页面背景改透明（露出背景图），卡片画刷换成半透明。
        /// 关掉时恢复标准调色板。
        /// </summary>
        private void ApplyGlass(bool on, bool updateBox)
        {
            _glassOn = on;
            try
            {
                var res = Application.Current.Resources;
                Theme.Apply(CurrentTheme());        // 先把调色板重置成标准色

                if (on)
                {
                    if (res["SurfaceGlass"] != null) res["Surface"] = res["SurfaceGlass"];
                    if (res["BorderGlass"] != null) res["Border"] = res["BorderGlass"];
                    if (res["InputBgGlass"] != null) res["InputBg"] = res["InputBgGlass"];

                    // 输入框：完全透明 + 细描边（窗口 11 / Notion 那种做法）
                    // 之前是 "卡片 60% 白 + 输入框 60% 白 = 84% 白"，会白得像贴纸
                    bool dark = Theme.Effective == "dark";
                    res["FieldBg"] = Brush("#00FFFFFF");
                    res["FieldBorder"] = Brush(dark ? "#40FFFFFF" : "#33000000");
                    if (Sidebar != null) Sidebar.Background = res["SurfaceGlass"] as Brush;
                    if (TopBar != null) TopBar.Background = System.Windows.Media.Brushes.Transparent;
                    // 页面底色统一走 PageBg：玻璃 = 全透明（露出背景图）
                    res["PageBg"] = System.Windows.Media.Brushes.Transparent;
                }
                else
                {
                    ApplyBackgroundScope(_bgAll);   // 关掉玻璃后按"显示范围"设置恢复
                    if (Sidebar != null) Sidebar.SetResourceReference(Border.BackgroundProperty, "Surface");
                    res["FieldBg"] = Brush(Theme.Effective == "dark" ? "#171C24" : "#FFFFFF");
                    res["FieldBorder"] = Brush(Theme.Effective == "dark" ? "#28303C" : "#E4E7EC");
                    if (TopBar != null) TopBar.SetResourceReference(Panel.BackgroundProperty, "Surface");
                }

                if (updateBox && GlassBox != null) GlassBox.IsChecked = on;
            }
            catch (Exception ex) { AppendLog("玻璃面板切换失败：" + ex.Message); }
        }

        private void GlassBox_Changed(object sender, RoutedEventArgs e)
        {
            if (GlassBox == null) return;
            ApplyGlass(GlassBox.IsChecked == true, false);
            try { SaveConfig(); } catch { }
            AppendLog("玻璃面板：" + (GlassBox.IsChecked == true ? "开" : "关"));
        }

        private static Brush Brush(string hex)
        {
            try
            {
                var c = (Color)ColorConverter.ConvertFromString(hex);
                var b = new SolidColorBrush(c);
                b.Freeze();
                return b;
            }
            catch { return Brushes.Transparent; }
        }

        // ================= 启动页背景 =================
        private string BackgroundFile()
        {
            return Path.Combine(_baseDir, "background.img");
        }

        private void LoadBackground()
        {
            try
            {
                string file = BackgroundFile();
                if (File.Exists(file))
                {
                    ApplyBackgroundBrush(file);
                    SetBgPathText(file);
                }
                else
                {
                    BgPathText.Text = "（默认矢量背景）";
                }
            }
            catch (Exception ex) { AppendLog("加载背景失败：" + ex.Message); }
        }

        private void PickBackground_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "图片|*.jpg;*.jpeg;*.png;*.bmp;*.gif|所有文件|*.*",
                Title = "选择启动页背景图",
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                Directory.CreateDirectory(_baseDir);
                string target = BackgroundFile();
                File.Copy(dlg.FileName, target, true);
                ApplyBackgroundBrush(target);
                SetBgPathText(dlg.FileName);      // 显示原图名，而不是内部 background.img
                ApplyBackgroundScope(_bgAll);     // 重新应用显示范围
                AppendLog("已设置背景图：" + dlg.FileName);

                // 设置页看不到背景 —— 自动切回启动页，让用户立刻看到效果
                if (NavLaunch != null) NavLaunch.IsChecked = true;
                CardTitle.Text = "背景已更新";
                CardDetail.Text = System.IO.Path.GetFileName(dlg.FileName) + " · 暗度 " +
                                  (int)Math.Round(BgDimSlider.Value) + "% · 模糊 " + (int)Math.Round(BgBlurSlider.Value);
            }
            catch (Exception ex)
            {
                MessageBox.Show("设置背景图失败：" + ex.Message, "提示");
            }
        }

        private void ResetBackground_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string file = BackgroundFile();
                if (File.Exists(file)) File.Delete(file);
                HeroImageLayer.Background = null;
                HeroImageLayer.Effect = null;
                HeroImageLayer.Visibility = Visibility.Collapsed;
                HeroScrim.Visibility = Visibility.Collapsed;
                HeroMountains.Visibility = Visibility.Visible;
                SetBgPathText(null);
                AppendLog("已恢复默认背景");
            }
            catch (Exception ex)
            {
                MessageBox.Show("恢复默认失败：" + ex.Message, "提示");
            }
        }

        private bool _applyingBg;          // 防止滑块赋值时递归触发保存

        /// <summary>背景框只显示文件名（完整路径放 ToolTip），避免长路径被截断。</summary>
        private void SetBgPathText(string fileOrEmpty)
        {
            if (BgPathText == null) return;
            if (string.IsNullOrEmpty(fileOrEmpty))
            {
                BgPathText.Text = "（默认矢量背景）";
                BgPathText.ToolTip = null;
                return;
            }
            try
            {
                BgPathText.Text = System.IO.Path.GetFileName(fileOrEmpty);
                BgPathText.ToolTip = fileOrEmpty;
            }
            catch { BgPathText.Text = fileOrEmpty; }
            BgPathText.CaretIndex = 0;
            BgPathText.ScrollToHome();
        }

        private void ApplyBackgroundBrush(string file)
        {
            // 关键：背景图总是复制到同一个路径（background.img），
            // WPF 会按 Uri 缓存图片 → 换图后还是显示旧图。
            // IgnoreImageCache + OnLoad 强制重新解码。
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.CreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreImageCache;
            bmp.UriSource = new Uri(file, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();

            var brush = new System.Windows.Media.ImageBrush(bmp)
            {
                Stretch = System.Windows.Media.Stretch.UniformToFill,
                AlignmentX = AlignmentX.Center,
                AlignmentY = AlignmentY.Center,
                // 放大一点，模糊时边缘不会因为采样到区域外而透明
                RelativeTransform = new ScaleTransform(1.08, 1.08, 0.5, 0.5),
            };
            HeroImageLayer.Background = brush;
            HeroImageLayer.Visibility = Visibility.Visible;
            HeroMountains.Visibility = Visibility.Collapsed;   // 图片模式不叠山形
            HeroScrim.Visibility = Visibility.Visible;

            // 暗度：配置里没有就用亮度自适应（亮图压深一点，暗图别太黑）
            _applyingBg = true;
            try
            {
                int dim = (int)J.Int(LoadConfig(), "bg_dim", -1);
                if (dim < 0)
                {
                    double lum = AverageLuminance(bmp);
                    dim = lum > 0.70 ? 32 : lum > 0.5 ? 24 : lum > 0.3 ? 18 : 12;
                    AppendLog(string.Format("背景亮度 {0:F2} → 自动暗度 {1}%", lum, dim));
                }
                BgDimSlider.Value = dim;
                BgBlurSlider.Value = J.Int(LoadConfig(), "bg_blur", 0);
            }
            finally { _applyingBg = false; }

            ApplyScrimDim(BgDimSlider.Value);
            ApplyScrimBlur(BgBlurSlider.Value);
        }

        /// <summary>图片平均亮度（缩到 32x32 采样，够用且快）。</summary>
        private static double AverageLuminance(System.Windows.Media.Imaging.BitmapSource bmp)
        {
            try
            {
                var small = new System.Windows.Media.Imaging.TransformedBitmap(bmp,
                    new System.Windows.Media.ScaleTransform(
                        32.0 / Math.Max(1, bmp.PixelWidth), 32.0 / Math.Max(1, bmp.PixelHeight)));
                var conv = new System.Windows.Media.Imaging.FormatConvertedBitmap(
                    small, PixelFormats.Bgra32, null, 0);
                int w = conv.PixelWidth, h = conv.PixelHeight;
                byte[] px = new byte[w * h * 4];
                conv.CopyPixels(px, w * 4, 0);
                double sum = 0;
                for (int i = 0; i < px.Length; i += 4)
                    sum += 0.0722 * px[i] + 0.7152 * px[i + 1] + 0.2126 * px[i + 2];
                return sum / (255.0 * Math.Max(1, w * h));
            }
            catch { return 0.5; }
        }

        /// <summary>暗度 0~100 → 蒙版透明度（上浅下深，保证底部黑字/蓝按钮压得住）。</summary>
        private void ApplyScrimDim(double percent)
        {
            double a = Math.Max(0, Math.Min(100, percent)) / 100.0;
            byte top = (byte)Math.Round(a * 0.7 * 255);
            byte bottom = (byte)Math.Round(a * 255);
            var grad = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1),
            };
            grad.GradientStops.Add(new GradientStop(Color.FromArgb(top, 8, 14, 26), 0));
            grad.GradientStops.Add(new GradientStop(Color.FromArgb(bottom, 8, 14, 26), 1));
            grad.Freeze();
            HeroScrim.Background = grad;

            if (BgDimText != null) BgDimText.Text = (int)Math.Round(percent) + "%";
        }

        /// <summary>模糊半径（毛玻璃观感）。0 = 关闭。</summary>
        private void ApplyScrimBlur(double radius)
        {
            if (HeroImageLayer == null) return;
            if (radius <= 0.5)
            {
                HeroImageLayer.Effect = null;
            }
            else
            {
                var blur = new System.Windows.Media.Effects.BlurEffect
                {
                    Radius = radius,
                    KernelType = System.Windows.Media.Effects.KernelType.Gaussian,
                    RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance,
                };
                HeroImageLayer.Effect = blur;
            }
            if (BgBlurText != null) BgBlurText.Text = ((int)Math.Round(radius)).ToString();
        }

        private void BgDimSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (BgDimText == null || HeroScrim == null) return;
            ApplyScrimDim(e.NewValue);
            if (_applyingBg) return;
            try { J.SaveFile(ConfigPaths()[0], MergeConfig("bg_dim", (int)Math.Round(e.NewValue))); } catch { }
        }

        private void BgBlurSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (BgBlurText == null || HeroImageLayer == null) return;
            ApplyScrimBlur(e.NewValue);
            if (_applyingBg) return;
            try { J.SaveFile(ConfigPaths()[0], MergeConfig("bg_blur", (int)Math.Round(e.NewValue))); } catch { }
        }

        /// <summary>读现有配置 + 改一个键（滑块调整时即时落盘）。</summary>
        private Dictionary<string, object> MergeConfig(string key, object value)
        {
            var cfg = LoadConfig();
            if (cfg == null) cfg = new Dictionary<string, object>();
            cfg["data_dir"] = _baseDir;
            cfg["memory"] = MemoryBox.Text.Trim();
            cfg[key] = value;
            return cfg;
        }

        // ================= 后台任务 + 日志 =================
        private bool _running;
        private int _page;
        private bool _initializing;
        private bool _gameRunning;
        private string _cancelReason;

        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr ILCreateFromPath(string pszPath);

        [System.Runtime.InteropServices.DllImport("shell32.dll")]
        private static extern void ILFree(IntPtr pidl);

        [System.Runtime.InteropServices.DllImport("shell32.dll")]
        private static extern int SHOpenFolderAndSelectItems(IntPtr pidlFolder, uint cidl, IntPtr[] apidl, uint dwFlags);

        /// <summary>
        /// 打开文件夹。
        /// 注意：不能用 Process.Start("explorer.exe", 路径) —— 有些机器上 explorer 二次启动会
        /// 0xc0000142（DLL 初始化失败）。这里改用 Shell API：SHOpenFolderAndSelectItems
        /// （让已经在跑的 explorer 去打开，不新起进程），失败再退 ShellExecute，最后复制路径。
        /// </summary>
        private void OpenFolderSafe(string dir)
        {
            if (!Directory.Exists(dir))
            {
                MessageBox.Show("目录不存在：\n" + dir, "提示");
                return;
            }

            var errors = new List<string>();

            // 1) Shell API：在资源管理器里打开并选中该文件夹
            try
            {
                IntPtr pidl = ILCreateFromPath(dir);
                if (pidl != IntPtr.Zero)
                {
                    try
                    {
                        int hr = SHOpenFolderAndSelectItems(pidl, 0, null, 0);
                        if (hr == 0) return;
                        errors.Add("SHOpenFolderAndSelectItems: 0x" + hr.ToString("X8"));
                    }
                    finally { ILFree(pidl); }
                }
                else errors.Add("ILCreateFromPath 失败");
            }
            catch (Exception ex) { errors.Add("SHOpenFolderAndSelectItems: " + Describe(ex)); }

            // 2) ShellExecute 打开目录本身
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = dir,
                    UseShellExecute = true,
                    Verb = "open",
                });
                return;
            }
            catch (Exception ex) { errors.Add("ShellExecute: " + Describe(ex)); }

            // 3) 兜底：系统 shell 全失败 → 用应用内文件浏览（不依赖资源管理器）
            try { Clipboard.SetText(dir); } catch { }
            AppendLog("系统 shell 打不开目录（" + string.Join(" | ", errors) + "），改用应用内浏览");
            OpenBrowser(dir);
        }

        private static string Describe(Exception ex)
        {
            var we = ex as System.ComponentModel.Win32Exception;
            string code = we != null ? " (0x" + we.NativeErrorCode.ToString("X") + ")" : "";
            return ex.Message + code;
        }

        private void RunTask(string title, Func<int> action, Action onDone = null)
        {
            if (_running) { MessageBox.Show("还有任务在跑", "提示"); return; }
            _running = true;
            Http.CancelRequested = false;
            if (CancelButton != null) CancelButton.Visibility = Visibility.Visible;
            StatusText.Text = title + " …";
            CardTitle.Text = title;
            AppendLog("---- " + title + " ----");

            var writer = new LogWriter(this);
            var oldOut = Console.Out;
            var oldErr = Console.Error;

            var thread = new Thread(() =>
            {
                Console.SetOut(writer);
                Console.SetError(writer);
                int code = -1;
                try { code = action(); }
                catch (Exception ex) { writer.WriteLine("[异常] " + ex.Message); }
                finally
                {
                    Console.SetOut(oldOut);
                    Console.SetError(oldErr);
                }

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _running = false;
                    Http.CancelRequested = false;
                    if (CancelButton != null) CancelButton.Visibility = Visibility.Collapsed;
                    StatusText.Text = (_cancelReason != null ? _cancelReason : "就绪 · " + title + " 结束（退出码 " + code + "）");
                    CardTitle.Text = "准备就绪";
                    SetProgress(0);
                    if (onDone != null) { try { onDone(); } catch { } }
                }));
            });
            thread.IsBackground = true;
            thread.Start();
        }

        private void OnHttpProgress(string text, int done, int total)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (total <= 0) return;
                SetProgress((double)done / total);
                CardDetail.Text = text;
            }));
        }

        private void SetProgress(double ratio)
        {
            var grid = ProgressFill.Parent as Grid;
            if (grid == null) return;
            double width = Math.Max(0, grid.ActualWidth) * Math.Max(0, Math.Min(1, ratio));
            ProgressFill.Width = width;
        }

        private void ClearLog_Click(object sender, RoutedEventArgs e) { LogBox.Clear(); }

        private void AppendLog(string text)
        {
            if (LogBox == null) return;
            // 后台线程（下载/测速/安装）也会打日志，必须切回 UI 线程
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => AppendLog(text)));
                return;
            }
            lock (_logLock)
            {
                LogBox.AppendText(text + Environment.NewLine);
                if (ManageOutput != null) ManageOutput.AppendText(text + Environment.NewLine);
                // 内容少的时候不要滚到底（会把顶部文字挤出去）
                if (LogBox.LineCount > 9) LogBox.ScrollToEnd();
                else LogBox.ScrollToHome();
            }
        }

        private class LogWriter : TextWriter
        {
            private readonly MainWindow _owner;
            public LogWriter(MainWindow owner) { _owner = owner; }
            public override Encoding Encoding { get { return Encoding.UTF8; } }
            public override void Write(string value)
            {
                if (string.IsNullOrEmpty(value)) return;
                _owner.Dispatcher.BeginInvoke(new Action(() => _owner.AppendLog(value.TrimEnd())));
            }
            public override void WriteLine(string value) { Write(value); }
        }
    }
}
