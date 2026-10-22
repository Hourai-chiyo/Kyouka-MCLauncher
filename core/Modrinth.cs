using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Mcroute
{
    /// <summary>Modrinth 的一个搜索结果。</summary>
    public class ModHit
    {
        public string ProjectId;
        public string Slug;
        public string Title;
        public string Description;
        public string Author;
        public string IconUrl;
        public long Downloads;
        public string Categories;      // "fabric · optimization"
        public List<string> Loaders = new List<string>();
        public List<string> Versions = new List<string>();

        public string DownloadsText()
        {
            if (Downloads >= 1000000) return (Downloads / 1000000.0).ToString("F1") + "M";
            if (Downloads >= 1000) return (Downloads / 1000.0).ToString("F0") + "K";
            return Downloads.ToString();
        }
    }

    /// <summary>
    /// Modrinth 客户端（公开 API，不需要 Key）。
    /// 只做三件事：搜索、查适配版本、下载安装到 mods/。
    ///
    /// 注意：CurseForge 需要用户自己的 API Key，另一套接口，暂未接入。
    /// </summary>
    public static class Modrinth
    {
        public const string Api = "https://api.modrinth.com/v2";

        private static string UA
        {
            get
            {
                string v = "0.4.2";
                try
                {
                    var asm = typeof(Modrinth).Assembly;
                    v = asm.GetName().Version.ToString(3);
                }
                catch { }
                return "mcroute/" + v + " (https://github.com/Hourai-chiyo/mcroute)";
            }
        }

        /// <summary>把界面上的加载器名映射成 Modrinth 的 loader id。</summary>
        public static string LoaderId(string loaderName)
        {
            if (string.IsNullOrEmpty(loaderName)) return null;
            string s = loaderName.ToLowerInvariant();
            if (s.Contains("neoforge")) return "neoforge";
            if (s.Contains("quilt")) return "quilt";
            if (s.Contains("fabric")) return "fabric";
            if (s.Contains("forge")) return "forge";
            return null;
        }

        /// <summary>搜索 mod。mcVersion / loader 可以为空（不过滤）。</summary>
        public static List<ModHit> Search(string query, string mcVersion, string loader, int limit, out string error)
        {
            return Search(query, mcVersion, loader, limit, "mod", out error);
        }

        /// <summary>projectType: "mod" / "resourcepack" / "shader"。</summary>
        public static List<ModHit> Search(string query, string mcVersion, string loader, int limit,
                                          string projectType, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(projectType)) projectType = "mod";
            var list = new List<ModHit>();
            try
            {
                var facets = new List<string> { "[\"project_type:" + projectType + "\"]" };
                if (!string.IsNullOrEmpty(mcVersion)) facets.Add("[\"versions:" + mcVersion + "\"]");
                string loaderId = LoaderId(loader);
                // 资源包/光影没有加载器概念
                if (projectType == "mod" && !string.IsNullOrEmpty(loaderId)) facets.Add("[\"categories:" + loaderId + "\"]");

                string facetsJson = "[" + string.Join(",", facets) + "]";
                string url = Api + "/search?query=" + Uri.EscapeDataString(query ?? "") +
                             "&limit=" + Math.Max(1, Math.Min(50, limit)) +
                             "&facets=" + Uri.EscapeDataString(facetsJson);

                object json = GetJson(url);
                foreach (object hit in Version.AsList(J.Get(json, "hits")))
                {
                    var m = new ModHit
                    {
                        ProjectId = J.Str(hit, "project_id"),
                        Slug = J.Str(hit, "slug"),
                        Title = J.Str(hit, "title"),
                        Description = J.Str(hit, "description"),
                        Author = J.Str(hit, "author"),
                        IconUrl = J.Str(hit, "icon_url"),
                        Downloads = (long)J.Int(hit, "downloads", 0),
                    };
                    foreach (object l in Version.AsList(J.Get(hit, "categories"))) m.Categories += (l as string) + " ";
                    m.Categories = m.Categories.Trim();
                    foreach (object l in Version.AsList(J.Get(hit, "loaders"))) if (l is string) m.Loaders.Add((string)l);
                    foreach (object v in Version.AsList(J.Get(hit, "versions"))) if (v is string) m.Versions.Add((string)v);
                    if (!string.IsNullOrEmpty(m.ProjectId) && !string.IsNullOrEmpty(m.Title)) list.Add(m);
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
            return list;
        }

        /// <summary>取某个项目适配当前 MC 版本 / 加载器的文件列表（原始 JSON，未解析）。</summary>
        public static List<object> Versions(string projectId, string mcVersion, string loader, out string error)
        {
            error = null;
            try
            {
                var q = new List<string>();
                if (!string.IsNullOrEmpty(mcVersion))
                    q.Add("game_versions=" + Uri.EscapeDataString("[\"" + mcVersion + "\"]"));
                string loaderId = LoaderId(loader);
                if (!string.IsNullOrEmpty(loaderId))
                    q.Add("loaders=" + Uri.EscapeDataString("[\"" + loaderId + "\"]"));

                string url = Api + "/project/" + Uri.EscapeDataString(projectId) + "/version";
                if (q.Count > 0) url += "?" + string.Join("&", q);

                return Version.AsList(GetJson(url)) ?? new List<object>();
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return new List<object>();
            }
        }

        /// <summary>
        /// 安装：取第一个适配版本的主文件，下到 <数据目录>/versions/<版本>/mods/。
        /// 返回 false 时 out message 是原因；成功时 message 里附带缺失的前置依赖提示。
        /// </summary>
        public static bool Install(string baseDir, string verName, string projectId,
                                   string mcVersion, string loader, out string message)
        {
            message = "";
            string err;
            var versions = Versions(projectId, mcVersion, loader, out err);
            if (!string.IsNullOrEmpty(err)) { message = "查询版本失败：" + err; return false; }
            if (versions.Count == 0)
            {
                message = "没有适配 " + mcVersion + " / " + (string.IsNullOrEmpty(loader) ? "任意加载器" : loader) + " 的版本";
                return false;
            }

            object first = versions[0];
            var files = Version.AsList(J.Get(first, "files"));
            if (files == null || files.Count == 0) { message = "这个版本没有文件"; return false; }

            object fileObj = files[0];
            string url = J.Str(fileObj, "url");
            string filename = J.Str(fileObj, "filename");
            string sha1 = "";
            var hashes = J.Get(fileObj, "hashes");
            if (hashes != null) sha1 = J.Str(hashes, "sha1");

            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(filename))
            {
                message = "文件信息不完整";
                return false;
            }

            string modsDir = Path.Combine(baseDir, "versions", verName, "mods");
            Directory.CreateDirectory(modsDir);
            string dest = Path.Combine(modsDir, filename);
            if (File.Exists(dest))
            {
                message = "已存在：" + filename;
                return true;
            }

            string info;
            string status = Http.Download(url, dest, string.IsNullOrEmpty(sha1) ? null : sha1, out info, true);
            if (status == "fail")
            {
                message = "下载失败：" + info;
                return false;
            }

            // 前置依赖提示（只提示，不自动装）
            var required = new List<string>();
            foreach (object dep in Version.AsList(J.Get(first, "dependencies")) ?? new List<object>())
            {
                if (J.Str(dep, "dependency_type") == "required")
                {
                    string pid = J.Str(dep, "project_id");
                    if (!string.IsNullOrEmpty(pid)) required.Add(pid);
                }
            }

            string ver = J.Str(first, "version_number");
            message = "已安装 " + filename + (string.IsNullOrEmpty(ver) ? "" : "（" + ver + "）");
            if (required.Count > 0)
            {
                var names = ProjectNames(required);
                message += "\n注意：这个 mod 需要前置：" + string.Join("、", names) + "\n（请手动安装，避免装错版本）";
            }
            return true;
        }

        /// <summary>
        /// 安装到指定类型：kind = "mod" / "resourcepack" / "shader"。
        /// 目标目录分别是 mods/ resourcepacks/ shaderpacks/。
        /// </summary>
        public static bool InstallToKind(string baseDir, string verName, string projectId,
                                         string mcVersion, string loader, string kind, out string message)
        {
            if (kind == "mod") return Install(baseDir, verName, projectId, mcVersion, loader, out message);

            message = "";
            string err;
            var versions = Versions(projectId, mcVersion, kind == "shader" ? null : loader, out err);
            if (!string.IsNullOrEmpty(err)) { message = "查询版本失败：" + err; return false; }
            if (versions.Count == 0) { message = "没有适配 " + mcVersion + " 的版本"; return false; }

            object first = versions[0];
            var files = Version.AsList(J.Get(first, "files"));
            if (files == null || files.Count == 0) { message = "这个版本没有文件"; return false; }
            object fileObj = files[0];
            string url = J.Str(fileObj, "url");
            string filename = J.Str(fileObj, "filename");
            string sha1 = J.Str(J.Dict(fileObj, "hashes"), "sha1");
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(filename)) { message = "文件信息不完整"; return false; }

            string dirName = kind == "resourcepack" ? LocalPacks.ResourcePacks : LocalPacks.ShaderPacks;
            string dir = LocalPacks.PackDir(baseDir, verName, dirName);
            Directory.CreateDirectory(dir);
            string dest = Path.Combine(dir, filename);
            if (File.Exists(dest)) { message = "已存在：" + filename; return true; }

            string info;
            string status = Http.Download(url, dest, string.IsNullOrEmpty(sha1) ? null : sha1, out info, true);
            if (status == "fail") { message = "下载失败：" + info; return false; }

            message = "已下载 " + filename;
            return true;
        }

        /// <summary>批量查项目名（用于依赖提示）。</summary>
        public static List<string> ProjectNames(List<string> ids)
        {
            var result = new List<string>();
            if (ids == null || ids.Count == 0) return result;
            try
            {
                string url = Api + "/projects?ids=" + Uri.EscapeDataString("[" + string.Join(",", ids.Select(i => "\"" + i + "\"")) + "]");
                var arr = Version.AsList(GetJson(url));
                if (arr != null)
                    foreach (object p in arr)
                    {
                        string t = J.Str(p, "title");
                        result.Add(string.IsNullOrEmpty(t) ? J.Str(p, "slug") : t);
                    }
            }
            catch { }
            if (result.Count == 0) result.AddRange(ids);
            return result;
        }

        private static object GetJson(string url)
        {
            var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
            req.UserAgent = UA;
            req.Accept = "application/json";
            req.Timeout = 30000;
            req.ReadWriteTimeout = 30000;
            using (var resp = (System.Net.HttpWebResponse)req.GetResponse())
            using (var stream = resp.GetResponseStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
                return J.Parse(reader.ReadToEnd());
        }

        /// <summary>命令行：mcroute.exe mods-search &lt;关键词&gt; [--mc 1.20.1] [--loader fabric]</summary>
        public static int CommandSearch(string query, string mc, string loader, int limit)
        {
            string err;
            var hits = Search(query, mc, loader, limit, out err);
            if (!string.IsNullOrEmpty(err)) { Console.WriteLine("[错误] " + err); return 1; }
            Console.WriteLine("搜索结果 " + hits.Count + " 条（Modrinth）" + (mc == null ? "" : " · " + mc) + (loader == null ? "" : " · " + loader));
            Console.WriteLine();
            int i = 0;
            foreach (var h in hits)
            {
                i++;
                Console.WriteLine(string.Format("  {0}. {1}  [{2}]  下载 {3}", i, h.Title, h.Slug, h.DownloadsText()));
                Console.WriteLine("     " + (h.Description ?? ""));
            }
            return 0;
        }
    }
}
