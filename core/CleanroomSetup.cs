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
    /// Cleanroom（1.12.2）实例一键修复。
    ///
    /// 背景：带 `++relauncher-*.jar` 的整合包是两段式启动，第二段要由 Relauncher
    /// 用 Java 21/25 + Foundation + Cleanroom 重新起游戏。跨机器最容易坏的四件事：
    ///   1. cleanroom_relauncher.cfg 里是打包者机器的绝对路径
    ///   2. Class Path 缺 client jar / Cleanroom jar（缺 binpatches → class data 为空）
    ///   3. Class Path 里原版 forge 排在 Cleanroom 前（会设 SecurityManager → Java 17+ 崩）
    ///   4. 缺 LWJGL3 natives（lwjgl.dll not found）+ Java 选成了 8
    ///
    /// 这个类按 `relauncher/mmcpack/patches/*.json` 的清单把上面四件一次做对。
    /// </summary>
    public static class CleanroomSetup
    {
        public const string NativesDirName = "natives-lwjgl3";

        /// <summary>是不是 Cleanroom 实例（看 relauncher 目录或 ++relauncher jar）。</summary>
        public static bool Detect(string baseDir, string ver)
        {
            try
            {
                string vdir = Path.Combine(baseDir, "versions", ver);
                if (Directory.Exists(Path.Combine(vdir, "relauncher"))) return true;
                string mods = Path.Combine(vdir, "mods");
                if (Directory.Exists(mods) &&
                    Directory.GetFiles(mods, "++relauncher*").Length > 0) return true;
                if (Directory.Exists(mods) &&
                    Directory.GetFiles(mods, "*cleanroom-relauncher*.jar").Length > 0) return true;
            }
            catch { }
            return false;
        }

        public static string ConfigPath(string baseDir, string ver)
        {
            return Path.Combine(baseDir, "versions", ver, @"config\cleanroom_relauncher.cfg");
        }

        public static string PatchesDir(string baseDir, string ver)
        {
            return Path.Combine(baseDir, "versions", ver, @"relauncher\mmcpack\patches");
        }

        /// <summary>
        /// 确保该实例的第二段环境可用。已就绪时几乎零开销（只做存在性检查）。
        /// report 里是给用户看的说明；返回 false 时 report 是失败原因。
        /// </summary>
        public static bool Ensure(string baseDir, string ver, out string report)
        {
            return Ensure(baseDir, ver, false, out report);
        }

        public static bool Ensure(string baseDir, string ver, bool dryRun, out string report)
        {
            report = "";
            var notes = new List<string>();
            try
            {
                if (!Detect(baseDir, ver))
                {
                    report = "不是 Cleanroom 实例（没有 relauncher/）";
                    return false;
                }

                string vdir = Path.Combine(baseDir, "versions", ver);
                string patches = PatchesDir(baseDir, ver);
                if (!Directory.Exists(patches))
                {
                    report = "缺少 relauncher\\mmcpack\\patches\\*.json，无法确定运行库清单";
                    return false;
                }

                // ---- 1) 收集 mmcpack 清单里的库 ----
                var libs = ReadPatchLibraries(patches);
                if (libs.Count == 0)
                {
                    report = "patches 里没有可用的 libraries[]";
                    return false;
                }

                // ---- 2) 补齐缺失的库 ----
                int downloaded = 0, skipped = 0, failed = 0;
                var missing = new List<string>();
                foreach (var lib in libs)
                {
                    string dest = Path.Combine(baseDir, "versions", "libraries", lib.RelPath);
                    if (File.Exists(dest) && (string.IsNullOrEmpty(lib.Sha1) || !dryRun))
                    {
                        skipped++;
                        continue;
                    }
                    if (dryRun) { missing.Add(lib.Name); continue; }

                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(dest));
                        string info;
                        string status = Http.Download(lib.Url, dest, string.IsNullOrEmpty(lib.Sha1) ? null : lib.Sha1, out info, false);
                        if (status == "fail" || !File.Exists(dest)) { failed++; missing.Add(lib.Name); }
                        else downloaded++;
                    }
                    catch { failed++; missing.Add(lib.Name); }
                }
                if (downloaded > 0) notes.Add("补了 " + downloaded + " 个运行库");
                if (failed > 0) notes.Add("有 " + failed + " 个库下载失败（" + string.Join("、", missing.Take(3)) + "）");

                // ---- 3) 拼 Class Path：client jar → Cleanroom jar → 库 ----
                string clientJar = Path.Combine(vdir, ver + ".jar");
                string cleanroomJar = libs
                    .Select(l => Path.Combine(baseDir, "versions", "libraries", l.RelPath))
                    .FirstOrDefault(p => p.Replace('\\', '/').Contains("/com/cleanroommc/cleanroom/") && File.Exists(p));

                // 兼容：从 1.12.2-Forge_* 这类 id 里找客户端 jar
                if (!File.Exists(clientJar))
                {
                    var cand = Directory.GetFiles(vdir, "*.jar")
                        .Where(f => !f.EndsWith(".disabled"))
                        .OrderByDescending(f => new FileInfo(f).Length)
                        .FirstOrDefault();
                    if (cand != null) clientJar = cand;
                }

                var cp = new List<string>();
                if (File.Exists(clientJar)) cp.Add(clientJar);
                else notes.Add("缺客户端 jar");
                if (cleanroomJar != null) cp.Add(cleanroomJar);
                else notes.Add("清单里没有 Cleanroom jar");
                foreach (var lib in libs)
                {
                    string p = Path.Combine(baseDir, "versions", "libraries", lib.RelPath);
                    if (File.Exists(p) && !cp.Contains(p)) cp.Add(p);
                }

                // ---- 4) 解压 LWJGL3 natives + 选 Java ----
                string natives = Path.Combine(vdir, NativesDirName);
                int dlls = 0;
                if (!dryRun)
                {
                    dlls = ExtractNatives(cp, natives);
                    if (dlls > 0) notes.Add("解出 " + dlls + " 个 natives DLL");
                }

                string javaw = Java.FindBestAtLeast(baseDir, 17);
                if (string.IsNullOrEmpty(javaw)) notes.Add("没找到 Java 17+（Cleanroom 不能用 Java 8）");

                // ---- 5) 写回 cfg ----
                string cfgPath = ConfigPath(baseDir, ver);
                if (!File.Exists(cfgPath))
                {
                    report = "缺少 config\\cleanroom_relauncher.cfg";
                    return false;
                }

                bool cfgChanged = false;
                string cfg = File.ReadAllText(cfgPath, Encoding.UTF8);

                // Class Path
                var mCp = Regex.Match(cfg, "S:\"Class Path\"=([^\\r\\n]*)");
                if (mCp.Success)
                {
                    string newCp = string.Join(";", cp);
                    if (!string.Equals(mCp.Groups[1].Value.Trim(), newCp, StringComparison.Ordinal))
                    {
                        if (!dryRun) cfg = cfg.Substring(0, mCp.Index) + "S:\"Class Path\"=" + newCp + cfg.Substring(mCp.Index + mCp.Length);
                        cfgChanged = true;
                        notes.Add("Class Path 已重写（" + cp.Count + " 条）");
                    }
                }
                else notes.Add("cfg 里没有 Class Path 行");

                // JVM Arguments：补 librarypath（保留原参数）
                if (dlls > 0 || dryRun)
                {
                    string libPath = natives;
                    string extra = " -Dorg.lwjgl.librarypath=" + libPath + " -Djava.library.path=" + libPath;
                    var mJvm = Regex.Match(cfg, "(?m)^[ \\t]*S:\"JVM Arguments\"=(.*)$");
                    if (mJvm.Success)
                    {
                        string cur = mJvm.Groups[1].Value;
                        if (!cur.Contains("org.lwjgl.librarypath"))
                        {
                            if (!dryRun) cfg = cfg.Substring(0, mJvm.Index) + mJvm.Groups[0].Value.TrimEnd() + extra + cfg.Substring(mJvm.Index + mJvm.Length);
                            cfgChanged = true;
                            notes.Add("JVM Arguments 已补 librarypath");
                        }
                    }
                    else notes.Add("cfg 里没有 JVM Arguments 行");
                }

                // Java Path
                if (!string.IsNullOrEmpty(javaw))
                {
                    var mJp = Regex.Match(cfg, "S:\"Java Path\"=([^\\r\\n]*)");
                    string want = javaw;
                    if (mJp.Success)
                    {
                        string cur = mJp.Groups[1].Value.Trim();
                        if (!string.Equals(cur, want, StringComparison.OrdinalIgnoreCase))
                        {
                            if (!dryRun) cfg = cfg.Substring(0, mJp.Index) + "S:\"Java Path\"=" + want + cfg.Substring(mJp.Index + mJp.Length);
                            cfgChanged = true;
                            notes.Add("Java Path → " + Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(want))));
                        }
                    }
                }

                if (cfgChanged && !dryRun)
                {
                    try { File.Copy(cfgPath, cfgPath + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), true); } catch { }
                    File.WriteAllText(cfgPath, cfg, new UTF8Encoding(false));
                }

                if (dryRun)
                {
                    report = "【预演】" + (notes.Count == 0 ? "已就绪" : string.Join(" · ", notes));
                    return true;
                }

                report = notes.Count == 0
                    ? "Cleanroom 环境已就绪（" + cp.Count + " 条 classpath）"
                    : "Cleanroom 已修复：" + string.Join(" · ", notes);
                return failed == 0;
            }
            catch (Exception ex)
            {
                report = "Cleanroom 修复失败：" + ex.Message;
                return false;
            }
        }

        private class PatchLib
        {
            public string Name;
            public string RelPath;
            public string Url;
            public string Sha1;
        }

        /// <summary>按 patches 里声明的顺序读 libraries[]。</summary>
        private static List<PatchLib> ReadPatchLibraries(string patchesDir)
        {
            var list = new List<PatchLib>();
            var order = new[] { "net.minecraft.json", "net.minecraftforge.json", "org.lwjgl3.json" };
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string file in order)
            {
                string path = Path.Combine(patchesDir, file);
                if (!File.Exists(path)) continue;
                object json;
                try { json = J.Parse(File.ReadAllText(path, Encoding.UTF8)); }
                catch { continue; }

                foreach (object lib in Version.AsList(J.Get(json, "libraries")) ?? new List<object>())
                {
                    string name = J.Str(lib, "name");
                    if (string.IsNullOrEmpty(name) || !seen.Add(name)) continue;

                    object art = J.Dict(J.Get(lib, "downloads"), "artifact");
                    string url = art != null ? J.Str(art, "url") : null;
                    if (string.IsNullOrEmpty(url)) continue;

                    string rel = J.Str(art, "path");
                    if (string.IsNullOrEmpty(rel))
                    {
                        rel = MavenPath(name);
                        if (rel == null) continue;
                    }

                    list.Add(new PatchLib
                    {
                        Name = name,
                        RelPath = rel.Replace('/', '\\'),
                        Url = url,
                        Sha1 = J.Str(art, "sha1"),
                    });
                }
            }
            return list;
        }

        /// <summary>"g:a:v[:classifier]" → "g/a/v/a-v[-classifier].jar"</summary>
        private static string MavenPath(string name)
        {
            var parts = name.Split(':');
            if (parts.Length < 3) return null;
            string g = parts[0], a = parts[1], v = parts[2];
            string cls = parts.Length > 3 ? "-" + parts[3] : "";
            return g.Replace('.', '/') + "/" + a + "/" + v + "/" + a + "-" + v + cls + ".jar";
        }

        /// <summary>把 classpath 里 *-natives-windows.jar 的 DLL 解到 natives 目录。</summary>
        private static int ExtractNatives(List<string> classpath, string nativesDir)
        {
            int count = 0;
            try
            {
                Directory.CreateDirectory(nativesDir);
                foreach (string jar in classpath)
                {
                    string name = Path.GetFileName(jar).ToLowerInvariant();
                    if (!name.Contains("natives") || !name.Contains("windows")) continue;
                    if (name.Contains("arm64") || name.Contains("-x86.")) continue;

                    try
                    {
                        using (var zip = ZipFile.OpenRead(jar))
                        {
                            foreach (var entry in zip.Entries)
                            {
                                string ext = Path.GetExtension(entry.FullName).ToLowerInvariant();
                                if (ext != ".dll") continue;
                                string dest = Path.Combine(nativesDir, Path.GetFileName(entry.FullName));
                                if (File.Exists(dest)) { count++; continue; }
                                try { entry.ExtractToFile(dest, true); count++; } catch { }
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return count;
        }
    }
}
