using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;

namespace Mcroute
{
    /// <summary>
    /// 加载器安装：fabric / quilt / legacyfabric 走 meta；
    /// forge / neoforge 走官方安装器（老版本走 legacy：直接写版本 json，不跑安装器）。
    /// </summary>
    public static class Loader
    {
        private static readonly Dictionary<string, string> MetaBase = new Dictionary<string, string>
        {
            { "fabric", "https://meta.fabricmc.net/v2" },
            { "quilt", "https://meta.quiltmc.org/v3" },
            { "legacyfabric", "https://meta.legacyfabric.net/v2" },
        };

        public static int Run(string[] a)
        {
            if (a.Length < 3)
            {
                Console.WriteLine("[错误] 用法: loader <fabric|quilt|legacyfabric|forge|neoforge> <版本>");
                return 1;
            }

            string kind = a[1].ToLowerInvariant();
            string ver = a[2];
            string baseDir = Version.BaseDir(Commands.Arg(a, "--dir"));

            // 先确保对应的原版在本地（安装器要打补丁，启动也要它的 jar）
            EnsureVanilla(kind, ver, baseDir);

            if (MetaBase.ContainsKey(kind)) return InstallMeta(kind, ver, baseDir);
            if (kind == "forge" || kind == "neoforge") return InstallForge(kind, ver, baseDir);
            Console.WriteLine("[错误] 不认识的加载器: " + kind);
            return 1;
        }

        /// <summary>从加载器版本号反推 MC 版本，缺原版就先下。</summary>
        private static void EnsureVanilla(string kind, string ver, string baseDir)
        {
            string mc = null;
            if (kind == "neoforge")
            {
                string[] parts = ver.Split('.');
                if (parts.Length >= 2)
                {
                    mc = parts[1] == "0" ? "1." + parts[0] : "1." + parts[0] + "." + parts[1];
                    if (parts[0] == "47") mc = "1.20.1";   // NeoForge 的 1.20.1 特例
                }
            }
            else
            {
                int dash = ver.IndexOf('-');
                mc = dash > 0 ? ver.Substring(0, dash) : ver;
            }
            if (string.IsNullOrEmpty(mc)) return;

            string jsonPath = Version.VersionJsonPath(baseDir, mc);
            if (File.Exists(jsonPath))
            {
                Console.WriteLine("[准备] 原版 " + mc + " 已在本地");
                return;
            }
            Console.WriteLine("[准备] 本地没有原版 " + mc + "，先下载 ...");
            Commands.Fetch(new[] { "fetch", mc, "--dir", baseDir });
        }

        // ================= Fabric 系 =================
        private static int InstallMeta(string kind, string ver, string baseDir)
        {
            string metaBase = MetaBase[kind];
            string mc, loader;
            int dash = ver.IndexOf('-');
            if (dash > 0)
            {
                mc = ver.Substring(0, dash);
                loader = ver.Substring(dash + 1);
            }
            else
            {
                mc = ver;
                loader = null;
            }

            if (loader == null)
            {
                object list = Http.GetJson(metaBase + "/versions/loader/" + mc);
                if (list == null)
                {
                    Console.WriteLine("[错误] 拉不到 " + kind + " 的加载器列表（" + mc + "）");
                    return 1;
                }
                var items = Version.AsList(list);
                foreach (var item in items)
                {
                    var info = J.Dict(item, "loader");
                    if (info != null && J.Bool(info, "stable"))
                    {
                        loader = J.Str(info, "version");
                        break;
                    }
                }
                if (loader == null && items.Count > 0)
                    loader = J.Str(J.Dict(items[0], "loader"), "version");
                Console.WriteLine("[提示] 自动选择最新 stable " + kind + " " + loader);
            }

            string url = metaBase + "/versions/loader/" + mc + "/" + loader + "/profile/json";
            object profile = Http.GetJson(url);
            if (profile == null)
            {
                Console.WriteLine("[错误] 拉取 profile 失败: " + url);
                return 1;
            }

            string id = J.Str(profile, "id", kind + "-loader-" + loader + "-" + mc);
            string path = Path.Combine(baseDir, "versions", id, id + ".json");
            J.SaveFile(path, profile);

            Console.WriteLine();
            Console.WriteLine("加载器  : " + kind + " " + loader);
            Console.WriteLine("MC 版本 : " + mc);
            Console.WriteLine("mainClass: " + J.Str(profile, "mainClass"));
            Console.WriteLine("已写入  : " + path);
            Console.WriteLine();
            Console.WriteLine("下一步：");
            Console.WriteLine("  mcroute fetch  " + id);
            Console.WriteLine("  mcroute launch " + id);
            return 0;
        }

        // ================= Forge / NeoForge =================
        private static int InstallForge(string kind, string ver, string baseDir)
        {
            string version = ResolveForgeVersion(kind, ver);
            if (version == null) return 1;
            if (version != ver) Console.WriteLine("[提示] 版本自动补全: " + ver + " -> " + version);

            string url = kind == "forge"
                ? "https://maven.minecraftforge.net/net/minecraftforge/forge/" + version + "/forge-" + version + "-installer.jar"
                : "https://neoforged.forgecdn.net/releases/net/neoforged/neoforge/" + version + "/neoforge-" + version + "-installer.jar";

            Console.WriteLine("加载器  : " + kind + " " + version);
            Console.WriteLine("[1/3] 下载 installer ...");
            Console.WriteLine("      " + url);

            string tmpDir = Path.Combine(baseDir, "_tmp");
            Directory.CreateDirectory(tmpDir);
            // 文件名必须带版本号！否则装了 1.7.10 之后再装 1.19.2，
            // 会因为"文件已存在"直接复用旧安装器 -> 走错分支
            string installer = Path.Combine(tmpDir, kind + "-" + version + "-installer.jar");
            string info;
            string status = Http.Download(url, installer, null, out info);
            if (status == "fail")
            {
                Console.WriteLine("      失败: " + info);
                return 1;
            }
            Console.WriteLine("      OK，" + (new FileInfo(installer).Length / 1024 / 1024) + " MB");

            // 读 install_profile.json
            Dictionary<string, object> profile;
            try
            {
                using (var zip = ZipFile.OpenRead(installer))
                {
                    var entry = zip.GetEntry("install_profile.json");
                    if (entry == null)
                    {
                        Console.WriteLine("[错误] installer 里没有 install_profile.json");
                        return 1;
                    }
                    using (var sr = new StreamReader(entry.Open()))
                        profile = J.Obj(J.Parse(sr.ReadToEnd()));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[错误] 读取 install_profile 失败: " + ex.Message);
                return 1;
            }

            bool legacy = profile.ContainsKey("versionInfo");
            bool hasProcessors = Version.AsList(J.Get(profile, "processors")).Count > 0;

            if (legacy && !hasProcessors)
                return InstallLegacyForge(profile, baseDir);

            // 现代安装器：先把它需要的库预下载好，再跑它
            Console.WriteLine("[2/3] 预下载安装器依赖 ...");
            var jobs = new List<Tuple<string, string, string>>();
            string libRoot = Path.Combine(baseDir, "libraries");
            int queued = 0;

            void QueueLibs(object libsObj)
            {
                foreach (var lib in Version.AsList(libsObj))
                {
                    var resolved = Meta.ResolveLibrary(lib);
                    if (resolved != null && !string.IsNullOrEmpty(resolved.RelPath))
                    {
                        string dest = Path.Combine(libRoot, resolved.RelPath.Replace('/', '\\'));
                        if (!File.Exists(dest))
                        {
                            jobs.Add(Tuple.Create(resolved.Url, dest, resolved.Sha1));
                            queued++;
                        }
                    }
                }
            }

            QueueLibs(J.Get(profile, "libraries"));
            object versionJson = null;
            try
            {
                using (var zip = ZipFile.OpenRead(installer))
                {
                    var e = zip.GetEntry("version.json");
                    if (e != null)
                        using (var sr = new StreamReader(e.Open()))
                            versionJson = J.Parse(sr.ReadToEnd());
                }
            }
            catch { }
            if (versionJson != null) QueueLibs(J.Get(versionJson, "libraries"));
            Console.WriteLine("      需要预下载 " + queued + " 个文件");
            Http.DownloadAll(jobs, 16);

            // launcher_profiles.json（安装器拿它判断目录是否合法）
            string profiles = Path.Combine(baseDir, "launcher_profiles.json");
            if (!File.Exists(profiles))
                File.WriteAllText(profiles, "{\"profiles\":{},\"settings\":{},\"version\":3}");

            Console.WriteLine("[3/3] 运行 installer（可能要几分钟）...");
            var java = Java.Find(baseDir, kind == "neoforge" ? 21 : 17, null, out _);
            // 有些受限环境里 Java 建不了 %TEMP% 下的临时目录（AccessDeniedException），
            // 直接把 java.io.tmpdir 指到数据目录里自己管
            string javaTmp = Path.Combine(tmpDir, "javatmp");
            Directory.CreateDirectory(javaTmp);

            var psi = new ProcessStartInfo(java,
                "-Djava.io.tmpdir=\"" + javaTmp + "\" -jar \"" + installer +
                "\" --installClient \"" + baseDir + "\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.WorkingDirectory = baseDir;   // 安装器对工作目录敏感
            using (var p = Process.Start(psi))
            {
                string stdout = p.StandardOutput.ReadToEnd();
                string stderr = p.StandardError.ReadToEnd();
                p.WaitForExit();

                // 完整日志落盘，方便排查
                string logFile = Path.Combine(tmpDir, "installer-" + version + ".log");
                try { File.WriteAllText(logFile, stdout + "\r\n=== STDERR ===\r\n" + stderr); } catch { }

                if (p.ExitCode != 0)
                {
                    Console.WriteLine("      installer 退出码 " + p.ExitCode + "（完整日志 " + logFile + "）");
                    var tail = (stdout + "\n" + stderr).Split('\n')
                        .Where(x => x.Trim().Length > 0).Reverse().Take(20).Reverse();
                    foreach (string line in tail) Console.WriteLine("      | " + line.Trim());
                    return 1;
                }
                Console.WriteLine("      installer 输出已存: " + logFile);
            }
            Console.WriteLine("      installer 完成");
            Console.WriteLine();
            Console.WriteLine("到 mcdata\\versions 下看看新出现的版本目录，然后 fetch + launch。");
            return 0;
        }

        /// <summary>老版本 Forge（1.7.10 及更早）：不跑安装器，直接写版本 json。</summary>
        private static int InstallLegacyForge(Dictionary<string, object> profile, string baseDir)
        {
            var vi = J.Dict(profile, "versionInfo");
            if (vi == null)
            {
                Console.WriteLine("[错误] 没有 versionInfo");
                return 1;
            }

            string id = J.Str(vi, "id");
            if (string.IsNullOrEmpty(id))
            {
                Console.WriteLine("[错误] versionInfo 里没有 id");
                return 1;
            }

            int fixedCount = 0;
            var libs = J.Get(vi, "libraries") as object[];
            if (libs != null)
            {
                var newList = new List<object>();
                foreach (var libObj in libs)
                {
                    var lib = J.Obj(libObj);
                    if (lib == null) { newList.Add(libObj); continue; }

                    string name = J.Str(lib, "name");
                    var parts = name.Split(':');
                    if (parts.Length == 3 && parts[0] == "net.minecraftforge" && parts[1] == "forge")
                    {
                        lib["name"] = name + ":universal";
                        if (string.IsNullOrEmpty(J.Str(lib, "url")))
                            lib["url"] = "https://maven.minecraftforge.net/";
                        fixedCount++;
                    }
                    if (string.IsNullOrEmpty(J.Str(lib, "url")))
                    {
                        lib["url"] = "https://libraries.minecraft.net/";
                        fixedCount++;
                    }
                    newList.Add(lib);
                }
                vi["libraries"] = newList.ToArray();
            }

            string path = Path.Combine(baseDir, "versions", id, id + ".json");
            J.SaveFile(path, vi);

            Console.WriteLine("[legacy] 已写出 " + id + "（修正 " + fixedCount + " 条库）");
            Console.WriteLine("         版本 json: " + path);
            Console.WriteLine();
            Console.WriteLine("下一步：");
            Console.WriteLine("  mcroute fetch  " + id);
            Console.WriteLine("  mcroute launch " + id);
            return 0;
        }

        /// <summary>把 "1.7.10" 这种只写 MC 版本的情况补全成完整 Forge 版本号。</summary>
        private static string ResolveForgeVersion(string kind, string ver)
        {
            if (kind == "neoforge") return ver;   // NeoForge 版本号本来就完整

            if (ver.IndexOf('-') > 0) return ver; // 已经写成 <mc>-<forge>

            string text = Http.GetString("https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml", 60);
            if (text == null)
            {
                Console.WriteLine("[错误] 取不到 Forge 版本列表（maven 不通）");
                return null;
            }
            var matches = Regex.Matches(text, "<version>([^<]+)</version>")
                .Cast<Match>()
                .Select(m => m.Groups[1].Value)
                .Where(v => v.StartsWith(ver + "-", StringComparison.Ordinal))
                .ToList();
            if (matches.Count == 0)
            {
                Console.WriteLine("[错误] maven 里没有 " + ver + " 的 Forge");
                return null;
            }
            return matches.OrderBy(v => NumericKey(v)).Last();
        }

        private static string NumericKey(string v)
        {
            // 只用于排序：把所有数字段补零对齐
            return Regex.Replace(v, "\\d+", m => m.Value.PadLeft(8, '0'));
        }
    }
}
