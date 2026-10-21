using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Mcroute
{
    /// <summary>
    /// 修复「别人的机器上打包」的 Cleanroom Relauncher 配置。
    ///
    /// 典型症状：整合包里 config/cleanroom_relauncher.cfg 的 Class Path 是
    ///   D:\新建文件夹\新建文件夹\.minecraft\versions\libraries\...
    /// 换台机器就全变成不存在的路径 → 二次启动时报
    ///   ClassNotFoundException: top.outlands.foundation.boot.Foundation
    ///
    /// 这里把这些条目换成当前机器上真实存在的路径（优先 <数据目录>/libraries），
    /// 找不到的条目丢掉；改之前自动备份。
    /// </summary>
    public static class CleanroomFix
    {
        private const string CfgRelative = @"config\cleanroom_relauncher.cfg";

        /// <summary>需要修复时返回说明文字；无配置/无需修复返回 null。</summary>
        public static string HealIfNeeded(string gameDir, string baseDir)
        {
            try
            {
                if (string.IsNullOrEmpty(gameDir)) return null;
                string cfg = Path.Combine(gameDir, CfgRelative);
                if (!File.Exists(cfg)) return null;

                string text = File.ReadAllText(cfg, Encoding.UTF8);
                var m = Regex.Match(text, "S:\"Class Path\"=([^\\r\\n]*)");
                if (!m.Success) return null;

                string[] entries = m.Groups[1].Value.Split(';')
                    .Select(x => x.Trim())
                    .Where(x => x.Length > 0)
                    .ToArray();
                if (entries.Length == 0) return null;

                // 已经全部存在 → 不需要动
                if (entries.All(File.Exists)) return null;

                var kept = new List<string>();
                int remapped = 0, dropped = 0;
                foreach (string p in entries)
                {
                    if (File.Exists(p)) { kept.Add(p); continue; }

                    // 尝试 1：路径里含 \libraries\ → 换成本机 baseDir\libraries\...
                    string alt = TryRemapToBase(p, baseDir);
                    if (alt != null && File.Exists(alt)) { kept.Add(alt); remapped++; continue; }

                    // 尝试 2：按文件名在 baseDir\libraries 下搜（只扫一层子目录，避免慢）
                    alt = TryRemapByFileName(p, baseDir);
                    if (alt != null) { kept.Add(alt); remapped++; continue; }

                    dropped++;
                }

                if (kept.Count == 0) return null;   // 全丢等于没修，保持原样

                string backup = cfg + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                try { File.Copy(cfg, backup, true); } catch { }

                string newLine = "S:\"Class Path\"=" + string.Join(";", kept);
                string patched = text.Substring(0, m.Index) + newLine + text.Substring(m.Index + m.Length);

                // Java Path 失效时清空，让 relauncher 用默认
                var jm = Regex.Match(patched, "S:\"Java Path\"=([^\\r\\n]*)");
                if (jm.Success && !File.Exists(jm.Groups[1].Value.Trim()))
                {
                    patched = patched.Substring(0, jm.Index) + "S:\"Java Path\"=" +
                              patched.Substring(jm.Index + jm.Length);
                }

                File.WriteAllText(cfg, patched, Encoding.UTF8);
                return string.Format("已修复 Cleanroom Relauncher 配置：{0} 条路径改为本机位置，丢弃 {1} 条失效项（备份 {2}）",
                    remapped, dropped, Path.GetFileName(backup));
            }
            catch
            {
                return null;
            }
        }

        private static string TryRemapToBase(string path, string baseDir)
        {
            int idx = path.IndexOf(@"\libraries\", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            string tail = path.Substring(idx + @"\libraries\".Length);
            return Path.Combine(baseDir, "libraries", tail);
        }

        /// <summary>按「group/artifact/version/file.jar」的相对结构，尝试在 baseDir\libraries 里找同名文件。</summary>
        private static string TryRemapByFileName(string path, string baseDir)
        {
            try
            {
                string libRoot = Path.Combine(baseDir, "libraries");
                if (!Directory.Exists(libRoot)) return null;

                string fileName = Path.GetFileName(path);
                if (string.IsNullOrEmpty(fileName)) return null;

                // 取路径里 group 目录段（例如 com\mojang\authlib\1.5.25\authlib-1.5.25.jar）
                var parts = path.Replace('/', '\\').Split('\\');
                if (parts.Length < 3) return null;

                string version = parts[parts.Length - 2];
                string artifact = parts[parts.Length - 3];
                string group = string.Join("\\", parts.Skip(Math.Max(0, parts.Length - 3 - 3)).Take(3));

                string guess = Path.Combine(libRoot, group, artifact, version, fileName);
                if (File.Exists(guess)) return guess;

                // 最后兜底：按 artifact 目录名扫
                foreach (string art in Directory.GetDirectories(libRoot, artifact, SearchOption.AllDirectories))
                {
                    string direct = Path.Combine(art, fileName);
                    if (File.Exists(direct)) return direct;
                    foreach (string sub in Directory.GetDirectories(art))
                    {
                        string f = Path.Combine(sub, fileName);
                        if (File.Exists(f)) return f;
                    }
                }
            }
            catch { }
            return null;
        }
    }
}
