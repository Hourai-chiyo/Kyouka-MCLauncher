using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Mcroute
{
    /// <summary>一个「实例」：同一 MC 版本可以有多个，各自独立的 mods / saves / config。</summary>
    public class InstanceInfo
    {
        public string Name;
        public string Version;      // 底板版本（如 1.20.1 / 1.20.1-forge-47.4.23）
        public string Dir;           // <数据目录>/versions/<Name>
        public string Created;
        public string Note;

        public string Display
        {
            get { return Name + (string.IsNullOrEmpty(Version) ? "" : "  ·  " + Version); }
        }
    }

    /// <summary>
    /// 多实例：和「版本隔离」共用 `versions/&lt;名字&gt;/` 结构，额外写一个 `instance.json`
    /// 记录底板版本；同目录还有 `&lt;名字&gt;.json`（inheritsFrom 底板版本）。
    ///
    /// 这样 HMCL / PCL / 官方启动器都能把它当成一个普通版本识别并启动，
    /// 而对 Kyouka 来说它又是一个独立的、可命名的实例。
    /// </summary>
    public static class Instances
    {
        public const string LegacyDirName = "instances";   // 旧结构（v1.4.0 之前）
        public const string MetaFile = "instance.json";

        public static string VersionDir(string baseDir, string name)
        {
            return Path.Combine(baseDir, "versions", name);
        }

        /// <summary>兼容旧名。</summary>
        public static string InstanceDir(string baseDir, string name)
        {
            return VersionDir(baseDir, name);
        }

        public static List<InstanceInfo> List(string baseDir)
        {
            var list = new List<InstanceInfo>();
            try
            {
                string root = Path.Combine(baseDir, "versions");
                if (!Directory.Exists(root)) return list;

                foreach (string dir in Directory.GetDirectories(root))
                {
                    string name = Path.GetFileName(dir);
                    if (!File.Exists(Path.Combine(dir, MetaFile))) continue;
                    var info = Load(baseDir, name);
                    if (info != null) list.Add(info);
                }
            }
            catch { }
            return list.OrderByDescending(i => i.Created).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static InstanceInfo Load(string baseDir, string name)
        {
            try
            {
                string dir = VersionDir(baseDir, name);
                if (!Directory.Exists(dir)) return null;
                var info = new InstanceInfo { Name = name, Dir = dir };

                string meta = Path.Combine(dir, MetaFile);
                if (File.Exists(meta))
                {
                    object json = J.Parse(File.ReadAllText(meta, Encoding.UTF8));
                    info.Name = J.Str(json, "name", name);
                    info.Version = J.Str(json, "version");
                    info.Created = J.Str(json, "created");
                    info.Note = J.Str(json, "note");
                }
                return info;
            }
            catch { return null; }
        }

        public static bool Exists(string baseDir, string name)
        {
            return !string.IsNullOrWhiteSpace(name) && Directory.Exists(VersionDir(baseDir, name));
        }

        public static bool ValidName(string name, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(name)) { error = "名字不能为空"; return false; }
            if (name.Length > 40) { error = "名字太长（40 字符以内）"; return false; }
            foreach (char c in Path.GetInvalidFileNameChars())
                if (name.IndexOf(c) >= 0) { error = "名字里不能有字符：" + c; return false; }
            if (name.Trim() != name) { error = "名字首尾不能有空格"; return false; }
            return true;
        }

        public static bool Create(string baseDir, string name, string version, out string error)
        {
            error = null;
            if (!ValidName(name, out error)) return false;
            if (Exists(baseDir, name)) { error = "已经存在同名版本/实例：「" + name + "」"; return false; }
            if (string.IsNullOrEmpty(version)) { error = "没选底板 MC 版本"; return false; }

            string baseJson = Path.Combine(baseDir, "versions", version, version + ".json");
            if (!File.Exists(baseJson))
            {
                error = "底板版本不存在：versions\\" + version + "\\" + version + ".json";
                return false;
            }

            try
            {
                string dir = VersionDir(baseDir, name);
                Directory.CreateDirectory(dir);
                Directory.CreateDirectory(Path.Combine(dir, "mods"));
                WriteVersionJson(dir, name, version);
                WriteMeta(dir, name, version, "");
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        public static bool Copy(string baseDir, string name, string newName, out string error)
        {
            error = null;
            if (!ValidName(newName, out error)) return false;
            if (!Exists(baseDir, name)) { error = "源实例不存在"; return false; }
            if (Exists(baseDir, newName)) { error = "已经存在同名版本/实例：「" + newName + "」"; return false; }

            try
            {
                string src = VersionDir(baseDir, name);
                string dst = VersionDir(baseDir, newName);
                var info = Load(baseDir, name) ?? new InstanceInfo { Version = "" };

                CopyDir(src, dst);
                // 旧名字的 json/jar 要改成新名字
                string oldJson = Path.Combine(dst, name + ".json");
                if (File.Exists(oldJson)) { try { File.Delete(oldJson); } catch { } }
                string oldJar = Path.Combine(dst, name + ".jar");
                if (File.Exists(oldJar)) { try { File.Move(oldJar, Path.Combine(dst, newName + ".jar")); } catch { } }

                WriteVersionJson(dst, newName, info.Version);
                WriteMeta(dst, newName, info.Version, info.Note);
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        public static bool Rename(string baseDir, string name, string newName, out string error)
        {
            error = null;
            if (!ValidName(newName, out error)) return false;
            if (!Exists(baseDir, name)) { error = "实例不存在"; return false; }
            if (Exists(baseDir, newName)) { error = "已经存在同名版本/实例：「" + newName + "」"; return false; }

            try
            {
                string src = VersionDir(baseDir, name);
                string dst = VersionDir(baseDir, newName);
                Directory.Move(src, dst);

                var info = Load(baseDir, newName) ?? new InstanceInfo { Version = "" };
                string oldJson = Path.Combine(dst, name + ".json");
                if (File.Exists(oldJson)) { try { File.Delete(oldJson); } catch { } }
                string oldJar = Path.Combine(dst, name + ".jar");
                if (File.Exists(oldJar)) { try { File.Move(oldJar, Path.Combine(dst, newName + ".jar")); } catch { } }

                WriteVersionJson(dst, newName, info.Version);
                WriteMeta(dst, newName, info.Version, info.Note);
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        public static bool Delete(string baseDir, string name, out string error)
        {
            error = null;
            if (!Exists(baseDir, name)) { error = "实例不存在"; return false; }
            if (File.Exists(Path.Combine(VersionDir(baseDir, name), name + ".json")) &&
                !File.Exists(Path.Combine(VersionDir(baseDir, name), MetaFile)))
            {
                error = "这个版本不是实例（没有 instance.json），为避免误删已拒绝";
                return false;
            }
            try
            {
                Directory.Delete(VersionDir(baseDir, name), true);
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        /// <summary>
        /// 把旧版 `instances/&lt;名字&gt;/` 迁到 `versions/&lt;名字&gt;/`（v1.4.0 之前的实例）。
        /// 返回迁移的数量。
        /// </summary>
        public static int MigrateLegacy(string baseDir)
        {
            int moved = 0;
            try
            {
                string legacy = Path.Combine(baseDir, LegacyDirName);
                if (!Directory.Exists(legacy)) return 0;

                foreach (string dir in Directory.GetDirectories(legacy))
                {
                    string name = Path.GetFileName(dir);
                    if (!File.Exists(Path.Combine(dir, MetaFile))) continue;
                    if (Exists(baseDir, name)) continue;   // 已有同名，跳过，不覆盖

                    try
                    {
                        var info = Load(baseDir, name);
                        string dst = VersionDir(baseDir, name);
                        CopyDir(dir, dst);
                        Directory.Move(dir, dir + ".migrated");
                        WriteVersionJson(dst, name, info != null ? info.Version : "");
                        WriteMeta(dst, name, info != null ? info.Version : "", info != null ? info.Note : "");
                        moved++;
                    }
                    catch { }
                }

                try
                {
                    if (Directory.Exists(legacy) && Directory.GetDirectories(legacy).Length == 0) Directory.Delete(legacy, true);
                }
                catch { }
            }
            catch { }
            return moved;
        }

        public static List<string> VersionCandidates(string baseDir)
        {
            var result = new List<string>();
            try
            {
                string root = Path.Combine(baseDir, "versions");
                if (!Directory.Exists(root)) return result;
                foreach (string dir in Directory.GetDirectories(root).OrderBy(x => Path.GetFileName(x)))
                {
                    string name = Path.GetFileName(dir);
                    bool hasJson = File.Exists(Path.Combine(dir, name + ".json"));
                    if (!hasJson) continue;
                    // 实例可以作为底板（继承它的 mods 配置），但一般不这么用
                    result.Add(name);
                }
            }
            catch { }
            return result;
        }

        /// <summary>写 `&lt;名字&gt;.json`：inheritsFrom 底板版本，保留 id。</summary>
        private static void WriteVersionJson(string dir, string name, string version)
        {
            if (string.IsNullOrEmpty(version)) return;
            string json = "{\"id\":\"" + name + "\",\"inheritsFrom\":\"" + version + "\",\"jar\":\"" + version + "\"}";
            File.WriteAllText(Path.Combine(dir, name + ".json"), json, new UTF8Encoding(false));
        }

        private static void WriteMeta(string dir, string name, string version, string note)
        {
            string json = "{\"name\":" + Esc(name) + ",\"version\":" + Esc(version ?? "") +
                          ",\"created\":" + Esc(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")) +
                          ",\"note\":" + Esc(note ?? "") + "}";
            File.WriteAllText(Path.Combine(dir, MetaFile), json, new UTF8Encoding(false));
        }

        private static void CopyDir(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (string file in Directory.GetFiles(src))
            {
                string fname = Path.GetFileName(file).ToLowerInvariant();
                if (fname.EndsWith(".log") || fname.StartsWith("crash-")) continue;
                try { File.Copy(file, Path.Combine(dst, Path.GetFileName(file)), true); } catch { }
            }
            foreach (string dir in Directory.GetDirectories(src))
            {
                string name = Path.GetFileName(dir).ToLowerInvariant();
                if (name == "logs" || name == "crash-reports") continue;
                CopyDir(dir, Path.Combine(dst, Path.GetFileName(dir)));
            }
        }

        private static string Esc(string s)
        {
            if (s == null) return "\"\"";
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }
    }
}
