using System;
using System.IO;
using System.IO.Compression;

namespace Mcroute
{
    /// <summary>
    /// 安全的 zip 解压：防 Zip Slip（恶意条目用 ..\..\ 把文件写到目标目录外面）。
    ///
    /// .NET Framework 4.8 的 ZipFile.ExtractToDirectory 不做这个检查，
    /// 所以整合包（mrpack / MultiMC / CurseForge）和 Java 包的解压全部走这里。
    /// </summary>
    public static class SafeZip
    {
        /// <summary>把条目解压到 rootDir 下的 relativePath；越界就抛异常。</summary>
        public static void ExtractRelative(ZipArchiveEntry entry, string rootDir, string relativePath)
        {
            string root = Path.GetFullPath(rootDir);
            if (!root.EndsWith(Path.DirectorySeparatorChar.ToString()))
                root += Path.DirectorySeparatorChar;

            string target = Path.GetFullPath(Path.Combine(
                root, (relativePath ?? "").Replace('/', Path.DirectorySeparatorChar)));

            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "压缩包里有越界路径（疑似 Zip Slip），已拒绝解压：" + entry.FullName);

            string dir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            if (string.IsNullOrEmpty(entry.Name)) return;   // 目录条目
            entry.ExtractToFile(target, true);
        }

        /// <summary>按条目自身路径解压到 rootDir。</summary>
        public static void ExtractEntry(ZipArchiveEntry entry, string rootDir)
        {
            ExtractRelative(entry, rootDir, entry.FullName);
        }

        /// <summary>整个压缩包解压到 destDir（替换 zip-slip 不安全的 ExtractToDirectory）。</summary>
        public static void ExtractAll(ZipArchive zip, string destDir)
        {
            foreach (var entry in zip.Entries)
                ExtractEntry(entry, destDir);
        }

        /// <summary>校验一个路径是否在 root 内（只做判断，不抛异常）。</summary>
        public static bool IsInside(string rootDir, string path)
        {
            string root = Path.GetFullPath(rootDir);
            if (!root.EndsWith(Path.DirectorySeparatorChar.ToString()))
                root += Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(path);
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
    }
}
