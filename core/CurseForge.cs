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
            var files = FilesFor(baseDir, modId, mc, loader, out err);
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
            filename = SafePath.FileNameOnly(filename);
            if (string.IsNullOrEmpty(filename)) filename = "mod-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".jar";
            string dest;
            if (!SafePath.TryResolveInside(modsDir, filename, out dest, out _))
            {
                message = "文件名不安全：" + filename;
                return false;
            }
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

        // ---------------- 文件指纹识别（把本地 jar 对应到 CurseForge 项目） ----------------

        /// <summary>指纹匹配结果（一个本地文件对应 CurseForge 的一个文件）。</summary>
        public class CfFileHit
        {
            public long ModId;
            public long FileId;
            public string FileName;
            public string DisplayName;
            public string DownloadUrl;
            public List<string> Sha1 = new List<string>();
        }

        /// <summary>CurseForge 项目信息（名称 / slug / 项目页）。</summary>
        public class CfModMeta
        {
            public long Id;
            public string Name;
            public string Slug;
            public string Url;
        }

        /// <summary>批量指纹反查：fingerprint → 匹配到的文件。失败返回空表。</summary>
        public static List<CfFileHit> MatchFingerprints(string baseDir, List<long> fingerprints, out string error)
        {
            error = null;
            var list = new List<CfFileHit>();
            string key = Key(baseDir);
            if (string.IsNullOrEmpty(key)) { error = "没有配置 CurseForge API Key（cf_api_key.txt）"; return list; }
            if (fingerprints == null || fingerprints.Count == 0) return list;

            try
            {
                var body = new StringBuilder("{\"fingerprints\":[");
                for (int i = 0; i < fingerprints.Count; i++)
                {
                    if (i > 0) body.Append(",");
                    body.Append(fingerprints[i]);
                }
                body.Append("]}");

                object json = PostJson(Api + "/fingerprints", key, body.ToString());
                list = ParseFingerprintResponse(J.Serialize(J.Get(json, "data")));
                if (list.Count == 0)
                {
                    // 再看有没有未匹配的（用于给出"没识别到"的提示）
                    var unmatched = Version.AsList(J.Get(J.Get(json, "data"), "unmatchedFingerprints"));
                    if (unmatched != null && unmatched.Count > 0) error = unmatched.Count + " 个文件 CurseForge 不认识";
                }
            }
            catch (Exception ex) { error = ex.Message; }
            return list;
        }

        /// <summary>解析 POST /v1/mods/fingerprints 的 data 部分（纯函数，可单测）。</summary>
        public static List<CfFileHit> ParseFingerprintResponse(string dataJson)
        {
            var list = new List<CfFileHit>();
            if (string.IsNullOrEmpty(dataJson)) return list;
            object data;
            try { data = J.Parse(dataJson); }
            catch { return list; }

            foreach (var m in Version.AsList(J.Get(data, "exactMatches")))
            {
                var file = J.Dict(m, "file");
                if (file == null) continue;

                var hit = new CfFileHit
                {
                    ModId = J.Int(m, "id", J.Int(file, "modId", 0)),
                    FileId = J.Int(file, "id", 0),
                    FileName = J.Str(file, "fileName"),
                    DisplayName = J.Str(file, "displayName"),
                    DownloadUrl = J.Str(file, "downloadUrl"),
                };
                foreach (var h in Version.AsList(J.Get(file, "hashes")))
                {
                    if (J.Int(h, "algo", -1) != 1) continue;   // 1 = sha1
                    string v = J.Str(h, "value");
                    if (!string.IsNullOrEmpty(v)) hit.Sha1.Add(v.ToLowerInvariant());
                }
                if (hit.ModId > 0) list.Add(hit);
            }
            return list;
        }

        /// <summary>批量取项目信息（名称 / slug）。</summary>
        public static Dictionary<long, CfModMeta> ModMeta(string baseDir, List<long> modIds, out string error)
        {
            error = null;
            var map = new Dictionary<long, CfModMeta>();
            string key = Key(baseDir);
            if (string.IsNullOrEmpty(key) || modIds == null || modIds.Count == 0) return map;

            try
            {
                for (int i = 0; i < modIds.Count; i += 50)
                {
                    var batch = modIds.Skip(i).Take(50).ToList();
                    var body = new StringBuilder("{\"modIds\":[");
                    for (int k = 0; k < batch.Count; k++)
                    {
                        if (k > 0) body.Append(",");
                        body.Append(batch[k]);
                    }
                    body.Append("]}");

                    object json = PostJson(Api + "/mods", key, body.ToString());
                    foreach (var m in Version.AsList(J.Get(json, "data")))
                    {
                        long id = J.Int(m, "id", 0);
                        if (id <= 0) continue;
                        var meta = new CfModMeta
                        {
                            Id = id,
                            Name = J.Str(m, "name"),
                            Slug = J.Str(m, "slug"),
                        };
                        string website = J.Str(J.Dict(m, "links"), "websiteUrl");
                        if (!string.IsNullOrEmpty(website)) meta.Url = website;
                        else if (!string.IsNullOrEmpty(meta.Slug))
                            meta.Url = "https://www.curseforge.com/minecraft/mc-mods/" + meta.Slug;
                        map[id] = meta;
                    }
                }
            }
            catch (Exception ex) { error = ex.Message; }
            return map;
        }

        /// <summary>
        /// 取「确定适配」的文件：API 的 gameVersion/modLoaderType 过滤并不总是可靠，
        /// 这里再按文件自带的 gameVersions 数组做一次客户端过滤（1.12.2 / Forge 等）。
        /// </summary>
        public static List<object> FilesFor(string baseDir, long modId, string mc, string loader, out string error)
        {
            var all = Files(baseDir, modId, mc, loader, out error);
            if (string.IsNullOrEmpty(mc)) return all;

            string loaderTag = null;
            if (!string.IsNullOrEmpty(loader))
            {
                string l = loader.ToLowerInvariant();
                if (l.Contains("neoforge")) loaderTag = "NeoForge";
                else if (l.Contains("forge")) loaderTag = "Forge";
                else if (l.Contains("fabric")) loaderTag = "Fabric";
                else if (l.Contains("quilt")) loaderTag = "Quilt";
            }

            var filtered = new List<object>();
            foreach (var f in all)
            {
                var versions = Version.AsList(J.Get(f, "gameVersions"));
                bool mcOk = false, loaderOk = loaderTag == null;
                foreach (var v in versions)
                {
                    string sv = v as string;
                    if (string.IsNullOrEmpty(sv)) continue;
                    if (string.Equals(sv, mc, StringComparison.OrdinalIgnoreCase)) mcOk = true;
                    if (loaderTag != null && string.Equals(sv, loaderTag, StringComparison.OrdinalIgnoreCase)) loaderOk = true;
                }
                if (mcOk && loaderOk) filtered.Add(f);
            }

            // 过滤后为空时，只对"完全没带 gameVersions 标签"的老条目放行；
            // 有标签但不匹配的文件不能再退回（否则会给 1.20 装 1.19 的 mod）
            if (filtered.Count > 0) return filtered;

            var noTags = new List<object>();
            foreach (var f in all)
            {
                var versions = Version.AsList(J.Get(f, "gameVersions"));
                bool any = false;
                foreach (var v in versions) { if (!string.IsNullOrEmpty(v as string)) { any = true; break; } }
                if (!any) noTags.Add(f);
            }
            return noTags;
        }

        /// <summary>该 mod 在指定 MC 版本 + 加载器下的最新文件（原始 JSON，可能为 null）。</summary>
        public static object LatestFile(string baseDir, long modId, string mc, string loader, out string error)
        {
            var files = FilesFor(baseDir, modId, mc, loader, out error);
            return files.Count > 0 ? files[0] : null;
        }

        private static object GetJson(string url, string key)
        {
            var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
            req.UserAgent = "Kyouka-MCLauncher/1.10 (+https://github.com/Hourai-chiyo/Kyouka-MCLauncher)";
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
            req.UserAgent = "Kyouka-MCLauncher/1.10 (+https://github.com/Hourai-chiyo/Kyouka-MCLauncher)";
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
