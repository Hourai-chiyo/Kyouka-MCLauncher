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

        public static string ConfigPath(string baseDir)
        {
            return Path.Combine(baseDir ?? ".", "mirrors.json");
        }

        /// <summary>默认镜像：只放实测可用的，别塞一堆没验证过的。</summary>
        public static List<MirrorInfo> Defaults()
        {
            return new List<MirrorInfo>
            {
                new MirrorInfo { Name = "HIT 哈工大镜像", Base = "https://mirrors.hit.edu.cn/bmclapi/", Enabled = true },
            };
        }

        private static List<MirrorInfo> _cache;

        /// <summary>当前生效的镜像列表（按数据目录里的 mirrors.json，带缓存）。</summary>
        public static List<MirrorInfo> Current()
        {
            if (_cache != null) return _cache;
            try { _cache = Load(Version.BaseDir(null)); }
            catch { _cache = Defaults(); }
            return _cache;
        }

        public static void Invalidate() { _cache = null; }

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
                            SpeedMbps = 0,
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

            string suffix = null;
            foreach (string host in MirroredHosts)
            {
                if (url.StartsWith(host, StringComparison.Ordinal))
                {
                    suffix = url.Substring(host.Length);
                    break;
                }
            }
            if (suffix == null) return result;

            // 按测速结果从快到慢
            foreach (var m in mirrors.Where(x => x.Enabled).OrderByDescending(x => x.SpeedMbps))
            {
                string candidate = m.Normalized() + suffix;
                if (!result.Contains(candidate)) result.Add(candidate);
            }
            return result;
        }

        /// <summary>探测一个镜像是否可用（HEAD 那支元数据）。</summary>
        public static bool Probe(MirrorInfo mirror, int timeoutSeconds = 15)
        {
            string url = mirror.Normalized() + ProbePath;
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
            string url = mirror.Normalized() + SpeedProbePath;
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
