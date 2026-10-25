using System;
using System.IO;

namespace Mcroute
{
    /// <summary>
    /// 路径边界：把"外部数据里的相对路径"解析成"确定在某个根目录内"的绝对路径。
    ///
    /// 为什么需要它：整合包清单、下载响应头、补丁 JSON 里的路径都来自外部，
    /// 直接 Path.Combine 会让 "..\..\x" 或绝对路径写到目录外（Zip Slip 的变种）。
    /// SafeZip 只管 zip 条目，这里管所有非 zip 的路径拼接。
    /// </summary>
    public static class SafePath
    {
        /// <summary>
        /// 解析相对于 rootDir 的路径；越界、绝对路径、盘符、UNC、ADS 一律抛 InvalidDataException。
        /// </summary>
        public static string ResolveInside(string rootDir, string relativePath, string what = "路径")
        {
            string full, error;
            if (!TryResolveInside(rootDir, relativePath, out full, out error))
                throw new InvalidDataException(what + " 不合法：" + error);
            return full;
        }

        /// <summary>同上，不抛异常。</summary>
        public static bool TryResolveInside(string rootDir, string relativePath, out string fullPath, out string error)
        {
            fullPath = null;
            error = null;

            if (string.IsNullOrWhiteSpace(rootDir)) { error = "根目录为空"; return false; }
            if (string.IsNullOrWhiteSpace(relativePath)) { error = "路径为空"; return false; }

            string rel = relativePath.Replace('\\', '/').Trim();
            if (rel.Length == 0) { error = "路径为空"; return false; }

            // 绝对路径 / 盘符 / UNC / 开头的斜杠，全部拒绝
            if (rel.StartsWith("/") || rel.StartsWith("\\\\")) { error = "不允许绝对路径"; return false; }
            if (rel.Length >= 2 && rel[1] == ':') { error = "不允许盘符路径"; return false; }
            if (rel.IndexOf(':') >= 0) { error = "路径含冒号（可能是 ADS 或盘符）"; return false; }

            string root;
            try { root = Path.GetFullPath(rootDir); }
            catch (Exception ex) { error = "根目录无效：" + ex.Message; return false; }
            if (!root.EndsWith(Path.DirectorySeparatorChar.ToString()))
                root += Path.DirectorySeparatorChar;

            string candidate = rel.Replace('/', Path.DirectorySeparatorChar);

            // 逐段拒绝 ".."（GetFullPath 之后也会再校验一次，双保险）
            foreach (string seg in candidate.Split(Path.DirectorySeparatorChar))
            {
                if (seg == "..") { error = "不允许 .. 段"; return false; }
            }

            try
            {
                // 以根目录为当前目录解析，避免 "C:foo" 这类相对盘符路径
                string combined = Path.Combine(root, candidate);
                fullPath = Path.GetFullPath(combined);
            }
            catch (Exception ex) { error = "路径无效：" + ex.Message; return false; }

            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                error = "解析后跑到根目录外了";
                return false;
            }
            if (fullPath.Length == root.Length) { error = "解析结果就是根目录本身"; return false; }

            return true;
        }

        /// <summary>
        /// 从 URL / Content-Disposition 里的原始文件名提取一个安全文件名（不含目录）。
        /// 解析不出来返回 null（调用方应改用生成名）。
        /// </summary>
        public static string FileNameOnly(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;

            string name = raw.Trim();

            // 去掉查询串/锚点
            int cut = name.IndexOfAny(new[] { '?', '#' });
            if (cut >= 0) name = name.Substring(0, cut);

            try { name = Uri.UnescapeDataString(name); } catch { }

            // 只取最后一段
            name = name.Replace('\\', '/');
            int slash = name.LastIndexOf('/');
            if (slash >= 0) name = name.Substring(slash + 1);

            try { name = Path.GetFileName(name); } catch { }

            // 去掉非法字符与结尾的点/空格（Windows 会静默截断，可能变成同名或空名）
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c.ToString(), "");
            name = name.Trim().TrimEnd('.', ' ');

            if (name.Length == 0) return null;
            if (name == "." || name == "..") return null;
            return name;
        }
    }
}
