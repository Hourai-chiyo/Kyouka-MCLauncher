using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Mcroute
{
    /// <summary>一个本地 mod 文件。</summary>
    public class ModInfo
    {
        public string FilePath;      // 完整路径
        public string FileName;      // 文件名（含 .jar / .jar.disabled）
        public string Name;          // 显示名
        public string Version;       // mod 版本
        public string Loader;        // Fabric / Forge / NeoForge / Quilt / 未知
        public long Size;
        public bool Disabled;

        public string SizeText()
        {
            return Size >= 1024 * 1024
                ? (Size / 1024.0 / 1024.0).ToString("F1") + " MB"
                : (Size / 1024.0).ToString("F0") + " KB";
        }
    }

    /// <summary>
    /// 本地 mods 目录扫描：读 jar 里的元数据，支持 Fabric / Forge / NeoForge / 老 Forge。
    /// 只读文件，不联网。
    /// </summary>
    public static class Mods
    {
        public static string ModsDir(string baseDir, string ver)
        {
            return Path.Combine(baseDir, "versions", ver, "mods");
        }

        /// <summary>扫描一个版本的 mods 目录。</summary>
        public static List<ModInfo> Scan(string baseDir, string ver)
        {
            var list = new List<ModInfo>();
            string dir = ModsDir(baseDir, ver);
            if (!Directory.Exists(dir)) return list;

            foreach (string file in Directory.GetFiles(dir).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                string name = Path.GetFileName(file);
                bool jar = name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase);
                bool disabled = name.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase);
                if (!jar && !disabled) continue;

                var info = new ModInfo
                {
                    FilePath = file,
                    FileName = name,
                    Disabled = disabled,
                    Size = new FileInfo(file).Length,
                };

                try
                {
                    string full = disabled ? file.Substring(0, file.Length - ".disabled".Length) : file;
                    FillFromJar(info, full);
                }
                catch { }

                if (string.IsNullOrEmpty(info.Name))
                {
                    // 解析失败就用文件名（去掉 .jar[.disabled]）
                    string clean = name;
                    if (clean.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
                        clean = clean.Substring(0, clean.Length - ".disabled".Length);
                    if (clean.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                        clean = clean.Substring(0, clean.Length - 4);
                    info.Name = clean;
                }
                if (string.IsNullOrEmpty(info.Version)) info.Version = "-";
                if (string.IsNullOrEmpty(info.Loader)) info.Loader = "未知";

                list.Add(info);
            }
            return list;
        }

        /// <summary>读 jar 元数据（按 Fabric → Forge → 老 Forge 顺序尝试）。</summary>
        private static void FillFromJar(ModInfo info, string jarPath)
        {
            using (var zip = ZipFile.OpenRead(jarPath))
            {
                // 1) Fabric / Quilt
                var fabric = zip.GetEntry("fabric.mod.json");
                if (fabric != null)
                {
                    string text = ReadText(fabric);
                    object json = null;
                    try { json = J.Parse(text); } catch { }
                    if (json != null)
                    {
                        info.Name = J.Str(json, "name");
                        if (string.IsNullOrEmpty(info.Name)) info.Name = J.Str(json, "id");
                        info.Version = J.Str(json, "version");
                        info.Loader = "Fabric";
                        return;
                    }
                }

                // 2) Forge / NeoForge：META-INF/mods.toml（或旧位置 META-INF/MANIFEST.MF 里）
                var toml = zip.GetEntry("META-INF/mods.toml");
                if (toml != null)
                {
                    string text = ReadText(toml);
                    info.Name = TomlValue(text, "displayName");
                    if (string.IsNullOrEmpty(info.Name)) info.Name = TomlValue(text, "modId");
                    info.Version = TomlValue(text, "version");
                    info.Loader = text.IndexOf("neoforge", StringComparison.OrdinalIgnoreCase) >= 0 ? "NeoForge" : "Forge";
                    return;
                }

                // 3) 1.7.10 老 Forge：mcmod.info（JSON 数组）
                var oldInfo = zip.GetEntry("mcmod.info");
                if (oldInfo != null)
                {
                    string text = ReadText(oldInfo);
                    try
                    {
                        object json = J.Parse(text);
                        var arr = Version.AsList(json);
                        object first = null;
                        if (arr != null && arr.Count > 0) first = arr[0];
                        else
                        {
                            // 有些 mcmod.info 是 {"modList":[ {...} ]}
                            var modList = Version.AsList(J.Get(json, "modList"));
                            if (modList != null && modList.Count > 0) first = modList[0];
                        }
                        if (first != null)
                        {
                            info.Name = J.Str(first, "name");
                            if (string.IsNullOrEmpty(info.Name)) info.Name = J.Str(first, "modid");
                            info.Version = J.Str(first, "version");
                            info.Loader = "Forge";
                            return;
                        }
                    }
                    catch { }
                }

                // 4) 什么都没有，但 jar 里有 mods.toml / fabric.mod.json 之外的线索就放弃
            }
        }

        private static string ReadText(ZipArchiveEntry entry)
        {
            using (var stream = entry.Open())
            using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                return reader.ReadToEnd();
        }

        /// <summary>从 mods.toml 里取 key = "value"（只认第一个 [[mods]] 块）。</summary>
        private static string TomlValue(string text, string key)
        {
            var m = Regex.Match(text, @"(?m)^\s*" + Regex.Escape(key) + @"\s*=\s*""([^""]*)""");
            return m.Success ? m.Groups[1].Value : null;
        }

        /// <summary>启用 / 禁用（重命名 .jar ↔ .jar.disabled）。</summary>
        public static bool SetEnabled(ModInfo mod, bool enabled, out string error)
        {
            error = null;
            try
            {
                if (mod.Disabled == !enabled) return true;   // 已经是目标状态

                string target;
                if (enabled)
                {
                    if (!mod.FilePath.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
                        return true;
                    target = mod.FilePath.Substring(0, mod.FilePath.Length - ".disabled".Length);
                }
                else
                {
                    target = mod.FilePath + ".disabled";
                }

                if (File.Exists(target)) { error = "目标文件已存在：" + Path.GetFileName(target); return false; }
                File.Move(mod.FilePath, target);
                mod.FilePath = target;
                mod.FileName = Path.GetFileName(target);
                mod.Disabled = !enabled;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static bool Delete(ModInfo mod, out string error)
        {
            error = null;
            try
            {
                if (File.Exists(mod.FilePath)) File.Delete(mod.FilePath);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// 从任意直链安装一个 mod（支持的典型场景：mcmod 过验证码后拿到的 key 直链、
        /// 网盘直链、Modrinth CDN 直链……）。
        ///
        /// 文件名处理：优先 Content-Disposition，其次 URL 末段；都没有就下成临时文件，
        /// 读 jar 元数据后用「id-version.jar」重命名。
        /// </summary>
        public static bool InstallFromUrl(string baseDir, string verName, string url, out string message)
        {
            message = "";
            if (string.IsNullOrEmpty(url) || !(url.StartsWith("http://") || url.StartsWith("https://")))
            {
                message = "链接不合法（需要 http/https 直链）";
                return false;
            }
            if (string.IsNullOrEmpty(verName))
            {
                message = "没有指定实例版本";
                return false;
            }

            string modsDir = Path.Combine(baseDir, "versions", verName, "mods");
            string filename = null;
            try
            {
                Directory.CreateDirectory(modsDir);

                // 先探一下响应头，拿文件名
                filename = GuessFileName(url);

                string dest;
                string tmp;
                if (!string.IsNullOrEmpty(filename))
                {
                    dest = Path.Combine(modsDir, filename);
                    tmp = dest;
                }
                else
                {
                    tmp = Path.Combine(modsDir, ".download-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".jar");
                    dest = tmp;
                }

                string info;
                string status = Http.Download(url, tmp, null, out info, true);
                if (status == "fail" && !string.IsNullOrEmpty(filename))
                {
                    // 镜像候选对未知域名无效，这里直接报错
                    message = "下载失败：" + info;
                    return false;
                }
                if (!File.Exists(tmp)) { message = "下载失败：没有生成文件"; return false; }

                // 校验是不是真的 jar/zip
                byte[] head = new byte[4];
                using (var fs = File.OpenRead(tmp))
                {
                    if (fs.Read(head, 0, 4) < 4) { try { File.Delete(tmp); } catch { } message = "文件太小，不像 jar"; return false; }
                }
                if (head[0] != 0x50 || head[1] != 0x4B)
                {
                    try { File.Delete(tmp); } catch { }
                    message = "下到的不是 jar（可能是网页/验证页），请确认链接是直接下载地址";
                    return false;
                }

                // 没有文件名就按元数据重命名
                if (string.IsNullOrEmpty(filename))
                {
                    var probe = new ModInfo { FilePath = tmp, FileName = Path.GetFileName(tmp), Size = new FileInfo(tmp).Length };
                    try { FillFromJarPublic(probe, tmp); } catch { }
                    string nice = (!string.IsNullOrEmpty(probe.Name) && probe.Name != "-")
                        ? probe.Name.Replace(" ", "-").ToLowerInvariant()
                        : "mod-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                    string ver = string.IsNullOrEmpty(probe.Version) || probe.Version == "-" ? "" : "-" + probe.Version;
                    string target = Path.Combine(modsDir, nice + ver + ".jar");
                    int n = 1;
                    while (File.Exists(target)) target = Path.Combine(modsDir, nice + ver + "-" + (n++) + ".jar");
                    File.Move(tmp, target);
                    message = "已安装 " + Path.GetFileName(target) + "（" + probe.Name + " " + probe.Version + "）";
                    return true;
                }

                message = "已安装 " + filename + "（" + (new FileInfo(dest).Length / 1024) + " KB）";
                return true;
            }
            catch (Exception ex)
            {
                message = "安装失败：" + ex.Message;
                return false;
            }
        }

        /// <summary>从响应头 / URL 猜文件名，猜不到返回 null。</summary>
        private static string GuessFileName(string url)
        {
            try
            {
                var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
                req.Method = "GET";
                req.UserAgent = Http.UA;
                req.Timeout = 20000;
                req.AddRange(0, 0);
                using (var resp = (System.Net.HttpWebResponse)req.GetResponse())
                {
                    string cd = resp.Headers["Content-Disposition"];
                    if (!string.IsNullOrEmpty(cd))
                    {
                        var m = System.Text.RegularExpressions.Regex.Match(cd, @"filename\*?=(?:UTF-8'')?""?([^"";]+)""?",
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        if (m.Success) return Uri.UnescapeDataString(m.Groups[1].Value);
                    }
                }
            }
            catch { }

            try
            {
                var uri = new Uri(url);
                string last = uri.AbsolutePath.Split('/').LastOrDefault();
                if (!string.IsNullOrEmpty(last) && last.IndexOf('.') > 0 &&
                    (last.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
                     last.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
                    return Uri.UnescapeDataString(last);
            }
            catch { }
            return null;
        }

        private static void FillFromJarPublic(ModInfo info, string jarPath)
        {
            FillFromJar(info, jarPath);
        }

        /// <summary>命令行：mcroute.exe mods &lt;版本&gt;</summary>
        public static int Command(string baseDir, string ver)
        {
            if (string.IsNullOrEmpty(ver))
            {
                Console.WriteLine("用法: mcroute.exe mods <版本>");
                return 1;
            }
            string dir = ModsDir(baseDir, ver);
            Console.WriteLine("mods 目录：" + dir);
            if (!Directory.Exists(dir))
            {
                Console.WriteLine("（这个版本没有 mods 文件夹）");
                return 0;
            }

            var list = Scan(baseDir, ver);
            Console.WriteLine("共 " + list.Count + " 个文件（启用 " + list.Count(x => !x.Disabled) + " / 禁用 " + list.Count(x => x.Disabled) + "）");
            Console.WriteLine();
            foreach (var m in list)
            {
                Console.WriteLine(string.Format("  [{0}] {1,-28} {2,-12} {3,-9} {4}",
                    m.Disabled ? "禁" : "启", Trim(m.Name, 28), Trim(m.Version, 12), Trim(m.Loader, 9), m.FileName));
            }
            return 0;
        }

        private static string Trim(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= max ? s : s.Substring(0, max - 1) + "…";
        }
    }
}
