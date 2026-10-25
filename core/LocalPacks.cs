using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Mcroute
{
    /// <summary>资源包 / 光影包（本地文件管理，不做下载）。</summary>
    public class LocalPack
    {
        public string FilePath;
        public string FileName;
        public long Size;
        public bool Disabled;
        public string Kind;          // "resourcepacks" / "shaderpacks"
        public string Title;         // 显示名
        public string Description;   // pack.mcmeta 里的描述（光影没有）
        public int PackFormat;       // 资源包格式号，0 = 未知

        public string SizeText()
        {
            if (Size >= 1024 * 1024) return (Size / 1024.0 / 1024.0).ToString("F1") + " MB";
            if (Size >= 1024) return (Size / 1024.0).ToString("F0") + " KB";
            return Size + " B";
        }
    }

    /// <summary>
    /// 资源包（resourcepacks/）与光影（shaderpacks/）的扫描、启停、删除、导入。
    ///
    /// 约定和 mod 一致：禁用 = 在文件名后加 `.disabled`；
    /// 版本隔离开启时目录是 `versions/&lt;版本&gt;/resourcepacks`。
    /// </summary>
    public static class LocalPacks
    {
        public const string ResourcePacks = "resourcepacks";
        public const string ShaderPacks = "shaderpacks";
        public const string DisabledSuffix = ".disabled";

        public static string PackDir(string baseDir, string ver, string kind)
        {
            return Path.Combine(baseDir, "versions", ver, kind);
        }

        public static List<LocalPack> Scan(string baseDir, string ver, string kind)
        {
            var list = new List<LocalPack>();
            string dir = PackDir(baseDir, ver, kind);
            if (!Directory.Exists(dir)) return list;

            try
            {
                foreach (string file in Directory.GetFiles(dir))
                {
                    string name = Path.GetFileName(file);
                    if (name.StartsWith(".download-", StringComparison.OrdinalIgnoreCase)) continue;

                    bool disabled = name.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase);
                    string realName = disabled ? name.Substring(0, name.Length - DisabledSuffix.Length) : name;
                    string ext = Path.GetExtension(realName).ToLowerInvariant();
                    if (ext != ".zip" && ext != ".jar") continue;

                    var p = new LocalPack
                    {
                        FilePath = file,
                        FileName = realName,
                        Size = new FileInfo(file).Length,
                        Disabled = disabled,
                        Kind = kind,
                        Title = Path.GetFileNameWithoutExtension(realName),
                    };
                    if (kind == ResourcePacks) ReadPackMeta(p);
                    list.Add(p);
                }

                // 文件夹形式的资源包（resourcepacks/xxx/pack.mcmeta 或 xxx.disabled/pack.mcmeta）
                foreach (string sub in Directory.GetDirectories(dir))
                {
                    string meta = Path.Combine(sub, "pack.mcmeta");
                    if (!File.Exists(meta)) continue;

                    string folder = Path.GetFileName(sub);
                    bool isDisabled = folder.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
                    string display = isDisabled
                        ? folder.Substring(0, folder.Length - ".disabled".Length)
                        : folder;

                    var p = new LocalPack
                    {
                        FilePath = sub,
                        FileName = display,
                        Size = 0,
                        Disabled = isDisabled,
                        Kind = kind,
                        Title = display,
                    };
                    TryReadMetaText(File.ReadAllText(meta, Encoding.UTF8), p);
                    list.Add(p);
                }
            }
            catch { }

            return list.OrderByDescending(x => !x.Disabled).ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static void ReadPackMeta(LocalPack p)
        {
            try
            {
                using (var zip = ZipFile.OpenRead(p.FilePath))
                {
                    var entry = zip.Entries.FirstOrDefault(e =>
                        string.Equals(e.FullName, "pack.mcmeta", StringComparison.OrdinalIgnoreCase));
                    if (entry == null) return;
                    using (var sr = new StreamReader(entry.Open(), Encoding.UTF8))
                        TryReadMetaText(sr.ReadToEnd(), p);
                }
            }
            catch { }
        }

        private static void TryReadMetaText(string json, LocalPack p)
        {
            try
            {
                object root = J.Parse(json);
                object pack = J.Dict(root, "pack");
                if (pack == null) return;
                p.PackFormat = J.Int(pack, "pack_format", 0);

                object desc = J.Get(pack, "description");
                if (desc is string) p.Description = (string)desc;
                else if (desc != null)
                {
                    string text = J.Str(desc, "text");
                    if (!string.IsNullOrEmpty(text)) p.Description = text;
                }
                // 描述里常带 §颜色代码，显示时去掉
                if (!string.IsNullOrEmpty(p.Description))
                    p.Description = System.Text.RegularExpressions.Regex.Replace(p.Description, "§.", "");
            }
            catch { }
        }

        /// <summary>启用 / 禁用（改扩展名）。</summary>
        public static bool SetEnabled(LocalPack p, bool enabled, out string error)
        {
            error = null;
            try
            {
                if (p.Disabled == !enabled) return true;
                if (!File.Exists(p.FilePath) && !Directory.Exists(p.FilePath))
                {
                    error = "文件不存在：" + p.FilePath;
                    return false;
                }

                string target;
                if (enabled)
                    target = p.FilePath.Substring(0, p.FilePath.Length - DisabledSuffix.Length);
                else
                    target = p.FilePath + DisabledSuffix;

                if (File.Exists(p.FilePath))
                    File.Move(p.FilePath, target);
                else
                    Directory.Move(p.FilePath, target);

                p.FilePath = target;
                p.Disabled = !enabled;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static bool Delete(LocalPack p, out string error)
        {
            error = null;
            try
            {
                if (File.Exists(p.FilePath)) File.Delete(p.FilePath);
                else if (Directory.Exists(p.FilePath)) Directory.Delete(p.FilePath, true);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>把外部文件复制进对应的包目录。</summary>
        public static bool Import(string baseDir, string ver, string kind, string srcPath, out string message)
        {
            message = "";
            try
            {
                if (!File.Exists(srcPath)) { message = "文件不存在"; return false; }
                string ext = Path.GetExtension(srcPath).ToLowerInvariant();
                if (ext != ".zip" && ext != ".jar") { message = "只支持 .zip / .jar"; return false; }

                string dir = PackDir(baseDir, ver, kind);
                Directory.CreateDirectory(dir);
                string dest = Path.Combine(dir, Path.GetFileName(srcPath));
                if (File.Exists(dest)) dest = Path.Combine(dir,
                    Path.GetFileNameWithoutExtension(srcPath) + "-" + DateTime.Now.ToString("HHmmss") + ext);
                File.Copy(srcPath, dest);
                message = "已导入 " + Path.GetFileName(dest);
                return true;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return false;
            }
        }
    }
}
