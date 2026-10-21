using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Mcroute
{
    /// <summary>CurseForge 搜索结果。</summary>
    public class CfHit
    {
        public long ModId;
        public string Slug;
        public string Name;
        public string Summary;
        public string Author;
        public string LogoUrl;
        public long Downloads;
        public string Categories;

        public string DownloadsText()
        {
            if (Downloads >= 1000000) return (Downloads / 1000000.0).ToString("F1") + "M";
            if (Downloads >= 1000) return (Downloads / 1000.0).ToString("F0") + "K";
            return Downloads.ToString();
        }
    }

    /// <summary>
    /// CurseForge 客户端。需要用户自己的 API Key（<数据目录>/cf_api_key.txt 或环境变量 CF_API_KEY）。
    ///
    /// 注意：部分 mod 作者关闭了第三方分发，文件的 downloadUrl 会是 null —— 这时只能引导用户开网页。
    /// </summary>
    public static class CurseForge
    {
        public const string Api = "https://api.curseforge.com/v1";
        public const int GameId = 432;   // Minecraft

        public static string Key(string baseDir)
        {
            try
            {
                string env = Environment.GetEnvironmentVariable("CF_API_KEY");
                if (!string.IsNullOrEmpty(env)) return env.Trim();
                string file = Path.Combine(baseDir ?? ".", "cf_api_key.txt");
                if (File.Exists(file)) return File.ReadAllText(file).Trim();
            }
            catch { }
            return null;
        }

        public static bool HasKey(string baseDir)
        {
            return !string.IsNullOrEmpty(Key(baseDir));
        }

        /// <summary>界面加载器名 → CurseForge modLoaderType。</summary>
        public static int? LoaderType(string loaderName)
        {
            if (string.IsNullOrEmpty(loaderName)) return null;
            string s = loaderName.ToLowerInvariant();
            if (s.Contains("neoforge")) return 6;
            if (s.Contains("quilt")) return 5;
            if (s.Contains("fabric")) return 4;
            if (s.Contains("forge")) return 1;
            return null;
        }

        public static List<CfHit> Search(string baseDir, string query, string mc, string loader, int limit, out string error)
        {
            error = null;
            var list = new List<CfHit>();
            string key = Key(baseDir);
            if (string.IsNullOrEmpty(key)) { error = "没有配置 CurseForge API Key（cf_api_key.txt）"; return list; }

            try
            {
                var q = new List<string>
                {
                    "gameId=" + GameId,
                    "searchFilter=" + Uri.EscapeDataString(query ?? ""),
                    "pageSize=" + Math.Max(1, Math.Min(50, limit)),
                    "sortField=2",          // 2 = 下载量
                    "sortOrder=desc",
                };
                if (!string.IsNullOrEmpty(mc)) q.Add("gameVersion=" + Uri.EscapeDataString(mc));
                int? lt = LoaderType(loader);
                if (lt.HasValue) q.Add("modLoaderType=" + lt.Value);

                string url = Api + "/mods/search?" + string.Join("&", q);
                object json = GetJson(url, key);
                foreach (object m in Version.AsList(J.Get(json, "data")) ?? new List<object>())
                {
                    var hit = new CfHit
                    {
                        ModId = (long)J.Int(m, "id", 0),
                        Slug = J.Str(m, "slug"),
                        Name = J.Str(m, "name"),
                        Summary = J.Str(m, "summary"),
                        Downloads = (long)J.Int(m, "downloadCount", 0),
                        LogoUrl = J.Str(J.Dict(m, "logo"), "thumbnailUrl"),
                    };
                    var authors = Version.AsList(J.Get(m, "authors"));
                    if (authors != null && authors.Count > 0) hit.Author = J.Str(authors[0], "name");
                    var cats = Version.AsList(J.Get(m, "categories"));
                    if (cats != null)
                        hit.Categories = string.Join(" ", cats.Select(c => J.Str(c, "name")).Where(s => !string.IsNullOrEmpty(s)).Take(3));
                    if (hit.ModId > 0 && !string.IsNullOrEmpty(hit.Name)) list.Add(hit);
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
            return list;
        }

        /// <summary>取适配文件（原始 JSON 列表，按文件日期倒序由 API 保证）。</summary>
        public static List<object> Files(string baseDir, long modId, string mc, string loader, out string error)
        {
            error = null;
            string key = Key(baseDir);
            if (string.IsNullOrEmpty(key)) { error = "没有配置 CurseForge API Key"; return new List<object>(); }
            try
            {
                var q = new List<string> { "pageSize=20" };
                if (!string.IsNullOrEmpty(mc)) q.Add("gameVersion=" + Uri.EscapeDataString(mc));
                int? lt = LoaderType(loader);
                if (lt.HasValue) q.Add("modLoaderType=" + lt.Value);

                string url = Api + "/mods/" + modId + "/files?" + string.Join("&", q);
                object json = GetJson(url, key);
                return Version.AsList(J.Get(json, "data")) ?? new List<object>();
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return new List<object>();
            }
        }

        /// <summary>安装：挑第一个能直链下载的适配文件。</summary>
        public static bool Install(string baseDir, string verName, long modId, string mc, string loader, out string message)
        {
            message = "";
            string err;
            var files = Files(baseDir, modId, mc, loader, out err);
            if (!string.IsNullOrEmpty(err)) { message = "查询文件失败：" + err; return false; }
            if (files.Count == 0) { message = "没有适配 " + mc + " / " + (loader ?? "-") + " 的文件"; return false; }

            object chosen = null;
            foreach (object f in files)
            {
                if (!string.IsNullOrEmpty(J.Str(f, "downloadUrl"))) { chosen = f; break; }
            }
            if (chosen == null)
            {
                message = "这个 mod 的文件都禁止第三方分发（downloadUrl 为空）。\n" +
                          "请打开网页手动下载：https://www.curseforge.com/projects/" + modId;
                return false;
            }

            string url = J.Str(chosen, "downloadUrl");
            string filename = J.Str(chosen, "fileName");
            string sha1 = J.Str(J.Dict(chosen, "hashes"), "sha1");
            if (string.IsNullOrEmpty(filename)) filename = "mod-" + modId + ".jar";

            string modsDir = Path.Combine(baseDir, "versions", verName, "mods");
            Directory.CreateDirectory(modsDir);
            string dest = Path.Combine(modsDir, filename);
            if (File.Exists(dest)) { message = "已存在：" + filename; return true; }

            string info;
            string status = Http.Download(url, dest, string.IsNullOrEmpty(sha1) ? null : sha1, out info, true);
            if (status == "fail") { message = "下载失败：" + info; return false; }

            // 必需前置（relationType=3）
            var required = new List<long>();
            foreach (object dep in Version.AsList(J.Get(chosen, "dependencies")) ?? new List<object>())
            {
                int rel = J.Int(dep, "relationType", 0);
                long depId = (long)J.Int(dep, "modId", 0);
                if (rel == 3 && depId > 0) required.Add(depId);
            }

            message = "已安装 " + filename;
            if (required.Count > 0)
            {
                var names = ModNames(baseDir, required);
                message += "\n注意：这个 mod 需要前置：" + string.Join("、", names) + "\n（请手动安装，避免装错版本）";
            }
            return true;
        }

        /// <summary>批量查 mod 名（POST /v1/mods）。</summary>
        public static List<string> ModNames(string baseDir, List<long> ids)
        {
            var result = new List<string>();
            if (ids == null || ids.Count == 0) return result;
            string key = Key(baseDir);
            if (string.IsNullOrEmpty(key)) { result.AddRange(ids.Select(i => i.ToString())); return result; }
            try
            {
                string body = "{\"modIds\":[" + string.Join(",", ids) + "]}";
                object json = PostJson(Api + "/mods", key, body);
                var arr = Version.AsList(J.Get(json, "data"));
                if (arr != null)
                    foreach (object m in arr)
                    {
                        string n = J.Str(m, "name");
                        if (!string.IsNullOrEmpty(n)) result.Add(n);
                    }
            }
            catch { }
            if (result.Count == 0) result.AddRange(ids.Select(i => i.ToString()));
            return result;
        }

        private static object GetJson(string url, string key)
        {
            var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
            req.UserAgent = "mcroute (https://github.com/Hourai-chiyo/mcroute)";
            req.Accept = "application/json";
            req.Headers["x-api-key"] = key;
            req.Timeout = 30000;
            using (var resp = (System.Net.HttpWebResponse)req.GetResponse())
            using (var stream = resp.GetResponseStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
                return J.Parse(reader.ReadToEnd());
        }

        private static object PostJson(string url, string key, string body)
        {
            var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
            req.Method = "POST";
            req.UserAgent = "mcroute (https://github.com/Hourai-chiyo/mcroute)";
            req.Accept = "application/json";
            req.ContentType = "application/json";
            req.Headers["x-api-key"] = key;
            req.Timeout = 30000;
            byte[] data = Encoding.UTF8.GetBytes(body);
            req.ContentLength = data.Length;
            using (var s = req.GetRequestStream()) s.Write(data, 0, data.Length);
            using (var resp = (System.Net.HttpWebResponse)req.GetResponse())
            using (var stream = resp.GetResponseStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
                return J.Parse(reader.ReadToEnd());
        }
    }
}
