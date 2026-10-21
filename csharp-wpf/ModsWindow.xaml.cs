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
    /// <summary>本地 mods 列表窗口：列出 / 启用 / 禁用 / 删除。</summary>
    public partial class ModsWindow : Window
    {
        private readonly string _baseDir;
        private readonly string _version;
        private List<ModInfo> _mods = new List<ModInfo>();

        public ModsWindow(string baseDir, string version)
        {
            InitializeComponent();
            _baseDir = baseDir;
            _version = version;
            TitleText.Text = "Mods — " + version;

            Loaded += (s, e) => { Reload(); ApplyTheme(); };
        }

        private void ApplyTheme()
        {
            try { Theme.Apply(Theme.Effective); } catch { }
        }

        private void Reload()
        {
            try
            {
                _mods = Mods.Scan(_baseDir, _version);
                ModList.Items.Clear();
                foreach (var m in _mods)
                    ModList.Items.Add(MakeItem(m));

                int on = _mods.Count(x => !x.Disabled);
                int off = _mods.Count - on;
                SummaryText.Text = string.Format("共 {0} 个 · 启用 {1} · 禁用 {2}", _mods.Count, on, off);

                UpdateButtons();
            }
            catch (Exception ex)
            {
                MessageBox.Show("读取 mods 失败：" + ex.Message, "提示");
            }
        }

        private static ListBoxItem MakeItem(ModInfo m)
        {
            var panel = new Grid();
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel();

            var name = new TextBlock
            {
                Text = m.Name,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            name.SetResourceReference(TextBlock.ForegroundProperty, "Text");

            var sub = new TextBlock
            {
                Text = string.Format("{0} · {1} · {2} · {3}", m.Version, m.Loader, m.SizeText(), m.FileName),
                FontSize = 11,
                Margin = new Thickness(0, 3, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            sub.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");

            left.Children.Add(name);
            left.Children.Add(sub);
            Grid.SetColumn(left, 0);
            panel.Children.Add(left);

            var state = new TextBlock
            {
                Text = m.Disabled ? "已禁用" : "启用中",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 2, 0),
            };
            state.SetResourceReference(TextBlock.ForegroundProperty, m.Disabled ? "TextFaint" : "Primary");
            Grid.SetColumn(state, 1);
            panel.Children.Add(state);

            return new ListBoxItem { Content = panel, Tag = m };
        }

        private ModInfo Selected()
        {
            var item = ModList.SelectedItem as ListBoxItem;
            return item == null ? null : item.Tag as ModInfo;
        }

        private void UpdateButtons()
        {
            var m = Selected();
            if (EnableBtn != null) EnableBtn.IsEnabled = m != null && m.Disabled;
            if (DisableBtn != null) DisableBtn.IsEnabled = m != null && !m.Disabled;
            if (DeleteBtn != null) DeleteBtn.IsEnabled = m != null;
        }

        private void ModList_SelectionChanged(object sender, SelectionChangedEventArgs e) { UpdateButtons(); }

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
            Reload();
        }

        private void Enable_Click(object sender, RoutedEventArgs e)
        {
            var m = Selected();
            if (m == null) return;
            string error;
            if (!Mods.SetEnabled(m, true, out error)) MessageBox.Show("启用失败：" + error, "提示");
            Reload();
        }

        private void Disable_Click(object sender, RoutedEventArgs e)
        {
            var m = Selected();
            if (m == null) return;
            string error;
            if (!Mods.SetEnabled(m, false, out error)) MessageBox.Show("禁用失败：" + error, "提示");
            Reload();
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
            Reload();
        }

        private void Refresh_Click(object sender, RoutedEventArgs e) { Reload(); }

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

                // 和主界面一样：优先 Shell API，避免 explorer.exe 二次启动问题
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
            catch (Exception ex) { MessageBox.Show("打开失败：" + ex.Message, "提示"); }
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }

        private void Close_Click(object sender, RoutedEventArgs e) { Close(); }
    }
}
