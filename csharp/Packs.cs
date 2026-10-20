using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Mcroute
{
    /// <summary>
    /// 整合包安装：Modrinth .mrpack / MultiMC·Prism 实例包 / CurseForge zip。
    /// 统一装成主流启动器兼容布局：versions/&lt;实例名&gt;/ + 合并后的完整版本 json。
    /// </summary>
    public static class Packs
    {
        public static int Install(string[] a)
        {
            string pack = Commands.Positional(a, 1);
            if (string.IsNullOrEmpty(pack)) { Console.WriteLine("[错误] 用法: install <整合包>"); return 1; }
            pack = Path.GetFullPath(pack);
            if (!File.Exists(pack)) { Console.WriteLine("[错误] 文件不存在: " + pack); return 1; }

            string baseDir = Version.BaseDir(Commands.Arg(a, "--dir"));
            bool noAssets = Commands.Has(a, "--no-assets");

            HashSet<string> names;
            try
            {
                using (var zip = ZipFile.OpenRead(pack))
                    names = new HashSet<string>(zip.Entries.Select(e => e.FullName.Replace('\\', '/')), StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[错误] 打不开压缩包: " + ex.Message);
                return 1;
            }

            if (names.Contains("modrinth.index.json")) return InstallMrpack(pack, baseDir, noAssets);
            if (names.Contains("mmc-pack.json")) return InstallMmc(pack, baseDir, noAssets);
            if (names.Contains("manifest.json")) return InstallCurseForge(pack, baseDir, noAssets);

            Console.WriteLine("[错误] 认不出的整合包格式（没有 modrinth.index.json / mmc-pack.json / manifest.json）");
            return 1;
        }

        // ================= 公共：写实例版本目录 =================
        private static string SafeName(string name)
        {
            string bad = "\\/:*?\"<>|";
            var sb = new StringBuilder();
            foreach (char c in (name ?? "").Trim())
                sb.Append(bad.IndexOf(c) >= 0 ? '_' : c);
            string safe = sb.ToString().Trim();
            return safe.Length == 0 ? "pack" : safe;
        }

        private static string InstanceDir(string baseDir, string name)
        {
            return Path.Combine(baseDir, "versions", SafeName(name));
        }

        /// <summary>把实例写成一个自包含的标准版本目录（合并 json + 客户端 jar）。</summary>
        private static string WriteInstanceVersion(string baseDir, string name, string parentVersion)
        {
            var vj = Version.Load(baseDir, parentVersion);
            if (vj == null)
            {
                Console.WriteLine("[警告] 读取父版本 " + parentVersion + " 失败，实例 json 没写");
                return null;
            }

            string dir = InstanceDir(baseDir, name);
            Directory.CreateDirectory(dir);
            string id = Path.GetFileName(dir);

            var merged = new Dictionary<string, object>(vj);
            merged["id"] = id;
            merged.Remove("inheritsFrom");

            // 客户端 jar：沿 inheritsFrom 链找第一个存在的复制过来
            string src = FindClientJar(baseDir, parentVersion);
            string dst = Path.Combine(dir, id + ".jar");
            if (src != null && !File.Exists(dst))
            {
                try { File.Copy(src, dst, true); }
                catch (Exception ex) { Console.WriteLine("[警告] 复制客户端 jar 失败: " + ex.Message); }
            }

            J.SaveFile(Path.Combine(dir, id + ".json"), merged);
            Console.WriteLine("[实例] 已写出标准版本目录: versions\\" + id);
            return id;
        }

        private static string FindClientJar(string baseDir, string ver)
        {
            string cur = ver;
            for (int i = 0; i < 6 && !string.IsNullOrEmpty(cur); i++)
            {
                string jar = Path.Combine(baseDir, "versions", cur, cur + ".jar");
                if (File.Exists(jar)) return jar;
                var vj = Version.Load(baseDir, cur);
                if (vj == null) return null;
                cur = J.Str(vj, "inheritsFrom");
            }
            return null;
        }

        /// <summary>加载器版本目录名（和 Python 版保持一致）。</summary>
        private static string LoaderVersionId(string kind, string mc, string loaderVer)
        {
            switch (kind)
            {
                case "forge": return mc + "-forge-" + loaderVer;
                case "neoforge": return "neoforge-" + loaderVer;
                case "fabric": return "fabric-loader-" + loaderVer + "-" + mc;
                case "quilt": return "quilt-loader-" + loaderVer + "-" + mc;
                default: return mc;
            }
        }

        private static void SaveInstanceMeta(string baseDir, string name, string id, string mc, string kind, string loaderVer, string source)
        {
            string dir = InstanceDir(baseDir, name);
            var meta = new Dictionary<string, object>
            {
                { "id", id },
                { "name", name },
                { "version_id", id },
                { "minecraft", mc },
                { "loader", kind },
                { "loader_version", loaderVer },
                { "game_dir", dir },
                { "source", source },
            };
            J.SaveFile(Path.Combine(dir, "instance.json"), meta);
        }

        /// <summary>确保原版 + 加载器都在本地。</summary>
        private static bool EnsureBase(string baseDir, string mc, string kind, string loaderVer, bool noAssets)
        {
            string json = Version.VersionJsonPath(baseDir, mc);
            if (!File.Exists(json))
            {
                Console.WriteLine("[1/3] 本地没有原版 " + mc + "，先下载 ...");
                var args = new List<string> { "fetch", mc, "--dir", baseDir };
                if (noAssets) args.Add("--no-assets");
                Commands.Fetch(args.ToArray());
                if (!File.Exists(json))
                {
                    Console.WriteLine("[错误] 原版下载失败，安装中止");
                    return false;
                }
            }
            else Console.WriteLine("[1/3] 原版 " + mc + " 已在本地");

            if (string.IsNullOrEmpty(kind)) return true;

            string id = LoaderVersionId(kind, mc, loaderVer);
            string loaderJson = Version.VersionJsonPath(baseDir, id);
            if (File.Exists(loaderJson))
            {
                Console.WriteLine("[2/3] 加载器 " + id + " 已在本地");
                return true;
            }

            Console.WriteLine("[2/3] 安装加载器 " + kind + " " + loaderVer + " ...");
            int code = Loader.Run(new[] { "loader", kind, loaderVer.Contains("-") ? loaderVer : mc + "-" + loaderVer, "--dir", baseDir });
            return File.Exists(Version.VersionJsonPath(baseDir, id));
        }

        private static void ExtractOverrides(ZipArchive zip, string prefix, string target)
        {
            int count = 0;
            foreach (var entry in zip.Entries)
            {
                string name = entry.FullName.Replace('\\', '/');
                if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (name.EndsWith("/")) continue;
                string rel = name.Substring(prefix.Length);
                string dest = Path.Combine(target, rel.Replace('/', '\\'));
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                entry.ExtractToFile(dest, true);
                count++;
            }
            Console.WriteLine("[4/4] 已铺开 " + prefix + "：" + count + " 个文件");
        }

        // ================= Modrinth .mrpack =================
        private static int InstallMrpack(string pack, string baseDir, bool noAssets)
        {
            Dictionary<string, object> index;
            using (var zip = ZipFile.OpenRead(pack))
            {
                var e = zip.GetEntry("modrinth.index.json");
                using (var sr = new StreamReader(e.Open(), Encoding.UTF8))
                    index = J.Obj(J.Parse(sr.ReadToEnd()));
            }

            string name = J.Str(index, "name", Path.GetFileNameWithoutExtension(pack));
            var deps = J.Dict(index, "dependencies") ?? new Dictionary<string, object>();
            string mc = J.Str(deps, "minecraft");
            if (string.IsNullOrEmpty(mc)) { Console.WriteLine("[错误] 索引里没有 minecraft 依赖"); return 1; }

            string kind = null, loaderVer = null;
            if (deps.ContainsKey("forge")) { kind = "forge"; loaderVer = J.Str(deps, "forge"); }
            else if (deps.ContainsKey("neoforge")) { kind = "neoforge"; loaderVer = J.Str(deps, "neoforge"); }
            else if (deps.ContainsKey("fabric-loader")) { kind = "fabric"; loaderVer = J.Str(deps, "fabric-loader"); }
            else if (deps.ContainsKey("quilt-loader")) { kind = "quilt"; loaderVer = J.Str(deps, "quilt-loader"); }

            Console.WriteLine("整合包  : " + name);
            Console.WriteLine("MC 版本 : " + mc);
            Console.WriteLine("加载器  : " + (kind == null ? "无（原版）" : kind + " " + loaderVer));

            if (!EnsureBase(baseDir, mc, kind, loaderVer, noAssets)) return 1;

            string versionId = kind == null ? mc : LoaderVersionId(kind, mc, loaderVer);
            string instDir = InstanceDir(baseDir, name);
            Directory.CreateDirectory(instDir);

            // 下载清单里的文件
            var jobs = new List<Tuple<string, string, string>>();
            int skipped = 0;
            foreach (var f in Version.AsList(J.Get(index, "files")))
            {
                var env = J.Dict(f, "env");
                if (env != null && J.Str(env, "client") == "unsupported") { skipped++; continue; }
                string path = J.Str(f, "path");
                var downloads = Version.AsList(J.Get(f, "downloads"));
                if (string.IsNullOrEmpty(path) || downloads.Count == 0) { skipped++; continue; }
                var hashes = J.Dict(f, "hashes");
                string sha1 = hashes == null ? null : J.Str(hashes, "sha1");
                jobs.Add(Tuple.Create(Convert.ToString(downloads[0]), Path.Combine(instDir, path.Replace('/', '\\')), sha1));
            }
            Console.WriteLine("[3/4] 下载 " + jobs.Count + " 个文件（跳过 " + skipped + " 个）");
            Http.DownloadAll(jobs, 16);

            using (var zip = ZipFile.OpenRead(pack))
                ExtractOverrides(zip, "overrides/", instDir);

            string id = WriteInstanceVersion(baseDir, name, versionId) ?? versionId;
            SaveInstanceMeta(baseDir, name, id, mc, kind, loaderVer, pack);

            Console.WriteLine();
            Console.WriteLine("完成。启动命令：");
            Console.WriteLine("  mcroute launch " + id);
            return 0;
        }

        // ================= MultiMC / Prism =================
        private static int InstallMmc(string pack, string baseDir, bool noAssets)
        {
            Dictionary<string, object> meta;
            string cfgName = null;
            using (var zip = ZipFile.OpenRead(pack))
            {
                var e = zip.GetEntry("mmc-pack.json");
                using (var sr = new StreamReader(e.Open(), Encoding.UTF8))
                    meta = J.Obj(J.Parse(sr.ReadToEnd()));

                var cfg = zip.GetEntry("instance.cfg");
                if (cfg != null)
                {
                    using (var sr = new StreamReader(cfg.Open(), Encoding.UTF8))
                    {
                        string line;
                        while ((line = sr.ReadLine()) != null)
                        {
                            if (line.StartsWith("name=", StringComparison.OrdinalIgnoreCase))
                            {
                                cfgName = line.Substring(5).Trim();
                                break;
                            }
                        }
                    }
                }
            }

            string mc = null, kind = null, loaderVer = null;
            var kindOf = new Dictionary<string, string>
            {
                { "net.minecraftforge", "forge" },
                { "net.neoforged", "neoforge" },
                { "net.fabricmc.fabric-loader", "fabric" },
                { "org.quiltmc.quilt-loader", "quilt" },
            };
            foreach (var comp in Version.AsList(J.Get(meta, "components")))
            {
                string uid = J.Str(comp, "uid");
                string ver = J.Str(comp, "version");
                if (uid == "net.minecraft") mc = ver;
                else if (kindOf.ContainsKey(uid)) { kind = kindOf[uid]; loaderVer = ver; }
            }
            if (string.IsNullOrEmpty(mc)) { Console.WriteLine("[错误] mmc-pack.json 里没有 net.minecraft 组件"); return 1; }

            string name = string.IsNullOrEmpty(cfgName) ? Path.GetFileNameWithoutExtension(pack) : cfgName;
            Console.WriteLine("整合包  : " + name);
            Console.WriteLine("MC 版本 : " + mc);
            Console.WriteLine("加载器  : " + (kind == null ? "无（原版）" : kind + " " + loaderVer));

            if (!EnsureBase(baseDir, mc, kind, loaderVer, noAssets)) return 1;

            string versionId = kind == null ? mc : LoaderVersionId(kind, mc, loaderVer);
            string instDir = InstanceDir(baseDir, name);
            Directory.CreateDirectory(instDir);

            Console.WriteLine("[3/3] 解压包内 .minecraft/ ...");
            int count = 0;
            using (var zip = ZipFile.OpenRead(pack))
            {
                foreach (var entry in zip.Entries)
                {
                    string n = entry.FullName.Replace('\\', '/');
                    if (!n.StartsWith(".minecraft/", StringComparison.OrdinalIgnoreCase)) continue;
                    if (n.EndsWith("/")) continue;
                    string rel = n.Substring(".minecraft/".Length);
                    string dest = Path.Combine(instDir, rel.Replace('/', '\\'));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    entry.ExtractToFile(dest, true);
                    count++;
                }
            }
            Console.WriteLine("      已铺开 " + count + " 个文件");

            string id = WriteInstanceVersion(baseDir, name, versionId) ?? versionId;
            SaveInstanceMeta(baseDir, name, id, mc, kind, loaderVer, pack);

            Console.WriteLine();
            Console.WriteLine("完成。启动命令：");
            Console.WriteLine("  mcroute launch " + id);
            return 0;
        }

        // ================= CurseForge zip =================
        private static string CurseForgeKey(string baseDir)
        {
            string env = Environment.GetEnvironmentVariable("CF_API_KEY");
            if (!string.IsNullOrEmpty(env)) return env.Trim();
            string file = Path.Combine(baseDir, "cf_api_key.txt");
            if (File.Exists(file)) return File.ReadAllText(file).Trim();
            return null;
        }

        private static object CfGet(string path, string key, int retries = 4)
        {
            string url = "https://api.curseforge.com/v1" + path;
            for (int i = 0; i < retries; i++)
            {
                try
                {
                    var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
                    req.Headers.Add("x-api-key", key);
                    req.UserAgent = "curl/8.21.0";
                    req.Accept = "application/json";
                    req.Timeout = 30000;
                    using (var resp = req.GetResponse())
                    using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                        return J.Parse(sr.ReadToEnd());
                }
                catch
                {
                    System.Threading.Thread.Sleep(1500 * (i + 1));
                }
            }
            return null;
        }

        private static int InstallCurseForge(string pack, string baseDir, bool noAssets)
        {
            string key = CurseForgeKey(baseDir);
            if (string.IsNullOrEmpty(key))
            {
                Console.WriteLine("[错误] 没有 CurseForge API key");
                Console.WriteLine("       方式一：环境变量 CF_API_KEY");
                Console.WriteLine("       方式二：写进 " + Path.Combine(baseDir, "cf_api_key.txt"));
                return 1;
            }

            Dictionary<string, object> manifest;
            using (var zip = ZipFile.OpenRead(pack))
            {
                var e = zip.GetEntry("manifest.json");
                using (var sr = new StreamReader(e.Open(), Encoding.UTF8))
                    manifest = J.Obj(J.Parse(sr.ReadToEnd()));
            }

            string name = J.Str(manifest, "name", Path.GetFileNameWithoutExtension(pack));
            var mcMeta = J.Dict(manifest, "minecraft");
            string mc = J.Str(mcMeta, "version");
            if (string.IsNullOrEmpty(mc)) { Console.WriteLine("[错误] manifest 里没有 minecraft.version"); return 1; }

            string loaderId = "";
            foreach (var l in Version.AsList(J.Get(mcMeta, "modLoaders")))
            {
                if (J.Bool(l, "primary")) { loaderId = J.Str(l, "id"); break; }
            }
            if (string.IsNullOrEmpty(loaderId))
            {
                var first = Version.AsList(J.Get(mcMeta, "modLoaders")).FirstOrDefault();
                if (first != null) loaderId = J.Str(first, "id");
            }

            string kind = null, loaderVer = null;
            if (loaderId.StartsWith("forge-")) { kind = "forge"; loaderVer = loaderId.Substring(6); }
            else if (loaderId.StartsWith("neoforge-")) { kind = "neoforge"; loaderVer = loaderId.Substring(9); }
            else if (loaderId.StartsWith("fabric-")) { kind = "fabric"; loaderVer = loaderId.Substring(7); }
            else if (loaderId.StartsWith("quilt-")) { kind = "quilt"; loaderVer = loaderId.Substring(6); }

            Console.WriteLine("整合包  : " + name + " " + J.Str(manifest, "version"));
            Console.WriteLine("MC 版本 : " + mc);
            Console.WriteLine("加载器  : " + (kind == null ? "无（原版）" : kind + " " + loaderVer));

            if (!EnsureBase(baseDir, mc, kind, loaderVer, noAssets)) return 1;

            string versionId = kind == null ? mc : LoaderVersionId(kind, mc, loaderVer);
            string instDir = InstanceDir(baseDir, name);
            string modsDir = Path.Combine(instDir, "mods");
            Directory.CreateDirectory(modsDir);

            // 用 API 换下载地址
            var files = Version.AsList(J.Get(manifest, "files"));
            Console.WriteLine("[3/4] 解析 " + files.Count + " 个文件（CurseForge API）...");
            var resolved = new List<Tuple<string, string>>();   // url, fileName
            var unresolved = new List<string>();

            System.Threading.Tasks.Parallel.ForEach(files, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 8 }, f =>
            {
                int pid = J.Int(f, "projectID");
                int fid = J.Int(f, "fileID");
                if (pid == 0 || fid == 0) return;
                var payload = CfGet("/mods/" + pid + "/files/" + fid, key);
                var data = payload == null ? null : J.Dict(payload, "data");
                if (data == null)
                {
                    lock (unresolved) unresolved.Add("projectID=" + pid + " fileID=" + fid);
                    return;
                }
                string fileName = J.Str(data, "fileName");
                string url = J.Str(data, "downloadUrl");
                if (string.IsNullOrEmpty(fileName))
                {
                    lock (unresolved) unresolved.Add("无 fileName: " + fid);
                    return;
                }
                if (string.IsNullOrEmpty(url))
                    url = "https://edge.forgecdn.net/files/" + (fid / 1000) + "/" + (fid % 1000) + "/" + Uri.EscapeDataString(fileName);
                string sha1 = null;
                foreach (var h in Version.AsList(J.Get(data, "hashes")))
                    if (J.Int(h, "algo") == 1) sha1 = J.Str(h, "value");
                lock (resolved) resolved.Add(Tuple.Create(url, fileName));
            });

            if (unresolved.Count > 0)
                Console.WriteLine("      有 " + unresolved.Count + " 个没解析出来");

            Console.WriteLine("      开始下载 " + resolved.Count + " 个 mod ...");
            var jobs = resolved.Select(r => Tuple.Create(r.Item1, Path.Combine(modsDir, r.Item2), (string)null)).ToList();
            Http.DownloadAll(jobs, 16);

            string overrides = J.Str(manifest, "overrides", "overrides").TrimEnd('/') + "/";
            using (var zip = ZipFile.OpenRead(pack))
                ExtractOverrides(zip, overrides, instDir);

            string id = WriteInstanceVersion(baseDir, name, versionId) ?? versionId;
            SaveInstanceMeta(baseDir, name, id, mc, kind, loaderVer, pack);

            Console.WriteLine();
            Console.WriteLine("完成。启动命令：");
            Console.WriteLine("  mcroute launch " + id);
            return 0;
        }
    }
}
