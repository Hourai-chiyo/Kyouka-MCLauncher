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
        public string Version;      // 对应 versions/<Version>
        public string Dir;           // <数据目录>/instances/<Name>
        public string Created;
        public string Note;

        public string Display
        {
            get { return Name + (string.IsNullOrEmpty(Version) ? "" : "  ·  " + Version); }
        }
    }

    /// <summary>
    /// 多实例：`<数据目录>/instances/&lt;名字&gt;/` 是独立游戏目录（版本隔离），
    /// 里面放 `instance.json` 记录它用哪个 MC 版本。
    /// 启动时把 `--game-dir` 指到实例目录即可，Core 不用改。
    /// </summary>
    public static class Instances
    {
        public const string DirName = "instances";
        public const string MetaFile = "instance.json";

        public static string Root(string baseDir)
        {
            return Path.Combine(baseDir, DirName);
        }

        public static string InstanceDir(string baseDir, string name)
        {
            return Path.Combine(baseDir, DirName, name);
        }

        public static List<InstanceInfo> List(string baseDir)
        {
            var list = new List<InstanceInfo>();
            string root = Root(baseDir);
            if (!Directory.Exists(root)) return list;

            try
            {
                foreach (string dir in Directory.GetDirectories(root))
                {
                    var info = Load(baseDir, Path.GetFileName(dir));
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
                string dir = InstanceDir(baseDir, name);
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
            return !string.IsNullOrWhiteSpace(name) && Directory.Exists(InstanceDir(baseDir, name));
        }

        /// <summary>名字里不能有路径分隔符/非法字符。</summary>
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
            if (Exists(baseDir, name)) { error = "实例「" + name + "」已存在"; return false; }
            if (string.IsNullOrEmpty(version)) { error = "没选 MC 版本"; return false; }

            try
            {
                string dir = InstanceDir(baseDir, name);
                Directory.CreateDirectory(dir);
                Directory.CreateDirectory(Path.Combine(dir, "mods"));
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
            if (Exists(baseDir, newName)) { error = "实例「" + newName + "」已存在"; return false; }

            try
            {
                string src = InstanceDir(baseDir, name);
                string dst = InstanceDir(baseDir, newName);
                CopyDir(src, dst);
                var info = Load(baseDir, newName) ?? new InstanceInfo { Name = newName, Version = "" };
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
            if (Exists(baseDir, newName)) { error = "实例「" + newName + "」已存在"; return false; }

            try
            {
                string src = InstanceDir(baseDir, name);
                string dst = InstanceDir(baseDir, newName);
                Directory.Move(src, dst);
                var info = Load(baseDir, newName) ?? new InstanceInfo { Version = "" };
                WriteMeta(dst, newName, info.Version, info.Note);
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        public static bool Delete(string baseDir, string name, out string error)
        {
            error = null;
            if (!Exists(baseDir, name)) { error = "实例不存在"; return false; }
            try
            {
                Directory.Delete(InstanceDir(baseDir, name), true);
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        /// <summary>可选作实例底板的版本列表（和启动页列表同一套过滤规则）。</summary>
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
                    bool isPack = File.Exists(Path.Combine(dir, MetaFile));
                    if (!hasJson && !isPack && !Directory.Exists(Path.Combine(dir, "mods"))) continue;
                    result.Add(name);
                }
            }
            catch { }
            return result;
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
                string ext = Path.GetExtension(file).ToLowerInvariant();
                // logs / crash-reports 不复制，省空间也没意义
                if (ext == ".log" || ext == ".txt" && Path.GetFileName(file).StartsWith("crash")) continue;
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
