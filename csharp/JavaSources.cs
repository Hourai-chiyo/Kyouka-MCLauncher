using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Mcroute
{
    /// <summary>一个可下载的 Java 包。</summary>
    public class JavaPackage
    {
        public string Vendor;      // Adoptium / Azul / Corretto
        public string Display;     // 显示用，如 "Temurin 17.0.13+11"
        public string Url;
        public string FileName;
        public string Sha256;      // 可能为空
    }

    /// <summary>
    /// Java 运行时下载源。优先级按用户指定：
    ///   1. Adoptium Temurin（首选，镜像：清华 TUNA → 官方 GitHub）
    ///   2. Azul Zulu（二号）
    ///   3. AWS Corretto（最后备选）
    ///   Oracle 不选。
    /// </summary>
    public static class JavaSources
    {
        public const string TunaAdoptium = "https://mirrors.tuna.tsinghua.edu.cn/Adoptium/";

        /// <summary>按优先级找可用包；全失败返回 null。</summary>
        public static JavaPackage Resolve(int major)
        {
            JavaPackage pkg = TryAdoptium(major);
            if (pkg != null) return pkg;

            Console.WriteLine("      Adoptium 取不到，改用 Azul Zulu ...");
            pkg = TryAzul(major);
            if (pkg != null) return pkg;

            Console.WriteLine("      Azul 也取不到，退回 AWS Corretto ...");
            return TryCorretto(major);
        }

        // ---------------- 1) Adoptium Temurin ----------------
        private static JavaPackage TryAdoptium(int major)
        {
            string api = "https://api.adoptium.net/v3/assets/latest/" + major +
                         "/hotspot?os=windows&architecture=x64&image_type=jdk&vendor=eclipse";
            object data = Http.GetJson(api, 40);
            var arr = Version.AsList(data);
            if (arr.Count == 0) return null;

            var item = J.Obj(arr[0]);
            var binary = J.Dict(item, "binary");
            var package = binary == null ? null : J.Dict(binary, "package");
            if (package == null) return null;

            string name = J.Str(package, "name");
            string link = J.Str(package, "link");
            string checksum = J.Str(package, "checksum");
            string releaseName = J.Str(item, "release_name", major + "u");

            if (string.IsNullOrEmpty(name)) return null;

            // 清华镜像路径：Adoptium/<major>/jdk/x64/windows/<文件名>
            var pkg = new JavaPackage
            {
                Vendor = "Adoptium",
                Display = "Temurin " + releaseName.Replace("jdk-", "").Replace("-", " "),
                FileName = name,
                Sha256 = checksum,
            };

            // 先试清华镜像（国内快），再试官方 GitHub 链接
            foreach (string baseUrl in new[] { TunaAdoptium + major + "/jdk/x64/windows/", "" })
            {
                if (baseUrl == "" && string.IsNullOrEmpty(link)) continue;
                string url = baseUrl == "" ? link : baseUrl + name;
                if (HeadOk(url)) { pkg.Url = url; return pkg; }
            }
            return null;
        }

        // ---------------- 2) Azul Zulu ----------------
        private static JavaPackage TryAzul(int major)
        {
            string api = "https://api.azul.com/metadata/v1/zulu/packages/?java_version=" + major +
                         "&os=windows&arch=x64&archive_type=zip&java_package_type=jdk" +
                         "&latest=true&release_status=ga&availability_types=CA&certifications=tck";
            object data = Http.GetJson(api, 40);
            var arr = Version.AsList(data);
            if (arr.Count == 0) return null;

            var item = J.Obj(arr[0]);
            string url = J.Str(item, "download_url");
            string name = J.Str(item, "name", "zulu" + major + ".zip");
            if (string.IsNullOrEmpty(url)) return null;

            return new JavaPackage
            {
                Vendor = "Azul",
                Display = "Zulu " + J.Str(item, "distro_version", major.ToString()),
                Url = url,
                FileName = name.EndsWith(".zip") ? name : name + ".zip",
            };
        }

        // ---------------- 3) AWS Corretto ----------------
        private static JavaPackage TryCorretto(int major)
        {
            string url = "https://corretto.aws/downloads/latest/amazon-corretto-" + major +
                         "-x64-windows-jdk.zip";
            if (!HeadOk(url)) return null;
            return new JavaPackage
            {
                Vendor = "Corretto",
                Display = "Amazon Corretto " + major,
                Url = url,
                FileName = "amazon-corretto-" + major + "-x64-windows-jdk.zip",
            };
        }

        /// <summary>HEAD 探测（失败当不可用）。</summary>
        private static bool HeadOk(string url)
        {
            try
            {
                var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
                req.Method = "HEAD";
                req.Timeout = 15000;
                req.UserAgent = Http.UA;
                using (var resp = (System.Net.HttpWebResponse)req.GetResponse())
                    return (int)resp.StatusCode >= 200 && (int)resp.StatusCode < 400;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>下载并解压一个 Java 包，返回 java.exe 的完整路径。</summary>
        public static string Install(int major, string baseDir)
        {
            var pkg = Resolve(major);
            if (pkg == null)
            {
                Console.WriteLine("[错误] 三个源都拿不到 Java " + major);
                return null;
            }
            Console.WriteLine("  Java 来源: " + pkg.Vendor + "  (" + pkg.Display + ")");
            Console.WriteLine("  下载地址: " + pkg.Url);

            string javaRoot = Path.Combine(baseDir, "java_versions");
            Directory.CreateDirectory(javaRoot);
            string zipPath = Path.Combine(javaRoot, pkg.FileName);

            if (!File.Exists(zipPath))
            {
                try
                {
                    byte[] data = Http.GetBytes(pkg.Url, 1800);
                    File.WriteAllBytes(zipPath, data);
                    Console.WriteLine("  已下载 " + (data.Length / 1024 / 1024) + " MB");
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[错误] 下载失败: " + ex.Message);
                    return null;
                }
            }
            else Console.WriteLine("  压缩包已存在，跳过下载");

            // 解压到 <javaRoot>/<vendor>-<major>
            string target = Path.Combine(javaRoot, pkg.Vendor.ToLowerInvariant() + "-" + major);
            if (Directory.Exists(target))
            {
                string exist = FindJavaExe(target);
                if (exist != null)
                {
                    Console.WriteLine("  已安装过，直接用: " + exist);
                    return exist;
                }
            }
            else Directory.CreateDirectory(target);

            try
            {
                ZipFile.ExtractToDirectory(zipPath, target);
                Console.WriteLine("  已解压到 " + target);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[错误] 解压失败: " + ex.Message);
                return null;
            }

            string javaExe = FindJavaExe(target);
            if (javaExe == null)
            {
                Console.WriteLine("[错误] 解压后找不到 bin\\java.exe");
                return null;
            }

            int detected = Java.Major(javaExe);
            Console.WriteLine("  验证: " + javaExe);
            Console.WriteLine("        报告版本 Java " + detected);
            if (detected != major)
                Console.WriteLine("  [警告] 期望 Java " + major + "，实际识别为 " + detected);

            return javaExe;
        }

        /// <summary>在解压目录里找 bin\java.exe（zip 里可能多一层目录）。</summary>
        public static string FindJavaExe(string root)
        {
            string direct = Path.Combine(root, "bin", "java.exe");
            if (File.Exists(direct)) return direct;
            foreach (string sub in Directory.GetDirectories(root))
            {
                string nested = Path.Combine(sub, "bin", "java.exe");
                if (File.Exists(nested)) return nested;
            }
            return null;
        }

        /// <summary>列出本地已安装的 Java（用于 java list）。</summary>
        public static void ListLocal(string baseDir)
        {
            Console.WriteLine("本机检测到的 Java：");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var roots = new List<string>
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Eclipse Adoptium"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Java"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft", "runtime"),
                Path.Combine(baseDir, "java_versions"),
            };
            foreach (string root in roots)
            {
                if (!Directory.Exists(root)) continue;
                foreach (string dir in Directory.GetDirectories(root))
                {
                    string exe = FindJavaExe(dir) ?? Path.Combine(dir, "bin", "java.exe");
                    if (!File.Exists(exe)) exe = Path.Combine(dir, "jre", "bin", "java.exe");
                    if (!File.Exists(exe) || !seen.Add(exe)) continue;
                    int major = Java.Major(exe);
                    Console.WriteLine(string.Format("  Java {0,-3} {1}", major, exe));
                }
            }
        }
    }

/// <summary>java 命令：list / install &lt;major&gt;</summary>
public static class JavaCommand
{
    public static int Run(string[] a)
    {
        string baseDir = Version.BaseDir(Commands.Arg(a, "--dir"));
        string sub = a.Length > 1 ? a[1].ToLowerInvariant() : "list";

        if (sub == "list")
        {
            JavaSources.ListLocal(baseDir);
            Console.WriteLine();
            Console.WriteLine("安装更多 Java：mcroute java install <8|11|17|21>");
            return 0;
        }

        if (sub == "install")
        {
            string majorArg = a.Length > 2 ? a[2] : null;
            int major;
            if (majorArg == null || !int.TryParse(majorArg, out major))
            {
                Console.WriteLine("[错误] 用法: mcroute java install <8|11|17|21>");
                return 1;
            }
            Console.WriteLine("[安装] Java " + major + "（源优先级：Adoptium → Azul → Corretto）");
            string exe = JavaSources.Install(major, baseDir);
            if (exe == null) return 1;
            Console.WriteLine();
            Console.WriteLine("完成：" + exe);
            Console.WriteLine("启动时会自动识别 java_versions 目录里的 Java。");
            return 0;
        }

        Console.WriteLine("[错误] 未知子命令: " + sub + "（可用: list / install）");
        return 1;
    }
}
}
