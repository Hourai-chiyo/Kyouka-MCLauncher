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
        private readonly string _baseDir;
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
        private readonly List<Tuple<string, int>> _javas = new List<Tuple<string, int>>();

        public MainForm()
        {
            // 文档截图时可以指定数据目录（MCROUTE_DATA），平时走默认逻辑
            _baseDir = Version.BaseDir(Environment.GetEnvironmentVariable("MCROUTE_DATA"));
            Text = "mcroute — Minecraft 启动器（C# 版）";
            ClientSize = new Size(940, 640);
            MinimumSize = new Size(820, 560);
            StartPosition = FormStartPosition.CenterScreen;

            BuildUi();
            RefreshVersions();
            RefreshJava();

            AppendLog("数据目录：" + _baseDir);
        }

        // ---------------- UI ----------------
        private void BuildUi()
        {
            var top = new Panel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(8, 6, 8, 0) };
            var account = new Label { Text = "未登录 · 离线模式（可单机游玩）", AutoSize = true, ForeColor = Color.DimGray };
            top.Controls.Add(account);
            Controls.Add(top);

            _tabs = new TabControl { Dock = DockStyle.Fill };
            _tabs.TabPages.Add(BuildLaunchTab());
            _tabs.TabPages.Add(BuildLoaderTab());
            _tabs.TabPages.Add(BuildPackTab());
            _tabs.TabPages.Add(BuildManageTab());
            Controls.Add(_tabs);

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 190 };
            _status = new Label { Dock = DockStyle.Top, Height = 22, Text = "就绪", ForeColor = Color.DimGray, Padding = new Padding(8, 4, 0, 0) };
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
            Controls.Add(bottom);
        }

        private TabPage BuildLaunchTab()
        {
            var page = new TabPage("  启动  ");

            var listLabel = new Label { Text = "本地已安装版本", Location = new Point(12, 10), AutoSize = true };
            _versionList = new ListBox { Location = new Point(12, 32), Size = new Size(430, 300), Font = new Font("Microsoft YaHei UI", 9f) };
            _versionList.DoubleClick += (s, e) => LaunchSelected();

            var refresh = new Button { Text = "刷新", Location = new Point(12, 340), Size = new Size(80, 30) };
            refresh.Click += (s, e) => { RefreshVersions(); RefreshJava(); };
            var launch = new Button { Text = "▶ 启动选中版本", Location = new Point(100, 340), Size = new Size(150, 30) };
            launch.Click += (s, e) => LaunchSelected();

            var dlLabel = new Label { Text = "下载原版版本", Location = new Point(465, 10), AutoSize = true };
            _downloadBox = new TextBox { Location = new Point(465, 32), Width = 180, Text = "1.21.1" };
            var skipAssets = new CheckBox { Text = "跳过资源（快，但没有音效贴图）", Location = new Point(465, 58), AutoSize = true };
            var dlBtn = new Button { Text = "开始下载", Location = new Point(465, 84), Size = new Size(120, 30) };
            dlBtn.Click += (s, e) => RunDownload(skipAssets.Checked);

            var javaLabel = new Label { Text = "Java（自动按版本匹配）", Location = new Point(465, 130), AutoSize = true };
            _javaCombo = new ComboBox { Location = new Point(465, 152), Width = 440, DropDownStyle = ComboBoxStyle.DropDownList };
            var reJava = new Button { Text = "重新检测 Java", Location = new Point(465, 182), Size = new Size(120, 28) };
            reJava.Click += (s, e) => RefreshJava();

            var memLabel = new Label { Text = "最大内存", Location = new Point(465, 222), AutoSize = true };
            _memBox = new TextBox { Location = new Point(465, 244), Width = 80, Text = "2G" };

            page.Controls.AddRange(new Control[]
            {
                listLabel, _versionList, refresh, launch,
                dlLabel, _downloadBox, skipAssets, dlBtn,
                javaLabel, _javaCombo, reJava, memLabel, _memBox,
            });
            return page;
        }

        private TabPage BuildLoaderTab()
        {
            var page = new TabPage("  加载器  ");
            page.Controls.Add(new Label
            {
                Text = "给某个 MC 版本装加载器（自动下载原版 + 安装 + 补库）",
                Location = new Point(14, 14),
                AutoSize = true,
                ForeColor = Color.DimGray,
            });

            page.Controls.Add(new Label { Text = "加载器", Location = new Point(14, 50), AutoSize = true });
            _loaderKind = new ComboBox
            {
                Location = new Point(90, 46),
                Width = 160,
                DropDownStyle = ComboBoxStyle.DropDownList,
            };
            _loaderKind.Items.AddRange(new object[] { "fabric", "quilt", "legacyfabric", "forge", "neoforge" });
            _loaderKind.SelectedIndex = 0;

            page.Controls.Add(new Label { Text = "版本", Location = new Point(14, 88), AutoSize = true });
            _loaderVer = new TextBox { Location = new Point(90, 84), Width = 260, Text = "1.20.1" };

            var install = new Button { Text = "安装加载器", Location = new Point(90, 122), Size = new Size(130, 32) };
            install.Click += (s, e) => RunLoader();

            page.Controls.Add(new Label
            {
                Text = "提示：\n· fabric / quilt / legacyfabric：版本写 MC 版本即可，自动挑最新 stable\n" +
                       "· forge：写 1.7.10 或 1.20.1-47.2.0 都行（自动补全）\n" +
                       "· neoforge：写 21.1.264",
                Location = new Point(14, 170),
                AutoSize = true,
                ForeColor = Color.DimGray,
            });

            page.Controls.AddRange(new Control[] { _loaderKind, _loaderVer, install });
            return page;
        }

        private TabPage BuildPackTab()
        {
            var page = new TabPage("  整合包  ");
            page.Controls.Add(new Label
            {
                Text = "支持 Modrinth .mrpack / MultiMC·Prism 实例包 / CurseForge zip（自动识别）",
                Location = new Point(14, 14),
                AutoSize = true,
                ForeColor = Color.DimGray,
            });
            page.Controls.Add(new Label { Text = "包文件", Location = new Point(14, 52), AutoSize = true });
            var path = new TextBox { Location = new Point(90, 48), Width = 660 };
            var browse = new Button { Text = "浏览...", Location = new Point(760, 46), Size = new Size(90, 28) };
            browse.Click += (s2, e2) =>
            {
                using (var dlg = new OpenFileDialog())
                {
                    dlg.Filter = "整合包|*.mrpack;*.zip|所有文件|*.*";
                    if (dlg.ShowDialog() == DialogResult.OK) path.Text = dlg.FileName;
                }
            };
            var install = new Button { Text = "安装这个整合包", Location = new Point(90, 88), Size = new Size(140, 32) };
            install.Click += (s2, e2) =>
            {
                if (path.Text.Trim().Length == 0) { MessageBox.Show("先选一个整合包文件", "提示"); return; }
                string file = path.Text.Trim();
                RunInBackground("安装 " + System.IO.Path.GetFileName(file),
                    () => Packs.Install(new[] { "install", file, "--dir", _baseDir }));
            };
            page.Controls.AddRange(new Control[] { path, browse, install });
            return page;
        }

        private TabPage BuildManageTab()
        {
            var page = new TabPage("  管理  ");
            page.Controls.Add(new Label
            {
                Text = "所有本地版本：原版 / 整合包 / 外部导入，都能体检、测试、生成启动器",
                Location = new Point(12, 12),
                AutoSize = true,
            });

            _manageList = new ListBox { Location = new Point(12, 36), Size = new Size(890, 300), Font = new Font("Microsoft YaHei UI", 9f) };

            var refresh = new Button { Text = "刷新", Location = new Point(12, 344), Size = new Size(80, 30) };
            refresh.Click += (s, e) => RefreshVersions();
            var verify = new Button { Text = "体检", Location = new Point(100, 344), Size = new Size(80, 30) };
            verify.Click += (s, e) => RunOnSelected("verify");
            var test = new Button { Text = "测试启动", Location = new Point(188, 344), Size = new Size(100, 30) };
            test.Click += (s, e) => RunOnSelected("test");
            var mkbat = new Button { Text = "生成启动器", Location = new Point(296, 344), Size = new Size(110, 30) };
            mkbat.Click += (s, e) => RunOnSelected("mkbat");
            var openDir = new Button { Text = "打开目录", Location = new Point(414, 344), Size = new Size(90, 30) };
            openDir.Click += (s, e) => OpenSelectedDir();

            page.Controls.AddRange(new Control[] { _manageList, refresh, verify, test, mkbat, openDir });
            return page;
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

        // ---------------- 数据 ----------------
        private void RefreshVersions()
        {
            _versionList.Items.Clear();
            _manageList.Items.Clear();
            string root = Path.Combine(_baseDir, "versions");
            if (!Directory.Exists(root))
            {
                AppendLog("还没有本地版本，先下载一个。");
                return;
            }

            var names = Directory.GetDirectories(root).Select(Path.GetFileName).OrderBy(x => x).ToList();
            int packs = 0;
            foreach (string name in names)
            {
                string dir = Path.Combine(root, name);
                string tag = File.Exists(Path.Combine(dir, "instance.json"))
                    ? "[整合包]"
                    : (Directory.Exists(Path.Combine(dir, "mods")) ? "[外部]" : "[原版]");
                if (tag == "[整合包]") packs++;
                _versionList.Items.Add(name);
                _manageList.Items.Add(name + "   " + tag);
            }
            AppendLog(string.Format("找到 {0} 个本地版本（其中 {1} 个整合包）", names.Count, packs));
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
        private void RefreshAll()
        {
            RefreshVersions();
            RefreshJava();
        }

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
            if (Directory.Exists(dir)) Process.Start("explorer.exe", "\"" + dir + "\"");
            else MessageBox.Show("目录不存在：" + dir, "提示");
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
            if (InvokeRequired) { BeginInvoke(new Action<string>(AppendLog), text); return; }
            _log.AppendText(text + Environment.NewLine);
        }

        /// <summary>把 Console 输出接到 TextBox。</summary>
        private class TextBoxWriter : TextWriter
        {
            private readonly MainForm _owner;
            public TextBoxWriter(MainForm owner) { _owner = owner; }
            public override Encoding Encoding { get { return Encoding.UTF8; } }
            public override void Write(string value) { if (!string.IsNullOrEmpty(value)) _owner.AppendLog(value.TrimEnd()); }
            public override void WriteLine(string value) { _owner.AppendLog(value); }
        }
    }
}
