using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Mcroute
{
    /// <summary>MC百科（mcmod.cn）搜索结果条目。</summary>
    public class McmodHit
    {
        public string Name;        // 中文名（含英文括注）
        public string Url;         // https://www.mcmod.cn/class/xxxx.html
        public string Summary;     // 简介（截断）
        public string Snapshot;    // 快照时间
    }

    /// <summary>
    /// MC百科搜索（只搜索 + 展示 + 跳转，不抓详情页、不下载）。
    ///
    /// mcmod 没有公开 API，这里解析 /s?key= 的结果 HTML：
    ///   &lt;div class="result-item"&gt; ... &lt;a href="...class/2785.html"&gt;钠 (Sodium)&lt;/a&gt;
    ///   ... &lt;div class="body"&gt;简介…&lt;/div&gt; ... 快照时间：2026-10-17
    /// 页面结构变化时返回空列表，不抛异常。
    /// </summary>
    public static class Mcmod
    {
        public const string SearchUrl = "https://search.mcmod.cn/s?key=";
        private const string BrowserUa =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36";

        public static List<McmodHit> Search(string query, int limit, out string error)
        {
            error = null;
            var result = new List<McmodHit>();
            if (string.IsNullOrWhiteSpace(query)) { error = "关键词为空"; return result; }

            string url = SearchUrl + Uri.EscapeDataString(query.Trim());
            string html;
            try { html = Http.GetString(url, 25); }
            catch (Exception ex) { error = "请求失败：" + ex.Message; return result; }

            if (string.IsNullOrEmpty(html)) { error = "返回为空（可能被风控/需要人机验证）"; return result; }
            if (html.IndexOf("result-item", StringComparison.Ordinal) < 0)
            {
                error = "页面里没有结果（可能被风控或无结果）";
                return result;
            }

            result = ParseHtml(html, limit);
            if (result.Count == 0) error = "没有解析出结果（mcmod 页面结构可能变了）";
            return result;
        }

        /// <summary>解析搜索结果 HTML（公开给单元测试用，不联网）。</summary>
        public static List<McmodHit> ParseHtml(string html, int limit)
        {
            var result = new List<McmodHit>();
            if (string.IsNullOrEmpty(html) || limit <= 0) return result;
            foreach (string block in SplitBlocks(html))
            {
                var hit = ParseBlock(block);
                if (hit == null || string.IsNullOrEmpty(hit.Url)) continue;
                result.Add(hit);
                if (result.Count >= limit) break;
            }
            return result;
        }

        /// <summary>按 &lt;div class="result-item"&gt; 切块。</summary>
        private static List<string> SplitBlocks(string html)
        {
            var list = new List<string>();
            const string marker = "<div class=\"result-item\"";
            int pos = 0;
            while (true)
            {
                int i = html.IndexOf(marker, pos, StringComparison.Ordinal);
                if (i < 0) break;
                int next = html.IndexOf(marker, i + marker.Length, StringComparison.Ordinal);
                if (next < 0) next = html.Length;
                list.Add(html.Substring(i, next - i));
                pos = next;
            }
            return list;
        }

        private static McmodHit ParseBlock(string block)
        {
            var hit = new McmodHit();

            // 标题链接：head 里第一个 class/... 链接
            var headMatch = Regex.Match(block,
                "<a[^>]*href=\"(https?://[^\"]*mcmod\\.cn/class/[^\"]+)\"[^>]*>(.*?)</a>",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (!headMatch.Success) return null;

            hit.Url = headMatch.Groups[1].Value;
            hit.Name = CleanText(headMatch.Groups[2].Value);
            if (string.IsNullOrEmpty(hit.Name)) return null;

            // 简介
            var bodyMatch = Regex.Match(block,
                "<div class=\"body\">(.*?)</div>",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (bodyMatch.Success) hit.Summary = Shorten(CleanText(bodyMatch.Groups[1].Value), 160);

            // 快照时间
            var snap = Regex.Match(block, "快照时间：</span>\\s*<span class=\"value\">([^<]+)</span>",
                RegexOptions.Singleline);
            if (snap.Success) hit.Snapshot = snap.Groups[1].Value.Trim();

            return hit;
        }

        /// <summary>去标签、去 BBCode 前缀、解实体、压空白。</summary>
        private static string CleanText(string html)
        {
            if (string.IsNullOrEmpty(html)) return "";
            string s = Regex.Replace(html, "<[^>]+>", " ");
            s = WebUtility.HtmlDecode(s);
            s = s.Replace('\u00a0', ' ');
            // 去掉 [h1=概述] [ban:title_menu] 这类 BBCode
            s = Regex.Replace(s, @"\[[^\[\]]{1,40}\]", " ");
            s = Regex.Replace(s, @"\s+", " ").Trim();
            return s;
        }

        private static string Shorten(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s;
            return s.Substring(0, max).TrimEnd() + "…";
        }

        /// <summary>用浏览器打开某条目。</summary>
        public static bool OpenInBrowser(string url)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true,
                });
                return true;
            }
            catch { return false; }
        }
    }
}
