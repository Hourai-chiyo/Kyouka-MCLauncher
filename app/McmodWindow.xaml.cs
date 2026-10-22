using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Mcroute
{
    /// <summary>
    /// MC百科（mcmod.cn）搜索窗口：只搜索 + 展示中文名/简介 + 跳转，不抓详情页、不在这里下载。
    /// </summary>
    public partial class McmodWindow : Window
    {
        private List<McmodHit> _hits = new List<McmodHit>();

        public McmodWindow(string initialQuery = null)
        {
            InitializeComponent();
            Loaded += (s, e) =>
            {
                StatusText.Text = "搜索 Mod 的中文名和简介（数据来自 MC百科）";
                if (!string.IsNullOrWhiteSpace(initialQuery))
                {
                    SearchBox.Text = initialQuery;
                    Search_Click(this, new RoutedEventArgs());
                }
                else SearchBox.Focus();
            };
        }

        /// <summary>测试/截图用：直接搜索。</summary>
        public void SearchNow(string query)
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
            HitList.Items.Clear();

            var t = new System.Threading.Tasks.Task(() =>
            {
                string err;
                var hits = Mcmod.Search(q, 20, out err);
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _hits = hits;
                    HitList.Items.Clear();
                    foreach (var h in hits) HitList.Items.Add(MakeItem(h));
                    if (hits.Count == 0)
                        StatusText.Text = string.IsNullOrEmpty(err) ? "没有结果" : err;
                    else
                        StatusText.Text = "找到 " + hits.Count + " 条（双击打开百科页）";
                    if (HitList.Items.Count > 0) HitList.SelectedIndex = 0;
                }));
            });
            t.Start();
        }

        private ListBoxItem MakeItem(McmodHit h)
        {
            var panel = new StackPanel();
            var title = new TextBlock
            {
                Text = h.Name,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            title.SetResourceReference(TextBlock.ForegroundProperty, "Text");

            var meta = new TextBlock
            {
                Text = h.Url + (string.IsNullOrEmpty(h.Snapshot) ? "" : "  ·  快照 " + h.Snapshot),
                FontSize = 10,
                Margin = new Thickness(0, 2, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            meta.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");

            var desc = new TextBlock
            {
                Text = h.Summary,
                FontSize = 11,
                Margin = new Thickness(0, 3, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 34,
            };
            desc.SetResourceReference(TextBlock.ForegroundProperty, "TextSub");

            panel.Children.Add(title);
            panel.Children.Add(meta);
            panel.Children.Add(desc);
            return new ListBoxItem { Content = panel, Tag = h };
        }

        private McmodHit Selected()
        {
            var item = HitList.SelectedItem as ListBoxItem;
            return item == null ? null : item.Tag as McmodHit;
        }

        private void HitList_MouseDoubleClick(object sender, MouseButtonEventArgs e) { OpenSelected(); }

        private void Open_Click(object sender, RoutedEventArgs e) { OpenSelected(); }

        private void OpenSelected()
        {
            var h = Selected();
            if (h == null) { StatusText.Text = "先选一条"; return; }
            if (Mcmod.OpenInBrowser(h.Url)) StatusText.Text = "已在浏览器打开：" + h.Name;
            else
            {
                try { Clipboard.SetText(h.Url); } catch { }
                StatusText.Text = "没调起浏览器，链接已复制";
            }
        }

        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            var h = Selected();
            if (h == null) { StatusText.Text = "先选一条"; return; }
            try { Clipboard.SetText(h.Url); StatusText.Text = "已复制：" + h.Url; }
            catch (Exception ex) { StatusText.Text = "复制失败：" + ex.Message; }
        }

        private void BrowserSearch_Click(object sender, RoutedEventArgs e)
        {
            string q = SearchBox.Text.Trim();
            string url = string.IsNullOrEmpty(q)
                ? "https://search.mcmod.cn/s"
                : Mcmod.SearchUrl + Uri.EscapeDataString(q);
            Mcmod.OpenInBrowser(url);
            StatusText.Text = "已在浏览器打开搜索页";
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }

        private void Close_Click(object sender, RoutedEventArgs e) { Close(); }
    }
}
