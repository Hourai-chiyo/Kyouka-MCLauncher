using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace Mcroute
{
    /// <summary>一个镜像源。</summary>
    public class MirrorInfo
    {
        public string Name;          // 显示名
        public string Base;          // 基地址（末尾自动补 /）
        public bool Enabled = true;
        public double SpeedMbps;     // 最近一次测速（MB/s），0 表示没测过
        public bool LastOk;          // 最近一次探测是否可达

        public string Normalized()
        {
            string b = (Base ?? "").Trim();
            if (b.Length > 0 && !b.EndsWith("/")) b += "/";
            return b;
        }
    }

    /// <summary>
    /// 下载镜像管理：多镜像可配置 + 测速 + 按速度排序。
    ///
    /// 设计原则：
    ///   · 默认只带一个确认可用的镜像（哈工大），其余由用户自己加（"有备无患"）
    ///   · 镜像永远只是"优先尝试"，失败自动回落官方源
    ///   · 镜像文件必须通过 SHA-1 校验才算数（由下载器负责）
    /// </summary>
    public static class Mirrors
    {
        /// <summary>能被镜像覆盖的上游主机（BMCLAPI 风格镜像按"路径原样"映射）。</summary>
        private static readonly string[] MirroredHosts =
        {
            "https://maven.minecraftforge.net/",
            "https://maven.neoforged.net/",
            "https://libraries.minecraft.net/",
            "https://maven.fabricmc.net/",
            "https://maven.quiltmc.org/",
            "https://resources.download.minecraft.net/",
            "https://piston-meta.mojang.com/",
            "https://launchermeta.mojang.com/",
            "https://launcher.mojang.com/",
            "https://files.minecraftforge.net/",
            "https://meta.fabricmc.net/",
            "https://meta.quiltmc.org/",
        };

        /// <summary>可达性探测（HEAD，小文件，快）。</summary>
        private const string ProbePath = "net/minecraftforge/forge/maven-metadata.xml";

        /// <summary>测速用的路径：2.9 MB 的 jar，小文件测速会被连接开销带偏。</summary>
        private const string SpeedProbePath = "com/google/guava/guava/31.0.1-jre/guava-31.0.1-jre.jar";

        /// <summary>
        /// BMCLAPI 官方（bmclapi2.bangbang93.com）的路径规则与高校镜像不同：
        /// 它不是"原样拼后缀"，而是按上游主机改写前缀。2026-10 实测：
        ///   libraries/forge/neoforge/fabric/quilt maven → /maven/…
        ///   resources.download.minecraft.net          → /assets/…
        ///   meta.fabricmc.net                         → /fabric-meta/…
        ///   meta.quiltmc.org                          → /quilt-meta/…
        ///   piston-meta / launchermeta / launcher     → 原样（/mc/game/…）
        /// 返回值：拼接前缀；null 表示这条 URL 该镜像不支持（交回官方源）。
        /// </summary>
        private static string BmclapiPrefix(string host)
        {
            switch (host)
            {
                case "https://maven.minecraftforge.net/": return "/maven/";
                case "https://maven.neoforged.net/": return "/maven/";
                case "https://libraries.minecraft.net/": return "/maven/";
                case "https://maven.fabricmc.net/": return "/maven/";
                case "https://maven.quiltmc.org/": return "/maven/";
                case "https://files.minecraftforge.net/": return "/maven/";
                case "https://resources.download.minecraft.net/": return "/assets/";
                case "https://meta.fabricmc.net/": return "/fabric-meta/";
                case "https://meta.quiltmc.org/": return "/quilt-meta/";
                case "https://piston-meta.mojang.com/": return "/";
                case "https://launchermeta.mojang.com/": return "/";
                case "https://launcher.mojang.com/": return "/";
                default: return null;
            }
        }

        /// <summary>这个基地址是不是 BMCLAPI 官方（需要按主机改写前缀）。</summary>
        public static bool IsBmclapi(string baseUrl)
        {
            string b = (baseUrl ?? "").ToLowerInvariant();
            return b.Contains("bmclapi2.bangbang93.com") || b.Contains("bmclapi.bangbang93.com");
        }

        /// <summary>把一条上游 URL 映射到某个镜像；不支持返回 null。</summary>
        public static string MapUrl(string url, MirrorInfo mirror)
        {
            if (string.IsNullOrEmpty(url) || mirror == null) return null;

            foreach (string host in MirroredHosts)
            {
                if (!url.StartsWith(host, StringComparison.Ordinal)) continue;

                string suffix = url.Substring(host.Length);
                if (IsBmclapi(mirror.Base))
                {
                    string prefix = BmclapiPrefix(host);
                    if (prefix == null) return null;
                    return mirror.Normalized().TrimEnd('/') + prefix + suffix;
                }
                return mirror.Normalized() + suffix;
            }
            return null;
        }


        public static string ConfigPath(string baseDir)
        {
            return Path.Combine(baseDir ?? ".", "mirrors.json");
        }

        /// <summary>
        /// 默认镜像：2026-10-21 实测（2.9 MB 探针），只放真的能下的。
        /// 排序按实测速度，快的在前；启动时会再自动测速覆盖。
        /// </summary>
        public static List<MirrorInfo> Defaults()
        {
            return new List<MirrorInfo>
            {
                new MirrorInfo { Name = "HIT 哈工大",        Base = "https://mirrors.hit.edu.cn/bmclapi/",  Enabled = true,  SpeedMbps = 6.90 },
                new MirrorInfo { Name = "NYIST 南阳理工",    Base = "https://mirror.nyist.edu.cn/bmclapi/", Enabled = true,  SpeedMbps = 3.70 },
                new MirrorInfo { Name = "USTC 中科大",       Base = "https://mirrors.ustc.edu.cn/bmclapi/", Enabled = true,  SpeedMbps = 3.60 },
                new MirrorInfo { Name = "JCUT 荆楚理工",     Base = "https://mirrors.jcut.edu.cn/bmclapi/", Enabled = true,  SpeedMbps = 3.00 },
                new MirrorInfo { Name = "HA 河南大学",       Base = "https://mirrors.ha.edu.cn/bmclapi/",   Enabled = true,  SpeedMbps = 2.70 },
                new MirrorInfo { Name = "NJU 南京大学",      Base = "https://mirror.nju.edu.cn/bmclapi/",   Enabled = true,  SpeedMbps = 1.40 },
                new MirrorInfo { Name = "SJTU 上海交大",     Base = "https://mirror.sjtu.edu.cn/bmclapi/",  Enabled = true,  SpeedMbps = 0.90 },
                // 官方 BMCLAPI（第三方公益服务，非 Mojang 官方）：默认关闭，需要时在设置里勾选。
                // 路径规则与高校镜像不同，按上游主机改写前缀（见 BmclapiPrefix）。
                new MirrorInfo { Name = "BMCLAPI 官方（第三方·默认关）", Base = "https://bmclapi2.bangbang93.com/", Enabled = false, SpeedMbps = 0 },
                new MirrorInfo { Name = "QLU 齐鲁工大（慢）", Base = "https://mirrors.qlu.edu.cn/bmclapi/",  Enabled = false, SpeedMbps = 0.20 },
                new MirrorInfo { Name = "ZJU 浙江大学（慢）", Base = "https://mirrors.zju.edu.cn/bmclapi/",  Enabled = false, SpeedMbps = 0.05 },
            };
        }

        private static List<MirrorInfo> _cache;

        /// <summary>当前数据目录（由 Version.BaseDir / GUI 设置，决定读哪个 mirrors.json）。</summary>
        public static string ContextBaseDir;

        /// <summary>当前生效的镜像列表（按数据目录里的 mirrors.json，带缓存）。</summary>
        public static List<MirrorInfo> Current()
        {
            if (_cache != null) return _cache;
            try { _cache = Load(string.IsNullOrEmpty(ContextBaseDir) ? Version.BaseDir(null) : ContextBaseDir); }
            catch { _cache = Defaults(); }
            return _cache;
        }

        public static void Invalidate() { _cache = null; }

        private static double ParseSpeed(object raw)
        {
            if (raw == null) return 0;
            string text = raw as string;
            if (text == null) text = Convert.ToString(raw, System.Globalization.CultureInfo.InvariantCulture);
            double v;
            return double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out v) ? v : 0;
        }

        public static List<MirrorInfo> Load(string baseDir)
        {
            string path = ConfigPath(baseDir);
            try
            {
                if (File.Exists(path))
                {
                    var data = J.ParseFile(path);
                    var list = new List<MirrorInfo>();
                    foreach (var item in Version.AsList(data))
                    {
                        string name = J.Str(item, "name");
                        string @base = J.Str(item, "base");
                        if (string.IsNullOrEmpty(@base)) continue;
                        list.Add(new MirrorInfo
                        {
                            Name = string.IsNullOrEmpty(name) ? @base : name,
                            Base = @base,
                            Enabled = J.Bool(item, "enabled", true),
                            SpeedMbps = ParseSpeed(J.Get(item, "speed")),
                            LastOk = false,
                        });
                    }
                    if (list.Count > 0) return list;
                }
            }
            catch { }

            var defaults = Defaults();
            try { Save(baseDir, defaults); } catch { }
            return defaults;
        }

        public static void Save(string baseDir, List<MirrorInfo> mirrors)
        {
            try
            {
                var arr = mirrors.Select(m => (object)new Dictionary<string, object>
                {
                    { "name", m.Name ?? "" },
                    { "base", m.Base ?? "" },
                    { "enabled", m.Enabled },
                    { "speed", m.SpeedMbps.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) },
                }).ToArray();
                J.SaveFile(ConfigPath(baseDir), arr);
            }
            catch { }
        }

        /// <summary>这条 URL 能不能被镜像覆盖（是的话返回镜像后的候选列表）。</summary>
        public static List<string> MirrorCandidates(string url, List<MirrorInfo> mirrors)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(url) || mirrors == null) return result;

            // 按测速结果从快到慢
            foreach (var m in mirrors.Where(x => x.Enabled).OrderByDescending(x => x.SpeedMbps))
            {
                string candidate = MapUrl(url, m);
                if (!string.IsNullOrEmpty(candidate) && !result.Contains(candidate)) result.Add(candidate);
            }
            return result;
        }

        /// <summary>探测一个镜像是否可用（HEAD 那支元数据）。</summary>
        public static bool Probe(MirrorInfo mirror, int timeoutSeconds = 15)
        {
            string url = MapUrl("https://maven.minecraftforge.net/" + ProbePath, mirror)
                         ?? mirror.Normalized() + ProbePath;
            try
            {
                var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
                req.Method = "HEAD";
                req.Timeout = timeoutSeconds * 1000;
                req.UserAgent = Http.UA;
                using (var resp = (System.Net.HttpWebResponse)req.GetResponse())
                {
                    mirror.LastOk = (int)resp.StatusCode >= 200 && (int)resp.StatusCode < 400;
                    return mirror.LastOk;
                }
            }
            catch
            {
                mirror.LastOk = false;
                return false;
            }
        }

        /// <summary>测速：下载探针文件并计时，结果写回 mirror.SpeedMbps。</summary>
        public static double SpeedTest(MirrorInfo mirror, int timeoutSeconds = 30)
        {
            string url = MapUrl("https://libraries.minecraft.net/" + SpeedProbePath, mirror)
                         ?? mirror.Normalized() + SpeedProbePath;
            var sw = Stopwatch.StartNew();
            try
            {
                var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
                req.Timeout = timeoutSeconds * 1000;
                req.ReadWriteTimeout = timeoutSeconds * 1000;
                req.UserAgent = Http.UA;

                long bytes = 0;
                using (var resp = (System.Net.HttpWebResponse)req.GetResponse())
                using (var net = resp.GetResponseStream())
                {
                    byte[] buf = new byte[81920];
                    int n;
                    while ((n = net.Read(buf, 0, buf.Length)) > 0) bytes += n;
                }
                sw.Stop();
                mirror.LastOk = true;
                mirror.SpeedMbps = sw.Elapsed.TotalSeconds <= 0
                    ? 0
                    : Math.Round(bytes / 1024.0 / 1024.0 / sw.Elapsed.TotalSeconds, 2);
                return mirror.SpeedMbps;
            }
            catch
            {
                sw.Stop();
                mirror.LastOk = false;
                mirror.SpeedMbps = 0;
                return 0;
            }
        }

        /// <summary>命令行 sources：列出 + 测速 + 保存。</summary>
        public static int Command(string baseDir, bool measure = true)
        {
            var mirrors = Load(baseDir);
            Console.WriteLine("镜像配置文件：" + ConfigPath(baseDir));
            Console.WriteLine("共 " + mirrors.Count + " 个镜像（可在文件里手动加）");
            Console.WriteLine();

            if (measure)
            {
                foreach (var m in mirrors)
                {
                    if (!m.Enabled)
                    {
                        Console.WriteLine(string.Format("  {0,-20} 已禁用", m.Name));
                        continue;
                    }
                    Console.Write(string.Format("  {0,-20} 测速中… ", m.Name));
                    double mbps = SpeedTest(m);
                    Console.WriteLine(m.LastOk
                        ? string.Format("{0} MB/s", mbps.ToString("F2"))
                        : "不可用");
                }
                Save(baseDir, mirrors);
                Console.WriteLine();
            }

            Console.WriteLine("按速度排序（下载时会按这个顺序优先尝试）：");
            foreach (var m in mirrors.Where(x => x.Enabled).OrderByDescending(x => x.SpeedMbps))
                Console.WriteLine(string.Format("  {0,6} MB/s  {1}  {2}",
                    m.SpeedMbps.ToString("F2"), m.Name, m.Normalized()));

            Console.WriteLine();
            Console.WriteLine("提示：官方源永远在最后兜底；镜像文件都会过 SHA-1 校验。");
            return 0;
        }
    }
}
