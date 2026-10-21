using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace Mcroute
{
    /// <summary>
    /// 自动更新：查 GitHub Release → 下载新 exe → 退出时用批处理替换再启动。
    ///
    /// 为什么用批处理：运行中的 exe 被系统占用，不能自己覆盖自己。
    /// 批处理先等本进程退出，再 move 新文件覆盖，然后重新启动。
    /// </summary>
    public static class Update
    {
        public const string Repo = "Hourai-chiyo/Kyouka-MCLauncher";
        /// <summary>发布资产里用来下载的文件名。</summary>
        public const string AssetName = "KyoukaMCLauncher.exe";

        public static string CurrentVersion()
        {
            try
            {
                var asm = typeof(Update).Assembly.GetName().Version;
                return asm.Major + "." + asm.Minor + "." + Math.Max(0, asm.Build);
            }
            catch { return "0.0.0"; }
        }

        /// <summary>把 "v1.2.3" / "1.2.3" 解析成整数数组，失败返回 null。</summary>
        public static int[] ParseVersion(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return null;
            string s = tag.Trim().TrimStart('v', 'V');
            var parts = new System.Collections.Generic.List<int>();
            foreach (string p in s.Split('-')[0].Split('.'))
            {
                int n;
                if (!int.TryParse(p, out n)) return null;
                parts.Add(n);
            }
            while (parts.Count < 3) parts.Add(0);
            return parts.ToArray();
        }

        public static bool IsNewer(string remote, string local)
        {
            int[] a = ParseVersion(remote), b = ParseVersion(local);
            if (a == null || b == null) return false;
            for (int i = 0; i < 3; i++)
            {
                if (a[i] > b[i]) return true;
                if (a[i] < b[i]) return false;
            }
            return false;
        }

        /// <summary>查最新版本。ok=false 时 error 是原因；否则 tag/downloadUrl 有值。</summary>
        public static bool CheckLatest(out string tag, out string downloadUrl, out string error)
        {
            tag = null; downloadUrl = null; error = null;
            try
            {
                string url = "https://api.github.com/repos/" + Repo + "/releases/latest";
                var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
                req.UserAgent = "KyoukaMCLauncher/" + CurrentVersion() + " (https://github.com/" + Repo + ")";
                req.Accept = "application/vnd.github+json";
                req.Timeout = 15000;
                req.ReadWriteTimeout = 15000;

                string text;
                using (var resp = (System.Net.HttpWebResponse)req.GetResponse())
                using (var stream = resp.GetResponseStream())
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                    text = reader.ReadToEnd();

                object json = J.Parse(text);
                tag = J.Str(json, "tag_name");
                foreach (object a in Version.AsList(J.Get(json, "assets")) ?? new System.Collections.Generic.List<object>())
                {
                    string name = J.Str(a, "name");
                    string dl = J.Str(a, "browser_download_url");
                    if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(dl)) continue;
                    // 优先用稳定名资产，其次任意以 KyoukaMCLauncher 开头的 exe
                    if (name.Equals(AssetName, StringComparison.OrdinalIgnoreCase)) { downloadUrl = dl; break; }
                    if (downloadUrl == null && name.StartsWith("KyoukaMCLauncher", StringComparison.OrdinalIgnoreCase)
                        && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        downloadUrl = dl;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>下载更新到 &lt;exe 目录&gt;\KyoukaMCLauncher.exe.new。成功返回新文件路径。</summary>
        public static string Download(string downloadUrl, out string error)
        {
            error = null;
            try
            {
                string dir = ExeDir();
                string target = Path.Combine(dir, AssetName + ".new");
                string info;
                string status = Http.Download(downloadUrl, target, null, out info, false);
                if (status == "fail" || !File.Exists(target))
                {
                    error = "下载失败：" + info;
                    return null;
                }
                // 粗校验：至少是个 PE 文件（MZ 头）
                byte[] head = new byte[2];
                using (var fs = File.OpenRead(target)) { if (fs.Read(head, 0, 2) < 2) { } }
                if (head[0] != 0x4D || head[1] != 0x5A)
                {
                    try { File.Delete(target); } catch { }
                    error = "下载到的不是 exe（可能被网络劫持）";
                    return null;
                }
                return target;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        /// <summary>
        /// 生成替换脚本并启动，然后调用方应当退出程序。
        /// 脚本：等 2 秒 → 备份旧 exe → 用新 exe 覆盖 → 启动 → 删除自己。
        /// </summary>
        public static bool SpawnUpdater(string newExePath, out string error)
        {
            error = null;
            try
            {
                string dir = ExeDir();
                string exe = Path.Combine(dir, AssetName);
                string old = exe + ".old";
                string bat = Path.Combine(dir, "_update.bat");

                var sb = new StringBuilder();
                sb.AppendLine("@echo off");
                sb.AppendLine("chcp 65001 >nul");
                sb.AppendLine("timeout /t 2 /nobreak >nul");
                sb.AppendLine("if exist \"" + old + "\" del /f /q \"" + old + "\" >nul 2>nul");
                sb.AppendLine("move /y \"" + exe + "\" \"" + old + "\" >nul 2>nul");
                sb.AppendLine("move /y \"" + newExePath + "\" \"" + exe + "\" >nul 2>nul");
                sb.AppendLine("start \"\" \"" + exe + "\"");
                sb.AppendLine("del /f /q \"%~f0\" >nul 2>nul");
                File.WriteAllText(bat, sb.ToString(), new UTF8Encoding(false));

                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c \"" + bat + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = dir,
                };
                Process.Start(psi);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static string ExeDir()
        {
            try { return Path.GetDirectoryName(System.Reflection.Assembly.GetEntryAssembly().Location); }
            catch { return AppDomain.CurrentDomain.BaseDirectory; }
        }

        /// <summary>启动时清理上次更新留下的 .old / .new。</summary>
        public static void CleanupLeftovers()
        {
            try
            {
                string dir = ExeDir();
                foreach (var pat in new[] { "*.old", "*.new" })
                    foreach (string f in Directory.GetFiles(dir, AssetName + pat))
                    {
                        try { File.Delete(f); } catch { }
                    }
            }
            catch { }
        }
    }
}
