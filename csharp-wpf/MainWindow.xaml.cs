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
                LoadBackground();
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
        private static string ConfigPath()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "mcroute");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "wpf.json");
        }

        private static Dictionary<string, object> LoadConfig()
        {
            try
            {
                string p = ConfigPath();
                if (!File.Exists(p)) return new Dictionary<string, object>();
                return J.Obj(J.ParseFile(p)) ?? new Dictionary<string, object>();
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
                J.SaveFile(ConfigPath(), new Dictionary<string, object>
                {
                    { "data_dir", _baseDir },
                    { "memory", MemoryBox.Text.Trim() },
                });
            }
            catch { }
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
            HeroLayer.Visibility = Visibility.Collapsed;

            var rb = sender as RadioButton;
            string name = rb == null ? "启动" : (rb.Content as string ?? "启动");
            switch (name)
            {
                case "启动": PageLaunch.Visibility = Visibility.Visible; HeroLayer.Visibility = Visibility.Visible; break;
                case "下载": PageDownload.Visibility = Visibility.Visible; break;
                case "整合包": PagePacks.Visibility = Visibility.Visible; break;
                case "管理": PageManage.Visibility = Visibility.Visible; RefreshManageList(); break;
                case "设置": PageSettings.Visibility = Visibility.Visible; RefreshJava(); break;
            }
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

        private void RefreshVersions()
        {
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
                string tag = File.Exists(Path.Combine(dir, "instance.json"))
                    ? "整合包"
                    : (Directory.Exists(Path.Combine(dir, "mods")) ? "外部" : "原版");
                _versions.Add(name);
                VersionList.Items.Add(MakeVersionItem(name, tag));
            }

            VersionCount.Text = _versions.Count + " 个";
            if (VersionList.Items.Count > 0) VersionList.SelectedIndex = 0;
            AppendLog("找到 " + _versions.Count + " 个本地版本");
        }

        private static ListBoxItem MakeVersionItem(string name, string tag)
        {
            var panel = new StackPanel();
            var title = new TextBlock
            {
                Text = name,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.Resources["Text"],
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var sub = new TextBlock
            {
                Text = tag,
                FontSize = 11,
                Margin = new Thickness(0, 3, 0, 0),
                Foreground = (Brush)Application.Current.Resources["TextMuted"],
            };
            panel.Children.Add(title);
            panel.Children.Add(sub);
            return new ListBoxItem { Content = panel, Tag = name };
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
            RunTask("下载 " + ver, () => Commands.Fetch(args), RefreshVersions);
        }

        private void Loader_Click(object sender, RoutedEventArgs e)
        {
            string kind = (LoaderKind.SelectedItem as ComboBoxItem)?.Content as string ?? "fabric";
            string ver = LoaderVersion.Text.Trim();
            if (ver.Length == 0) { MessageBox.Show("先填版本号", "提示"); return; }
            RunTask("安装 " + kind + " " + ver,
                () => Loader.Run(new[] { "loader", kind, ver, "--dir", _baseDir }), RefreshVersions);
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
                () => Packs.Install(new[] { "install", path, "--dir", _baseDir }), RefreshVersions);
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
                string tag = File.Exists(Path.Combine(dir, "instance.json"))
                    ? "整合包"
                    : (Directory.Exists(Path.Combine(dir, "mods")) ? "外部" : "原版");
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
                    BgPathText.Text = file;
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
                BgPathText.Text = target;
                AppendLog("已设置背景图：" + dlg.FileName);
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
                HeroImageLayer.Visibility = Visibility.Collapsed;
                HeroScrim.Visibility = Visibility.Collapsed;
                HeroMountains.Visibility = Visibility.Visible;
                BgPathText.Text = "（默认矢量背景）";
                AppendLog("已恢复默认背景");
            }
            catch (Exception ex)
            {
                MessageBox.Show("恢复默认失败：" + ex.Message, "提示");
            }
        }

        private void ApplyBackgroundBrush(string file)
        {
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(file, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();

            var brush = new System.Windows.Media.ImageBrush(bmp)
            {
                Stretch = System.Windows.Media.Stretch.UniformToFill,
                AlignmentX = AlignmentX.Center,
                AlignmentY = AlignmentY.Center,
            };
            HeroImageLayer.Background = brush;
            HeroImageLayer.Visibility = Visibility.Visible;
            HeroMountains.Visibility = Visibility.Collapsed;   // 图片模式不叠山形
            HeroScrim.Visibility = Visibility.Visible;         // 暗色蒙版，保证白字可读
        }

        // ================= 后台任务 + 日志 =================
        private bool _running;
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

            // 3) 兜底：复制路径 + 给出排查提示
            try { Clipboard.SetText(dir); } catch { }
            AppendLog("打开目录失败：" + string.Join(" | ", errors));
            MessageBox.Show(
                "打开目录失败：\n" + string.Join("\n", errors) + "\n\n" +
                "路径已复制到剪贴板，可以直接粘到资源管理器地址栏。\n" + dir + "\n\n" +
                "诊断：当前进程会话 = " + Process.GetCurrentProcess().SessionId + "\n" +
                "（正常交互会话通常是 1）\n\n" +
                "原因很可能是：mcroute 是从 AI 工具 / 沙箱窗口里启动的，\n" +
                "和你的资源管理器不在同一个 Windows 会话，shell 调用因此被拒。\n" +
                "请到资源管理器里双击 mcroute-wpf.exe 再试一次。",
                "提示");
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
