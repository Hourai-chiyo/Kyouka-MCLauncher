using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Mcroute
{
    public class Plan
    {
        public string Base, Version, MainClass, Java, GameDir, NativesDir, AssetsDir, AssetIndex;
        public int JavaMajor;
        public Dictionary<string, object> Json;
        public List<string> Classpath = new List<string>();
        public List<string> NativesJars = new List<string>();
        public List<string> JvmArgs = new List<string>();
        public List<string> GameArgs = new List<string>();
        public List<string> Missing = new List<string>();
        public int SkippedLibs;
    }

    public static class Commands
    {
        // ================= manifest =================
        public static int Manifest(string[] a)
        {
            string baseDir = Version.BaseDir(Arg(a, "--dir"));
            string ver = Positional(a, 1);
            object manifest = Http.GetJson("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json");
            if (manifest == null)
            {
                Console.WriteLine("[错误] 取版本清单失败（Mojang piston-meta 不通）");
                return 1;
            }
            var versions = Version.AsList(J.Get(manifest, "versions"));
            Console.WriteLine("清单来自 Mojang，共 {0} 个版本", versions.Count);
            if (string.IsNullOrEmpty(ver))
            {
                int shown = 0;
                foreach (var v in versions)
                {
                    if (shown++ >= 10) break;
                    Console.WriteLine("  {0,-20} {1}", J.Str(v, "id"), J.Str(v, "type"));
                }
                return 0;
            }
            foreach (var v in versions)
            {
                if (J.Str(v, "id") == ver)
                {
                    Console.WriteLine("{0} -> {1}", ver, J.Str(v, "url"));
                    return 0;
                }
            }
            Console.WriteLine("[错误] 清单里没有版本 " + ver);
            return 1;
        }

        // ================= fetch =================
        public static int Fetch(string[] a)
        {
            string ver = Positional(a, 1);
            if (string.IsNullOrEmpty(ver)) { Console.WriteLine("[错误] 用法: fetch <版本>"); return 1; }
            string baseDir = Version.BaseDir(Arg(a, "--dir"));
            bool noAssets = Has(a, "--no-assets");
            int workers = 16;

            Console.WriteLine("版本：" + ver);
            Console.WriteLine("目录：" + baseDir);

            Dictionary<string, object> vj = EnsureVersionJson(baseDir, ver);
            if (vj == null) return 1;

            var jobs = new List<Tuple<string, string, string>>();
            int clientMissing = 0;

            // ---- client jar ----
            string vdir = Path.Combine(baseDir, "versions", ver);
            string clientJar = Path.Combine(vdir, ver + ".jar");
            var downloads = J.Dict(vj, "downloads");
            var client = downloads == null ? null : J.Dict(downloads, "client");
            if (client != null && !string.IsNullOrEmpty(J.Str(client, "url")))
            {
                jobs.Add(Tuple.Create(J.Str(client, "url"), clientJar, J.Str(client, "sha1")));
            }
            else if (!File.Exists(clientJar))
            {
                // 加载器版本没有自己的 client jar：从父版本复制（Forge 需要 ${version_name}.jar）
                string parent = J.Str(vj, "inheritsFrom");
                if (!string.IsNullOrEmpty(parent))
                {
                    string parentJar = Path.Combine(baseDir, "versions", parent, parent + ".jar");
                    if (File.Exists(parentJar))
                    {
                        Directory.CreateDirectory(vdir);
                        File.Copy(parentJar, clientJar, true);
                        Console.WriteLine("[准备] 已把 {0}.jar 复制为 {1}.jar", parent, ver);
                    }
                    else clientMissing++;
                }
                else clientMissing++;
            }

            // ---- libraries ----
            string libRoot = Path.Combine(baseDir, "libraries");
            int libCount = 0, nativesCount = 0;
            foreach (var lib in Version.AsList(J.Get(vj, "libraries")))
            {
                if (!Version.RulesAllow(lib)) continue;

                var resolved = Meta.ResolveLibrary(lib);
                if (resolved != null && !string.IsNullOrEmpty(resolved.RelPath))
                {
                    libCount++;
                    jobs.Add(Tuple.Create(resolved.Url, Path.Combine(libRoot, resolved.RelPath.Replace('/', '\\')), resolved.Sha1));
                }

                // 老版本 natives：下载 windows 分类器
                var natives = J.Dict(lib, "natives");
                if (natives != null)
                {
                    string classifier = J.Str(natives, "windows");
                    if (!string.IsNullOrEmpty(classifier))
                    {
                        classifier = classifier.Replace("${arch}", "64");
                        var cls = J.Dict(J.Dict(lib, "downloads"), "classifiers");
                        var native = cls == null ? null : J.Dict(cls, classifier);
                        if (native != null && !string.IsNullOrEmpty(J.Str(native, "url")))
                        {
                            nativesCount++;
                            string rel = J.Str(native, "path");
                            if (string.IsNullOrEmpty(rel)) rel = Meta.MavenPathFromName(J.Str(lib, "name"), classifier);
                            jobs.Add(Tuple.Create(J.Str(native, "url"),
                                Path.Combine(libRoot, rel.Replace('/', '\\')), J.Str(native, "sha1")));
                        }
                    }
                }
            }

            // ---- assets ----
            var assetIndex = J.Dict(vj, "assetIndex");
            string assetIndexId = assetIndex == null ? "" : J.Str(assetIndex, "id");
            int assetObjects = 0;
            if (assetIndex != null && !noAssets)
            {
                string indexPath = Path.Combine(baseDir, "assets", "indexes", assetIndexId + ".json");
                object index = null;
                if (File.Exists(indexPath))
                {
                    try { index = J.ParseFile(indexPath); } catch { index = null; }
                }
                if (index == null)
                {
                    index = Http.GetJson(J.Str(assetIndex, "url"));
                    if (index != null) J.SaveFile(indexPath, index);
                }
                if (index != null)
                {
                    var objects = J.Dict(index, "objects");
                    if (objects != null)
                    {
                        foreach (var kv in objects)
                        {
                            string hash = J.Str(kv.Value, "hash");
                            if (hash.Length < 2) continue;
                            string dest = Path.Combine(baseDir, "assets", "objects", hash.Substring(0, 2), hash);
                            if (File.Exists(dest)) continue;
                            assetObjects++;
                            jobs.Add(Tuple.Create("https://resources.download.minecraft.net/" + hash.Substring(0, 2) + "/" + hash, dest, hash));
                        }
                    }
                }
            }
            else if (noAssets)
            {
                Console.WriteLine("--no-assets：跳过资源对象下载（缺音效贴图）");
            }

            Console.WriteLine("client jar: {0}  库: {1}  natives: {2}  资源对象缺: {3}",
                client != null ? "有" : "无", libCount, nativesCount, assetObjects);
            if (clientMissing > 0) Console.WriteLine("[警告] 缺少客户端 jar，且找不到父版本可复制");

            Http.DownloadAll(jobs, workers);
            return 0;
        }

        /// <summary>本地有就用本地的；没有就从官方 manifest 下载并保存。</summary>
        public static Dictionary<string, object> EnsureVersionJson(string baseDir, string ver)
        {
            string path = Version.VersionJsonPath(baseDir, ver);
            if (File.Exists(path))
            {
                var local = Version.Load(baseDir, ver);
                if (local != null) return local;
            }

            object manifest = Http.GetJson("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json");
            if (manifest == null) { Console.WriteLine("[错误] 取不到版本清单"); return null; }

            foreach (var v in Version.AsList(J.Get(manifest, "versions")))
            {
                if (J.Str(v, "id") != ver) continue;
                object vj = Http.GetJson(J.Str(v, "url"));
                if (vj == null) { Console.WriteLine("[错误] 下载版本 json 失败"); return null; }
                J.SaveFile(path, vj);
                Console.WriteLine("版本 json 已保存");
                return J.Obj(vj);
            }
            Console.WriteLine("[错误] 清单里没有版本 " + ver);
            return null;
        }

        // ================= plan =================
        public static Plan PlanLaunch(string baseDir, string ver, string javaArg, string gameDirArg)
        {
            var vj = Version.Load(baseDir, ver);
            if (vj == null)
            {
                Console.WriteLine("[错误] 找不到版本 " + ver + "（先 fetch）");
                return null;
            }

            var plan = new Plan
            {
                Base = baseDir,
                Version = ver,
                Json = vj,
                AssetsDir = Path.Combine(baseDir, "assets"),
                NativesDir = Path.Combine(baseDir, "natives", ver),
                GameDir = !string.IsNullOrEmpty(gameDirArg)
                    ? Path.GetFullPath(gameDirArg)
                    : (File.Exists(Path.Combine(baseDir, "versions", ver, "instance.json"))
                        ? Path.Combine(baseDir, "versions", ver)
                        : Path.Combine(baseDir, "instances", ver)),
            };

            string libRoot = Path.Combine(baseDir, "libraries");

            foreach (var lib in Version.AsList(J.Get(vj, "libraries")))
            {
                if (!Version.RulesAllow(lib)) { plan.SkippedLibs++; continue; }

                var natives = J.Dict(lib, "natives");
                if (natives != null)
                {
                    string classifier = J.Str(natives, "windows");
                    if (!string.IsNullOrEmpty(classifier))
                    {
                        classifier = classifier.Replace("${arch}", "64");
                        var cls = J.Dict(J.Dict(lib, "downloads"), "classifiers");
                        var native = cls == null ? null : J.Dict(cls, classifier);
                        if (native != null)
                        {
                            string rel = J.Str(native, "path");
                            if (string.IsNullOrEmpty(rel)) rel = Meta.MavenPathFromName(J.Str(lib, "name"), classifier);
                            if (!string.IsNullOrEmpty(rel))
                            {
                                string full = Path.Combine(libRoot, rel.Replace('/', '\\'));
                                plan.NativesJars.Add(full);
                                if (!File.Exists(full)) plan.Missing.Add("[natives] " + full);
                            }
                        }
                    }
                    continue;   // natives 库不进 classpath
                }

                var resolved = Meta.ResolveLibrary(lib);
                if (resolved == null || string.IsNullOrEmpty(resolved.RelPath)) continue;
                string dest = Path.Combine(libRoot, resolved.RelPath.Replace('/', '\\'));
                plan.Classpath.Add(dest);
                if (!File.Exists(dest)) plan.Missing.Add("[库] " + dest);
            }

            // 客户端 jar（本版本 -> 父版本）
            string clientJar = Path.Combine(baseDir, "versions", ver, ver + ".jar");
            if (!File.Exists(clientJar))
            {
                string parent = J.Str(vj, "inheritsFrom");
                if (!string.IsNullOrEmpty(parent))
                {
                    string parentJar = Path.Combine(baseDir, "versions", parent, parent + ".jar");
                    if (File.Exists(parentJar))
                    {
                        try
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(clientJar));
                            File.Copy(parentJar, clientJar, true);
                        }
                        catch { clientJar = parentJar; }
                    }
                    else clientJar = parentJar;
                }
            }
            if (File.Exists(clientJar)) plan.Classpath.Add(clientJar);
            else plan.Missing.Add("[客户端 jar] " + clientJar);

            plan.MainClass = J.Str(vj, "mainClass");
            var ai = J.Dict(vj, "assetIndex");
            plan.AssetIndex = ai != null ? J.Str(ai, "id") : J.Str(vj, "assets", "legacy");

            // Java
            int required = J.Int(J.Dict(vj, "javaVersion") ?? new Dictionary<string, object>(), "majorVersion", 8);
            plan.Java = Java.Find(baseDir, required, javaArg, out plan.JavaMajor);

            // 变量上下文
            string sep = ";";
            var ctx = new Dictionary<string, string>
            {
                { "version_name", ver },
                { "game_directory", plan.GameDir },
                { "assets_root", plan.AssetsDir },
                { "assets_index_name", plan.AssetIndex },
                { "version_type", J.Str(vj, "type", "release") },
                { "natives_directory", plan.NativesDir },
                { "library_directory", libRoot },
                { "classpath", string.Join(sep, plan.Classpath) },
                { "classpath_separator", sep },
                { "launcher_name", "mcroute" },
                { "launcher_version", "0.2" },
                { "auth_player_name", "Player" },
                { "auth_uuid", "00000000000000000000000000000000" },
                { "auth_access_token", "0" },
                { "auth_session", "0" },
                { "user_type", "legacy" },
                { "user_properties", "{}" },
                { "clientid", "0" },
                { "auth_xuid", "0" },
                { "resolution_width", "854" },
                { "resolution_height", "480" },
            };

            var arguments = J.Dict(vj, "arguments");
            if (arguments != null)
            {
                bool hasClasspath = false;
                var jvm = Version.ExpandArgs(J.Get(vj, "arguments") != null ? J.Get(arguments, "jvm") : null, ctx, false);
                foreach (string arg in jvm)
                {
                    if (arg == "-cp" || arg == "-classpath" || arg.StartsWith("-Djava.class.path")) hasClasspath = true;
                    plan.JvmArgs.Add(arg);
                }
                if (!hasClasspath)
                {
                    plan.JvmArgs.Add("-cp");
                    plan.JvmArgs.Add(string.Join(sep, plan.Classpath));
                }
                plan.GameArgs = Version.ExpandArgs(J.Get(arguments, "game"), ctx, false);

            }
            else
            {
                // 老版本：minecraftArguments 是一整串
                plan.JvmArgs.Add("-Djava.library.path=" + plan.NativesDir);
                plan.JvmArgs.Add("-cp");
                plan.JvmArgs.Add(string.Join(sep, plan.Classpath));
                string legacy = J.Str(vj, "minecraftArguments");
                foreach (string piece in legacy.Split(' '))
                    if (piece.Length > 0) plan.GameArgs.Add(Version.Subst(piece, ctx));
            }

            // 默认堆内存（json 没指定时补上，和 Python 版一致）
            if (!plan.JvmArgs.Any(x => x.StartsWith("-Xms"))) plan.JvmArgs.Insert(0, "-Xms512M");
            if (!plan.JvmArgs.Any(x => x.StartsWith("-Xmx"))) plan.JvmArgs.Insert(1, "-Xmx2G");

            return plan;
        }

        // ================= build =================
        public static int Build(string[] a)
        {
            string ver = Positional(a, 1);
            if (string.IsNullOrEmpty(ver)) { Console.WriteLine("[错误] 用法: build <版本>"); return 1; }
            string baseDir = Version.BaseDir(Arg(a, "--dir"));
            var plan = PlanLaunch(baseDir, ver, Arg(a, "--java"), Arg(a, "--game-dir"));
            if (plan == null) return 1;

            Console.WriteLine("=" + new string('=', 60));
            Console.WriteLine("build 报告");
            Console.WriteLine("=" + new string('=', 60));
            Console.WriteLine("  版本       : " + plan.Version);
            Console.WriteLine("  mainClass  : " + plan.MainClass);
            Console.WriteLine("  Java       : " + plan.Java + "   (Java " + plan.JavaMajor + ")");
            Console.WriteLine("  工作目录   : " + plan.GameDir);
            Console.WriteLine("  classpath  : " + plan.Classpath.Count + " 个条目（按规则跳过 " + plan.SkippedLibs + " 个库）");
            Console.WriteLine("  natives    : " + plan.NativesJars.Count + " 个 jar → " + plan.NativesDir);
            Console.WriteLine("  assets索引 : " + plan.AssetIndex);
            Console.WriteLine("  参数       : jvm " + plan.JvmArgs.Count + " + game " + plan.GameArgs.Count);

            if (plan.Missing.Count == 0)
            {
                Console.WriteLine("[OK] 所有依赖文件都在本地");
                return 0;
            }
            Console.WriteLine("[缺失] 共 " + plan.Missing.Count + " 个：");
            foreach (string m in plan.Missing.Take(10)) Console.WriteLine("  - " + m);
            return 2;
        }

        // ================= launch =================
        public static int Launch(string[] a)
        {
            string ver = Positional(a, 1);
            if (string.IsNullOrEmpty(ver)) { Console.WriteLine("[错误] 用法: launch <版本>"); return 1; }
            string baseDir = Version.BaseDir(Arg(a, "--dir"));
            var plan = PlanLaunch(baseDir, ver, Arg(a, "--java"), Arg(a, "--game-dir"));
            if (plan == null) return 1;

            if (plan.Missing.Count > 0 && !Has(a, "--force"))
            {
                Console.WriteLine("[拒绝启动] 还缺 " + plan.Missing.Count + " 个文件（先 fetch 或 --force）");
                foreach (string m in plan.Missing.Take(5)) Console.WriteLine("  - " + m);
                return 1;
            }

            Directory.CreateDirectory(plan.GameDir);
            Directory.CreateDirectory(plan.NativesDir);
            ExtractNatives(plan);

            // --memory 6G 覆盖默认堆大小
            string mem = Commands.Arg(a, "--memory");
            if (!string.IsNullOrEmpty(mem))
            {
                for (int i = 0; i < plan.JvmArgs.Count; i++)
                    if (plan.JvmArgs[i].StartsWith("-Xmx")) plan.JvmArgs[i] = "-Xmx" + mem;
            }

            var argv = new List<string>();
            argv.Add(plan.Java);
            argv.AddRange(plan.JvmArgs);
            argv.Add(plan.MainClass);
            argv.AddRange(plan.GameArgs);

            // argv[0] 是 java 自己，作为 FileName 传了，命令行里不能重复
            string cmdline = string.Join(" ", argv.Skip(1).Select(Quote));
            Console.WriteLine("[启动] " + ver + "  （工作目录 " + plan.GameDir + "）");

            var psi = new ProcessStartInfo(plan.Java, cmdline)
            {
                WorkingDirectory = plan.GameDir,
                UseShellExecute = false,
                RedirectStandardOutput = Has(a, "--console"),
                RedirectStandardError = Has(a, "--console"),
            };
            var proc = Process.Start(psi);
            if (proc == null) { Console.WriteLine("[错误] 启动失败"); return 1; }
            Console.WriteLine("[启动] PID = " + proc.Id);
            if (Has(a, "--wait")) proc.WaitForExit();
            return 0;
        }

        public static void ExtractNativesPublic(Plan plan) { ExtractNatives(plan); }

        private static void ExtractNatives(Plan plan)
        {
            int count = 0;
            foreach (string jar in plan.NativesJars)
            {
                if (!File.Exists(jar)) continue;
                try
                {
                    using (var zip = ZipFile.OpenRead(jar))
                    {
                        foreach (var entry in zip.Entries)
                        {
                            if (entry.FullName.EndsWith("/")) continue;
                            if (entry.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase)) continue;
                            string target = Path.Combine(plan.NativesDir, Path.GetFileName(entry.FullName));
                            if (File.Exists(target)) continue;
                            entry.ExtractToFile(target, true);
                            count++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[警告] 解压 natives 失败: " + Path.GetFileName(jar) + " -> " + ex.Message);
                }
            }
            if (count > 0) Console.WriteLine("[启动] 已解压 " + count + " 个 natives 文件");
        }

        public static string Quote(string s)
        {
            if (s.IndexOf(' ') < 0 && s.IndexOf('"') < 0) return s;
            return "\"" + s.Replace("\"", "\\\"") + "\"";
        }

        // ================= 参数解析小工具 =================
        public static string Arg(string[] a, string name)
        {
            for (int i = 0; i < a.Length - 1; i++)
                if (a[i] == name) return a[i + 1];
            return null;
        }

        public static bool Has(string[] a, string name)
        {
            return a.Any(x => x == name);
        }

        public static string Positional(string[] a, int index)
        {
            var list = new List<string>();
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i].StartsWith("-")) { i++; continue; }
                list.Add(a[i]);
            }
            return list.Count > index ? list[index] : null;
        }
    }
}
