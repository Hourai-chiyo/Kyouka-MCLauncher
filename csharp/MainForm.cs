using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Mcroute
{
    /// <summary>C# 版 GUI（WinForms，.NET Framework 4.8 自带，不需要任何依赖）。</summary>
    public class MainForm : Form
    {
        private string _baseDir;
        private TabControl _tabs;
        private ListBox _versionList;
        private ListBox _manageList;
        private ComboBox _javaCombo;
        private TextBox _memBox;
        private TextBox _downloadBox;
        private ComboBox _loaderKind;
        private TextBox _loaderVer;
        private TextBox _log;
        private Label _status;
        private TextBox _dataDirBox;
        private SimpleProgress _progress;
        private TextBox _cfKeyBox;
        private TextBox _searchBox;
        private TextBox _packPathBox;
        private readonly List<string> _allVersions = new List<string>();
        private readonly List<Tuple<string, int>> _javas = new List<Tuple<string, int>>();

        public MainForm()
        {
            Program.Trace("MainForm ctor 开始");
            // 文档截图时可以指定数据目录（MCROUTE_DATA），平时走配置 + 默认逻辑
            string fromEnv = Environment.GetEnvironmentVariable("MCROUTE_DATA");
            _baseDir = Version.BaseDir(string.IsNullOrEmpty(fromEnv) ? Config.LoadDataDir() : fromEnv);
            Program.Trace("数据目录 = " + _baseDir);

            // 窗口图标：直接取 exe 内嵌的那个（app.ico 已通过 ApplicationIcon 打进去）
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            Text = "mcroute — Minecraft 启动器（C# 版）";
            ClientSize = new Size(980, 690);
            MinimumSize = new Size(780, 540);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9f);

            Program.Trace("开始 BuildUi");
            BuildUi();
            Program.Trace("BuildUi 完成，开始 RefreshAll");
            RefreshAll();
            Program.Trace("RefreshAll 完成");
            AppendLog("数据目录：" + _baseDir);

            // 下载进度 -> 进度条 + 状态栏
            Http.Progress = (text, done, total) =>
            {
                if (IsDisposed || !IsHandleCreated) return;
                try
                {
                    BeginInvoke(new Action(() =>
                {
                    if (total > 0)
                    {
                        _progress.Maximum = total;
                        _progress.Value = Math.Min(done, total);
                    }
                        _status.Text = text;
                    }));
                }
                catch (InvalidOperationException) { /* 窗口正在销毁，忽略 */ }
            };

            // 注意：不能在构造函数里 BeginInvoke（窗口句柄还没建），用 Shown 事件
            Shown += (s2, e2) =>
            {
                EnablePackDragDrop();
                if (_versionList.Items.Count == 0) FirstRunGuide();
            };
        }

        // ---------------- 布局（全部 Dock/TableLayout，窗口放大不会散架） ----------------
        private void BuildUi()
        {
            var top = new Panel { Dock = DockStyle.Top, Height = 30, Padding = new Padding(10, 7, 10, 0) };
            top.Controls.Add(new Label { Text = "未登录 · 离线模式（可单机游玩）", AutoSize = true, ForeColor = Color.DimGray });

            // 数据目录行（TableLayout：标签 | 输入框(填满) | 按钮）
            var dataRow = new Panel { Dock = DockStyle.Top, Height = 38, Padding = new Padding(10, 4, 10, 0) };
            var dataGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
            dataGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            dataGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            dataGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90f));
            dataGrid.Controls.Add(new Label { Text = "数据目录", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 0) }, 0, 0);
            _dataDirBox = new TextBox { Dock = DockStyle.Fill, ReadOnly = true, Margin = new Padding(0, 3, 6, 0) };
            dataGrid.Controls.Add(_dataDirBox, 1, 0);
            var pickDir = new Button { Text = "选择...", Dock = DockStyle.Fill, Margin = new Padding(0, 2, 0, 0) };
            pickDir.Click += (s2, e2) => PickDataDir();
            dataGrid.Controls.Add(pickDir, 2, 0);
            dataRow.Controls.Add(dataGrid);

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 180 };
            _status = new Label { Dock = DockStyle.Top, Height = 22, Text = "就绪", ForeColor = Color.DimGray, Padding = new Padding(10, 4, 0, 0) };
            _log = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                ReadOnly = true,
                Font = new Font("Consolas", 9f),
                BackColor = Color.White,
            };
            bottom.Controls.Add(_log);
            bottom.Controls.Add(_status);
            _progress = new SimpleProgress { Dock = DockStyle.Top, Height = 18 };
            bottom.Controls.Add(_progress);

            var middle = new Panel { Dock = DockStyle.Fill };
            _tabs = new TabControl { Dock = DockStyle.Fill };
            _tabs.TabPages.Add(BuildLaunchTab());
            _tabs.TabPages.Add(BuildLoaderTab());
            _tabs.TabPages.Add(BuildPackTab());
            _tabs.TabPages.Add(BuildManageTab());
            middle.Controls.Add(_tabs);

            // Dock 顺序：先加的占外框，后加的填剩余
            Controls.Add(middle);
            Controls.Add(bottom);
            Controls.Add(dataRow);
            Controls.Add(top);
        }

        private TabPage BuildLaunchTab()
        {
            var page = new TabPage("  启动  ") { Padding = new Padding(8) };

            var split = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58f));
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42f));

            // 左：版本列表 + 按钮
            var leftInner = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 0, 8, 0) };
            var listLabel = new Label { Text = "本地已安装版本", Dock = DockStyle.Top, Height = 22 };
            _versionList = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
            _versionList.DoubleClick += (s, e) => LaunchSelected();   // 双击直接启动
            _versionList.KeyDown += (s2, e2) =>
            {
                if (e2.KeyCode == Keys.Enter) { e2.SuppressKeyPress = true; LaunchSelected(); }
            };

            var searchRow = new Panel { Dock = DockStyle.Top, Height = 30 };
            var searchIcon = new Label { Text = "搜索", AutoSize = true, Location = new Point(0, 8), ForeColor = Color.DimGray };
            _searchBox = new TextBox { Left = 44, Top = 5, Width = 260, Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right };
            searchRow.Resize += (s2, e2) => _searchBox.Width = Math.Max(80, searchRow.ClientSize.Width - 50);
            _searchBox.TextChanged += (s2, e2) => ApplyVersionFilter();
            searchRow.Controls.Add(searchIcon);
            searchRow.Controls.Add(_searchBox);
            ApplyCueBanner(_searchBox, "输入版本名筛选，例如 1.20 或 forge");
            var btnRow = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(0, 6, 0, 0) };
            var refresh = new Button { Text = "刷新", Width = 80, Height = 28 };
            refresh.Click += (s, e) => RefreshAll();
            var launch = new Button { Text = "▶ 启动选中版本", Width = 150, Height = 28 };
            launch.Click += (s, e) => LaunchSelected();
            btnRow.Controls.Add(refresh);
            btnRow.Controls.Add(launch);
            leftInner.Controls.Add(_versionList);
            leftInner.Controls.Add(searchRow);
            leftInner.Controls.Add(listLabel);
            leftInner.Controls.Add(btnRow);

            // 右：下载 / Java / 内存（用 TableLayout，避免出现多余滚动条）
            var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
            right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, 152f));
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, 132f));
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, 108f));
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            var dlGroup = new GroupBox { Text = "下载原版版本", Dock = DockStyle.Fill, Padding = new Padding(10) };
            var dlFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            _downloadBox = new TextBox { Width = 180, Text = "1.21.1" };
            var skipAssets = new CheckBox { Text = "跳过资源（快，但没有音效贴图）", AutoSize = true };
            var dlBtn = new Button { Text = "开始下载", Width = 120, Height = 28 };
            dlBtn.Click += (s, e) => RunDownload(skipAssets.Checked);
            dlFlow.Controls.Add(_downloadBox);
            dlFlow.Controls.Add(skipAssets);
            dlFlow.Controls.Add(dlBtn);
            dlGroup.Controls.Add(dlFlow);

            var javaGroup = new GroupBox { Text = "Java（自动按版本匹配）", Dock = DockStyle.Fill, Padding = new Padding(10) };
            var javaPanel = new Panel { Dock = DockStyle.Fill };
            _javaCombo = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList };
            var reJava = new Button { Text = "重新检测 Java", Width = 130, Height = 28, Top = 36, Left = 0 };
            reJava.Click += (s, e) => RefreshJava();
            javaPanel.Controls.Add(_javaCombo);
            javaPanel.Controls.Add(reJava);
            javaGroup.Controls.Add(javaPanel);

            var memGroup = new GroupBox { Text = "最大内存", Dock = DockStyle.Fill, Padding = new Padding(10) };
            var memPanel = new Panel { Dock = DockStyle.Fill };
            _memBox = new TextBox { Width = 80, Text = "2G" };
            var memHint = new Label { Text = "整合包通常要 4G 以上", AutoSize = true, Top = 36, Left = 0, ForeColor = Color.DimGray };
            memPanel.Controls.Add(_memBox);
            memPanel.Controls.Add(memHint);
            memGroup.Controls.Add(memPanel);

            right.Controls.Add(dlGroup, 0, 0);
            right.Controls.Add(javaGroup, 0, 1);
            right.Controls.Add(memGroup, 0, 2);

            split.Controls.Add(leftInner, 0, 0);
            split.Controls.Add(right, 1, 0);
            page.Controls.Add(split);
            return page;
        }

        private TabPage BuildLoaderTab()
        {
            var page = new TabPage("  加载器  ") { Padding = new Padding(12) };
            var outer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
            outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 28f));
            outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 74f));
            outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 46f));
            outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            outer.Controls.Add(new Label
            {
                Text = "给某个 MC 版本装加载器（自动下载原版 + 安装 + 补库）",
                AutoSize = true,
                ForeColor = Color.DimGray,
            }, 0, 0);

            var form = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
            form.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70f));
            form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            form.Controls.Add(new Label { Text = "加载器", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
            _loaderKind = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList };
            _loaderKind.Items.AddRange(new object[] { "fabric", "quilt", "legacyfabric", "forge", "neoforge" });
            _loaderKind.SelectedIndex = 0;
            form.Controls.Add(_loaderKind, 1, 0);
            form.Controls.Add(new Label { Text = "版本", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
            _loaderVer = new TextBox { Dock = DockStyle.Top, Text = "1.20.1" };
            form.Controls.Add(_loaderVer, 1, 1);
            outer.Controls.Add(form, 0, 1);

            var installRow = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 0) };
            var install = new Button { Text = "安装加载器", Width = 130, Height = 30 };
            install.Click += (s, e) => RunLoader();
            installRow.Controls.Add(install);
            outer.Controls.Add(installRow, 0, 2);

            outer.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = Color.DimGray,
                Text = "提示：\n" +
                       "· fabric / quilt / legacyfabric：版本写 MC 版本即可，自动挑最新 stable\n" +
                       "· forge：写 1.7.10 或 1.20.1-47.2.0 都行（自动补全）\n" +
                       "· neoforge：写 21.1.264\n\n" +
                       "Forge / NeoForge 会调用官方安装器（第一次要几分钟）；Fabric 系直接读 meta，几秒完成。\n" +
                       "装完后到「启动」页刷新，就能看到一个新版本。",
            }, 0, 3);

            page.Controls.Add(outer);
            return page;
        }

        private TabPage BuildPackTab()
        {
            var page = new TabPage("  整合包  ") { Padding = new Padding(12) };
            var outer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
            outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 28f));
            outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 40f));
            outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 40f));
            outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 46f));
            outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            outer.Controls.Add(new Label
            {
                Text = "支持 Modrinth .mrpack / MultiMC·Prism 实例包 / CurseForge zip（自动识别）",
                AutoSize = true,
                ForeColor = Color.DimGray,
            }, 0, 0);

            // 包文件
            var pathRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3 };
            pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90f));
            pathRow.Controls.Add(new Label { Text = "包文件", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 0) }, 0, 0);
            var path = new TextBox { Dock = DockStyle.Top };
            _packPathBox = path;
            path.DragEnter += (s2, e2) =>
            {
                if (e2.Data.GetDataPresent(DataFormats.FileDrop)) e2.Effect = DragDropEffects.Copy;
            };
            path.DragDrop += (s2, e2) =>
            {
                var files = (string[])e2.Data.GetData(DataFormats.FileDrop);
                if (files != null && files.Length > 0) path.Text = files[0];
            };
            pathRow.Controls.Add(path, 1, 0);
            var browse = new Button { Text = "浏览...", Dock = DockStyle.Fill };
            browse.Click += (s2, e2) =>
            {
                using (var dlg = new OpenFileDialog())
                {
                    dlg.Filter = "整合包|*.mrpack;*.zip|所有文件|*.*";
                    dlg.FilterIndex = 1;
                    dlg.Title = "选择整合包";
                    string lastDir = Config.LoadPackDir();
                    if (!string.IsNullOrEmpty(lastDir) && Directory.Exists(lastDir))
                        dlg.InitialDirectory = lastDir;
                    if (dlg.ShowDialog() == DialogResult.OK)
                    {
                        path.Text = dlg.FileName;
                        Config.SavePackDir(Path.GetDirectoryName(dlg.FileName));
                    }
                }
            };
            pathRow.Controls.Add(browse, 2, 0);
            outer.Controls.Add(pathRow, 0, 1);

            // CurseForge key（只有 Cf 包需要，存在 mcdata/cf_api_key.txt）
            var keyRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3 };
            keyRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            keyRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            keyRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90f));
            keyRow.Controls.Add(new Label { Text = "CF API key", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 0) }, 0, 0);
            _cfKeyBox = new TextBox { Dock = DockStyle.Top, UseSystemPasswordChar = true };
            string existingKey = Packs.ReadCurseForgeKey(_baseDir);
            if (!string.IsNullOrEmpty(existingKey)) _cfKeyBox.Text = existingKey;
            keyRow.Controls.Add(_cfKeyBox, 1, 0);
            var saveKey = new Button { Text = "保存 key", Dock = DockStyle.Fill };
            saveKey.Click += (s2, e2) => SaveCfKey();
            keyRow.Controls.Add(saveKey, 2, 0);
            outer.Controls.Add(keyRow, 0, 2);

            // 安装按钮
            var installRow = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 0) };
            var install = new Button { Text = "安装这个整合包", Width = 150, Height = 30 };
            install.Click += (s2, e2) =>
            {
                if (path.Text.Trim().Length == 0) { MessageBox.Show("先选一个整合包文件", "提示"); return; }
                string file = path.Text.Trim();
                RunInBackground("安装 " + Path.GetFileName(file),
                    () => Packs.Install(new[] { "install", file, "--dir", _baseDir }));
            };
            installRow.Controls.Add(install);
            outer.Controls.Add(installRow, 0, 3);

            outer.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = Color.DimGray,
                Text = "说明：\n" +
                       "· 也可以把整合包文件直接拖到上面的输入框里\n" +
                       "· 装完会自动补全依赖（缺什么下什么），可以直接启动\n" +
                       "· 只有 CurseForge 包需要 API key（在 https://console.curseforge.com 申请），存本地不联网\n" +
                       "· 装完到「管理」页体检 / 测试启动",
            }, 0, 4);

            page.Controls.Add(outer);
            return page;
        }

        private TabPage BuildManageTab()
        {
            var page = new TabPage("  管理  ") { Padding = new Padding(8) };
            var outer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
            outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 26f));
            outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 44f));

            outer.Controls.Add(new Label
            {
                Text = "所有本地版本：原版 / 整合包 / 外部导入，都能体检、测试、生成启动器",
                AutoSize = true,
            }, 0, 0);

            _manageList = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
            outer.Controls.Add(_manageList, 0, 1);

            var btnRow = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 0) };
            var refresh = new Button { Text = "刷新", Width = 80, Height = 28 };
            refresh.Click += (s, e) => RefreshAll();
            var verify = new Button { Text = "体检", Width = 80, Height = 28 };
            verify.Click += (s, e) => RunOnSelected("verify");
            var test = new Button { Text = "测试启动", Width = 100, Height = 28 };
            test.Click += (s, e) => RunOnSelected("test");
            var mkbat = new Button { Text = "生成启动器", Width = 110, Height = 28 };
            mkbat.Click += (s, e) => RunOnSelected("mkbat");
            var openDir = new Button { Text = "打开目录", Width = 90, Height = 28 };
            openDir.Click += (s, e) => OpenSelectedDir();
            btnRow.Controls.AddRange(new Control[] { refresh, verify, test, mkbat, openDir });
            outer.Controls.Add(btnRow, 0, 2);

            page.Controls.Add(outer);
            return page;
        }

        /// <summary>
        /// 拖拽注册必须在窗口句柄建好之后做，否则抛 "DragDrop 注册失败"。
        /// 某些环境（远程桌面/OLE 受限）本来就注册不上，所以失败也不能影响启动。
        /// </summary>
        private void EnablePackDragDrop()
        {
            try
            {
                if (_packPathBox != null) _packPathBox.AllowDrop = true;
            }
            catch (Exception ex)
            {
                Program.Trace("拖拽不可用: " + ex.Message);
            }
        }

        /// <summary>第一次打开且没有任何版本时引导一下。</summary>
        private void FirstRunGuide()
        {
            var r = MessageBox.Show(
                "还没有任何本地版本。\n\n" +
                "如果以前用过别的启动器（PCL / HMCL / 官方启动器），可以点「是」把它已有的版本目录导进来，\n" +
                "不用重新下载。\n\n" +
                "点「否」就留在当前页面，用右上角「开始下载」装一个原版版本。",
                "mcroute — 第一次使用", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (r == DialogResult.Yes) PickDataDir();
        }

        private void SaveCfKey()
        {
            string key = _cfKeyBox.Text.Trim();
            if (key.Length == 0) { MessageBox.Show("先把 key 粘贴进来", "提示"); return; }
            try
            {
                File.WriteAllText(Path.Combine(_baseDir, "cf_api_key.txt"), key);
                AppendLog("已保存 CurseForge API key 到 " + Path.Combine(_baseDir, "cf_api_key.txt"));
                MessageBox.Show("已保存。以后装 CurseForge 整合包不用再填。", "提示");
            }
            catch (Exception ex)
            {
                MessageBox.Show("保存失败：" + ex.Message, "提示");
            }
        }

        // ---------------- 数据目录 ----------------
        private void PickDataDir()
        {
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = "选择数据目录（里面有 versions / libraries / assets 的那一层）";
                dlg.SelectedPath = _baseDir;
                if (dlg.ShowDialog() != DialogResult.OK) return;

                string chosen = dlg.SelectedPath;
                if (!Directory.Exists(Path.Combine(chosen, "versions")) &&
                    Directory.Exists(Path.Combine(chosen, "mcdata", "versions")))
                    chosen = Path.Combine(chosen, "mcdata");
                if (!Directory.Exists(Path.Combine(chosen, "versions")))
                {
                    MessageBox.Show("这个目录里没有 versions 文件夹，看起来不是启动器的数据目录。\n\n" +
                                    "应该选包含 versions / libraries / assets 的那一层。", "提示");
                    return;
                }

                _baseDir = chosen;
                Config.SaveDataDir(chosen);
                RefreshAll();
                AppendLog("已切换数据目录：" + chosen);
            }
        }

        /// <summary>按搜索框内容过滤两个列表。</summary>
        private void ApplyVersionFilter()
        {
            string keyword = _searchBox == null ? "" : _searchBox.Text.Trim();
            _versionList.BeginUpdate();
            _manageList.BeginUpdate();
            _versionList.Items.Clear();
            _manageList.Items.Clear();
            string root = Path.Combine(_baseDir, "versions");
            foreach (string name in _allVersions)
            {
                if (keyword.Length > 0 && name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                string dir = Path.Combine(root, name);
                string tag = File.Exists(Path.Combine(dir, "instance.json"))
                    ? "[整合包]"
                    : (Directory.Exists(Path.Combine(dir, "mods")) ? "[外部]" : "[原版]");
                _versionList.Items.Add(name);
                _manageList.Items.Add(name + "   " + tag);
            }
            _versionList.EndUpdate();
            _manageList.EndUpdate();

            if (keyword.Length > 0)
                _status.Text = string.Format("筛选 \"{0}\"：{1}/{2} 个版本", keyword, _versionList.Items.Count, _allVersions.Count);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        /// <summary>给输入框加灰色提示文字（WinForms 没有 placeholder）。</summary>
        private static void ApplyCueBanner(TextBox box, string text)
        {
            try { SendMessage(box.Handle, 0x1501, (IntPtr)1, text); } catch { }
        }

        // ---------------- 数据 ----------------
        private void RefreshAll()
        {
            _dataDirBox.Text = _baseDir;
            if (_searchBox != null) _searchBox.Text = "";
            RefreshVersions();
            RefreshJava();
        }

        private void RefreshVersions()
        {
            _versionList.Items.Clear();
            _manageList.Items.Clear();
            string root = Path.Combine(_baseDir, "versions");
            if (!Directory.Exists(root))
            {
                AppendLog("这个数据目录里还没有版本：" + root);
                return;
            }

            var names = Directory.GetDirectories(root).Select(Path.GetFileName).OrderBy(x => x).ToList();
            _allVersions.Clear();
            _allVersions.AddRange(names);

            int packs = _allVersions.Count(n => File.Exists(Path.Combine(root, n, "instance.json")));
            ApplyVersionFilter();
            AppendLog(string.Format("找到 {0} 个本地版本（其中 {1} 个整合包）", _allVersions.Count, packs));
        }

        private void RefreshJava()
        {
            _javas.Clear();
            _javaCombo.Items.Clear();
            foreach (string root in JavaRoots())
            {
                if (!Directory.Exists(root)) continue;
                foreach (string sub in Directory.GetDirectories(root))
                {
                    string exe = Path.Combine(sub, "bin", "java.exe");
                    if (!File.Exists(exe)) continue;
                    int major = Java.Major(exe);
                    if (major <= 0) continue;
                    _javas.Add(Tuple.Create(exe, major));
                    _javaCombo.Items.Add("Java " + major + "  " + exe);
                }
            }
            if (_javaCombo.Items.Count > 0)
            {
                int idx = _javas.FindIndex(t => t.Item2 == 17);
                _javaCombo.SelectedIndex = idx >= 0 ? idx : 0;
                AppendLog("检测到 " + _javaCombo.Items.Count + " 个 Java");
            }
            else AppendLog("没检测到 Java");
        }

        private IEnumerable<string> JavaRoots()
        {
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Eclipse Adoptium");
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Java");
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft", "runtime");
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ModrinthApp", "meta", "java_versions");
            yield return Path.Combine(_baseDir, "java_versions");
        }

        // ---------------- 操作 ----------------
        private string SelectedVersion()
        {
            object sel = _versionList.SelectedItem;
            if (sel == null)
            {
                MessageBox.Show("先在左边选一个版本", "提示");
                return null;
            }
            return sel.ToString();
        }

        private void LaunchSelected()
        {
            string ver = SelectedVersion();
            if (ver == null) return;
            string java = SelectedJava();
            string mem = _memBox.Text.Trim();
            RunInBackground("启动 " + ver, () =>
                Commands.Launch(new[] { "launch", ver, "--dir", _baseDir, "--java", java, "--memory", mem }));
        }

        private void RunDownload(bool skipAssets)
        {
            string ver = _downloadBox.Text.Trim();
            if (ver.Length == 0) { MessageBox.Show("先填版本号，例如 1.21.1", "提示"); return; }
            var args = new List<string> { "fetch", ver, "--dir", _baseDir };
            if (skipAssets) args.Add("--no-assets");
            RunInBackground("下载 " + ver, () => Commands.Fetch(args.ToArray()));
        }

        private void RunLoader()
        {
            string kind = _loaderKind.SelectedItem.ToString();
            string ver = _loaderVer.Text.Trim();
            RunInBackground("安装 " + kind + " " + ver, () => Loader.Run(new[] { "loader", kind, ver, "--dir", _baseDir }));
        }

        private void RunOnSelected(string cmd)
        {
            object sel = _manageList.SelectedItem;
            if (sel == null) { MessageBox.Show("先在列表里选一个版本", "提示"); return; }
            string name = sel.ToString().Split(' ')[0];
            if (cmd == "verify")
                RunInBackground("体检 " + name, () => ExtraCommands.Verify(new[] { "verify", name, "--dir", _baseDir }));
            else if (cmd == "test")
                RunInBackground("测试 " + name, () => ExtraCommands.Test(new[] { "test", name, "--dir", _baseDir }));
            else
                RunInBackground("生成启动器 " + name, () => ExtraCommands.Mkbat(new[] { "mkbat", name, "--dir", _baseDir }));
        }

        private void OpenSelectedDir()
        {
            object sel = _manageList.SelectedItem;
            if (sel == null) { MessageBox.Show("先在列表里选一个版本", "提示"); return; }
            string name = sel.ToString().Split(' ')[0];
            string dir = Path.Combine(_baseDir, "versions", name);
            if (!Directory.Exists(dir)) dir = Path.Combine(_baseDir, "instances", name);
            if (!Directory.Exists(dir)) { MessageBox.Show("目录不存在：" + dir, "提示"); return; }
            // 不要 Process.Start("explorer.exe", ...)：某些环境下二次启动 explorer 会
            // 0xc0000142（DLL 初始化失败）。交给 ShellExecute 直接打开文件夹最稳。
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = dir,
                    UseShellExecute = true,
                    Verb = "open",
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show("打开目录失败：" + ex.Message, "提示");
            }
        }

        private string SelectedJava()
        {
            int idx = _javaCombo.SelectedIndex;
            if (idx >= 0 && idx < _javas.Count) return _javas[idx].Item1;
            return null;
        }

        // ---------------- 后台执行 + 日志 ----------------
        private void RunInBackground(string title, Func<int> action)
        {
            _status.Text = title + " ...";
            AppendLog("---- " + title + " ----");

            var writer = new TextBoxWriter(this);
            var oldOut = Console.Out;
            var oldErr = Console.Error;

            var thread = new Thread(() =>
            {
                Console.SetOut(writer);
                Console.SetError(writer);
                try
                {
                    int code = action();
                    BeginInvoke(new Action(() =>
                    {
                        AppendLog(title + " 结束（退出码 " + code + "）");
                        _status.Text = "就绪";
                        RefreshVersions();
                    }));
                }
                catch (Exception ex)
                {
                    BeginInvoke(new Action(() =>
                    {
                        AppendLog("[异常] " + ex.Message);
                        _status.Text = "就绪";
                    }));
                }
                finally
                {
                    Console.SetOut(oldOut);
                    Console.SetError(oldErr);
                }
            });
            thread.IsBackground = true;
            thread.Start();
        }

        private void AppendLog(string text)
        {
            if (IsDisposed) return;
            if (!IsHandleCreated) return;      // 句柄还没建好，直接丢弃（启动阶段）
            if (InvokeRequired) { BeginInvoke(new Action<string>(AppendLog), text); return; }
            _log.AppendText(text + Environment.NewLine);
        }

        /// <summary>给文档截图用：逐页渲染成 png（--shot 模式）。</summary>
        public void CaptureTabs(string dir)
        {
            Directory.CreateDirectory(dir);
            string[] names = { "launch", "loader", "packs", "manage" };
            int i = 0;
            var timer = new System.Windows.Forms.Timer { Interval = 800 };
            timer.Tick += (s2, e2) =>
            {
                if (i >= _tabs.TabPages.Count)
                {
                    timer.Stop();
                    Close();
                    return;
                }
                _tabs.SelectedIndex = i;
                if (i == 0)
                {
                    _progress.Maximum = 2300;
                    _progress.Value = 780;
                    _status.Text = "示例 780/2300   新下载 612  已存在 120  12.4 MB  5.1 MB/s";
                }
                Application.DoEvents();
                try
                {
                    using (var bmp = new Bitmap(ClientSize.Width, Height))
                    {
                        DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
                        string name = i < names.Length ? names[i] : "tab" + i;
                        bmp.Save(Path.Combine(dir, "gui-csharp-" + name + ".png"),
                                 System.Drawing.Imaging.ImageFormat.Png);
                        Console.WriteLine("saved " + name);
                    }
                }
                catch (Exception ex) { Console.WriteLine("fail " + ex.Message); }
                i++;
            };
            timer.Start();
        }

        private class TextBoxWriter : TextWriter
        {
            private readonly MainForm _owner;
            public TextBoxWriter(MainForm owner) { _owner = owner; }
            public override Encoding Encoding { get { return Encoding.UTF8; } }
            public override void Write(string value) { if (!string.IsNullOrEmpty(value)) _owner.AppendLog(value.TrimEnd()); }
            public override void WriteLine(string value) { _owner.AppendLog(value); }
        }
    }

    /// <summary>GUI 小配置（记住数据目录）。</summary>
    public static class Config
    {
        private static string PathFile()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "mcroute");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "gui.json");
        }

        public static string LoadDataDir()
        {
            try
            {
                string f = PathFile();
                if (!File.Exists(f)) return null;
                var data = J.Obj(J.ParseFile(f));
                string dir = J.Str(data, "data_dir");
                return string.IsNullOrEmpty(dir) ? null : dir;
            }
            catch { return null; }
        }

        public static string LoadPackDir()
        {
            try
            {
                string f = PathFile();
                if (!File.Exists(f)) return null;
                var data = J.Obj(J.ParseFile(f));
                string dir = J.Str(data, "pack_dir");
                return string.IsNullOrEmpty(dir) ? null : dir;
            }
            catch { return null; }
        }

        public static void SavePackDir(string dir)
        {
            try
            {
                string f = PathFile();
                var data = File.Exists(f) ? J.Obj(J.ParseFile(f)) : new Dictionary<string, object>();
                if (data == null) data = new Dictionary<string, object>();
                data["pack_dir"] = dir;
                J.SaveFile(f, data);
            }
            catch { }
        }

        public static void SaveDataDir(string dir)
        {
            try
            {
                J.SaveFile(PathFile(), new Dictionary<string, object> { { "data_dir", dir } });
            }
            catch { }
        }
    }
}

/// <summary>自绘进度条：系统 ProgressBar 在浅色主题下 0% 时几乎看不见，这个可以控制颜色。</summary>
public class SimpleProgress : Control
{
    private int _max = 100;
    private int _value;

    public SimpleProgress()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    public int Maximum
    {
        get { return _max; }
        set { _max = Math.Max(1, value); Invalidate(); }
    }

    public int Value
    {
        get { return _value; }
        set { _value = value; Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(SystemColors.Control);
        var track = new Rectangle(0, 2, Math.Max(0, Width - 1), Math.Max(0, Height - 5));
        using (var back = new SolidBrush(Color.FromArgb(205, 210, 216)))
            g.FillRectangle(back, track);

        double ratio = (double)_value / _max;
        int w = (int)(track.Width * Math.Min(1.0, Math.Max(0.0, ratio)));
        if (w > 0)
        {
            using (var fill = new SolidBrush(Color.FromArgb(62, 138, 200)))
                g.FillRectangle(fill, new Rectangle(track.X + 1, track.Y + 1, Math.Max(0, w - 2), Math.Max(0, track.Height - 2)));
        }
        using (var border = new Pen(Color.FromArgb(150, 156, 164)))
            g.DrawRectangle(border, track);
    }
}
