using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace Mcroute
{
    /// <summary>下载源路由：URL 改写 + 大学镜像 + 跨官方 maven 兜底。</summary>
    public static class Meta
    {
        // NeoForge 官方 maven 在国内被 DNS 污染，官方 CDN 路径完全一致
        // CurseForge media 域名会 403，edge 域名同一路径正常
        private static readonly List<Tuple<string, string>> Rewrites = new List<Tuple<string, string>>
        {
            Tuple.Create("https://maven.neoforged.net/", "https://neoforged.forgecdn.net/"),
            Tuple.Create("https://media.forgecdn.net/", "https://edge.forgecdn.net/"),
        };

        // 哈工大的 BMCLAPI 镜像（标准 maven 布局，实测 7~8 MB/s）
        public const string HitMirror = "https://mirrors.hit.edu.cn/bmclapi/";

        private static readonly string[] MirrorPrefixes =
        {
            "https://maven.minecraftforge.net/",
            "https://maven.neoforged.net/releases/",
            "https://maven.fabricmc.net/",
            "https://maven.quiltmc.org/",
        };

        private static readonly Dictionary<string, string> CrossMaven = new Dictionary<string, string>
        {
            { "https://libraries.minecraft.net/", "https://maven.minecraftforge.net/" },
            { "https://maven.minecraftforge.net/", "https://libraries.minecraft.net/" },
        };

        public static string Rewrite(string url)
        {
            foreach (var pair in Rewrites)
                if (url.StartsWith(pair.Item1, StringComparison.Ordinal))
                    return pair.Item2 + url.Substring(pair.Item1.Length);
            return url;
        }

        /// <summary>下载候选：镜像优先 -> 改写后的官方 -> 跨 maven 兜底。</summary>
        public static List<string> Candidates(string url)
        {
            var list = new List<string>();
            foreach (string prefix in MirrorPrefixes)
            {
                if (url.StartsWith(prefix, StringComparison.Ordinal))
                {
                    list.Add(HitMirror + url.Substring(prefix.Length));
                    break;
                }
            }

            string rewritten = Rewrite(url);
            if (!list.Contains(rewritten)) list.Add(rewritten);

            foreach (var kv in CrossMaven)
            {
                if (url.StartsWith(kv.Key, StringComparison.Ordinal))
                {
                    string other = kv.Value + url.Substring(kv.Key.Length);
                    if (!list.Contains(other)) list.Add(other);
                    break;
                }
            }
            return list;
        }

        /// <summary>"group:artifact:version[:classifier]" -> maven 相对路径</summary>
        public static string MavenPathFromName(string name, string classifierOverride = null)
        {
            var parts = (name ?? "").Split(':');
            if (parts.Length < 3) return null;
            string group = parts[0], artifact = parts[1], version = parts[2];
            string classifier = classifierOverride ?? (parts.Length > 3 ? parts[3] : null);

            string file = artifact + "-" + version + (string.IsNullOrEmpty(classifier) ? "" : "-" + classifier) + ".jar";
            return group.Replace('.', '/') + "/" + artifact + "/" + version + "/" + file;
        }

        /// <summary>
        /// 一条库信息 -> (下载地址, libraries 下相对路径, sha1)。
        /// 支持两种写法：downloads.artifact（官方） / name+url（Fabric 风格）。
        /// </summary>
        public static LibFile ResolveLibrary(object lib)
        {
            var downloads = J.Dict(lib, "downloads");
            var artifact = downloads == null ? null : J.Dict(downloads, "artifact");

            if (artifact != null)
            {
                string path = J.Str(artifact, "path");
                string url = J.Str(artifact, "url");
                string sha1 = J.Str(artifact, "sha1");
                if (!string.IsNullOrEmpty(path))
                {
                    if (string.IsNullOrEmpty(url))
                    {
                        // 老版 Forge：path 在、url 空（文件由安装器放好）
                        url = "https://maven.minecraftforge.net/" + path.Replace('\\', '/');
                    }
                    return new LibFile { Url = url, RelPath = path, Sha1 = sha1 };
                }
            }

            string name = J.Str(lib, "name");
            string baseUrl = J.Str(lib, "url");
            if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(name)) return null;

            var parts = name.Split(':');
            string classifier = parts.Length > 3 ? parts[3] : null;
            string rel = MavenPathFromName(name, classifier);
            if (rel == null) return null;
            if (!baseUrl.EndsWith("/")) baseUrl += "/";
            return new LibFile { Url = baseUrl + rel, RelPath = rel, Sha1 = J.Str(lib, "sha1") };
        }

        public class LibFile
        {
            public string Url;
            public string RelPath;
            public string Sha1;
        }
    }
}
