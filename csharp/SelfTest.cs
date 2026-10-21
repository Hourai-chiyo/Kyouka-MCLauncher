using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Mcroute
{
    /// <summary>
    /// 自测命令（不联网 / 联网各一组），给 CI 和人工回归用。
    ///   mcroute.exe --selftest-security            防 Zip Slip 测试
    ///   mcroute.exe --selftest-download &lt;url&gt; [sha1]  流式下载 + 校验测试
    /// </summary>
    public static class SelfTest
    {
        public static int Security()
        {
            // 测试目录放当前目录下：受限环境里子进程可能写不了 %TEMP%
            string tmp = Path.Combine(Directory.GetCurrentDirectory(),
                                      ".selftest-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmp);
            int pass = 0, fail = 0;

            try
            {
                // ---- 用例 1：恶意 zip（条目越界到目标目录外面）----
                string badZip = Path.Combine(tmp, "bad.zip");
                using (var fs = File.Create(badZip))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
                {
                    WriteEntry(zip, "../evil.txt", "pwned-1");
                    WriteEntry(zip, "..\\..\\evil2.txt", "pwned-2");
                    WriteEntry(zip, "config/ok.txt", "legit");
                }

                string outDir = Path.Combine(tmp, "out");
                Directory.CreateDirectory(outDir);

                bool rejected = false;
                string rejectReason = "";
                try
                {
                    using (var zip = ZipFile.OpenRead(badZip))
                        SafeZip.ExtractAll(zip, outDir);
                }
                catch (InvalidDataException ex) { rejected = true; rejectReason = ex.Message; }
                catch (Exception ex) { rejectReason = ex.GetType().Name + ": " + ex.Message; }

                bool escaped = File.Exists(Path.Combine(tmp, "evil.txt"))
                            || File.Exists(Path.Combine(Path.GetDirectoryName(tmp), "evil2.txt"))
                            || File.Exists(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(tmp)), "evil2.txt"));

                if (rejected && !escaped)
                {
                    Console.WriteLine("  [PASS] 恶意 zip 被拒绝：" + rejectReason);
                    pass++;
                }
                else
                {
                    Console.WriteLine("  [FAIL] 恶意 zip 未拦住：rejected=" + rejected + " escaped=" + escaped +
                                      " " + rejectReason);
                    fail++;
                }

                // ---- 用例 2：正常 zip 必须能正常解压 ----
                string goodZip = Path.Combine(tmp, "good.zip");
                using (var fs = File.Create(goodZip))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
                {
                    WriteEntry(zip, "config/a.txt", "hello");
                    WriteEntry(zip, "mods/b.txt", "world");
                }

                string goodOut = Path.Combine(tmp, "good-out");
                Directory.CreateDirectory(goodOut);
                using (var zip = ZipFile.OpenRead(goodZip))
                    SafeZip.ExtractAll(zip, goodOut);

                bool okA = File.Exists(Path.Combine(goodOut, "config", "a.txt"));
                bool okB = File.Exists(Path.Combine(goodOut, "mods", "b.txt"));
                if (okA && okB)
                {
                    Console.WriteLine("  [PASS] 正常 zip 解压正确（config/a.txt、mods/b.txt）");
                    pass++;
                }
                else
                {
                    Console.WriteLine("  [FAIL] 正常 zip 解压失败：config/a=" + okA + " mods/b=" + okB);
                    fail++;
                }

                // ---- 用例 3：IsInside 判定 ----
                bool inside = SafeZip.IsInside(goodOut, Path.Combine(goodOut, "config", "a.txt"));
                bool outside = !SafeZip.IsInside(goodOut, Path.Combine(tmp, "evil.txt"));
                if (inside && outside)
                {
                    Console.WriteLine("  [PASS] IsInside 判定正确");
                    pass++;
                }
                else
                {
                    Console.WriteLine("  [FAIL] IsInside 判定错误：inside=" + inside + " outside=" + outside);
                    fail++;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("  [ERROR] 自测异常：" + ex);
                fail++;
            }
            finally
            {
                try { Directory.Delete(tmp, true); } catch { }
            }

            Console.WriteLine();
            Console.WriteLine(string.Format("结果：{0} 通过 / {1} 失败", pass, fail));
            return fail == 0 ? 0 : 1;
        }

        public static int DownloadTest(string url, string expectedSha1)
        {
            if (string.IsNullOrEmpty(url))
            {
                Console.WriteLine("用法: mcroute.exe --selftest-download <url> [sha1]");
                return 1;
            }

            string tmp = Path.Combine(Directory.GetCurrentDirectory(),
                                      ".dltest-" + Guid.NewGuid().ToString("N").Substring(0, 8) + Path.GetExtension(url));
            Console.WriteLine("  下载: " + url);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string reason;
            bool ok = Http.StreamToFile(url, tmp, expectedSha1, out reason);
            sw.Stop();

            if (!ok)
            {
                Console.WriteLine("  [FAIL] " + reason);
                try { File.Delete(tmp); } catch { }
                return 1;
            }

            long size = new FileInfo(tmp).Length;
            Console.WriteLine("  [PASS] 流式下载成功：" + size + " 字节，耗时 " +
                              sw.Elapsed.TotalSeconds.ToString("F2") + " 秒");

            if (!string.IsNullOrEmpty(expectedSha1))
            {
                string actual = Http.Sha1File(tmp);
                bool same = string.Equals(actual, expectedSha1, StringComparison.OrdinalIgnoreCase);
                Console.WriteLine("  [INFO] sha1 期望 " + expectedSha1.Substring(0, 12) + "…  实际 " + actual.Substring(0, 12) + "…  " +
                                  (same ? "一致" : "不一致"));
                if (!same) { try { File.Delete(tmp); } catch { } return 1; }
            }

            try { File.Delete(tmp); } catch { }
            return 0;
        }

        /// <summary>调试用：打印一个 JSON 配置文件的解析结果。</summary>
        public static int ConfigDump(string path)
        {
            try
            {
                object o = J.ParseFile(path);
                var d = J.Obj(o);
                Console.WriteLine("  解析成功，顶层键数 " + (d == null ? 0 : d.Count));
                if (d != null)
                    foreach (var kv in d)
                        Console.WriteLine("    " + kv.Key + " = " + (kv.Value ?? "<null>") +
                                          "  (" + (kv.Value == null ? "null" : kv.Value.GetType().Name) + ")");
                Console.WriteLine("  J.Int glass = " + J.Int(o, "glass", -1));
                Console.WriteLine("  J.Int bg_dim = " + J.Int(o, "bg_dim", -1));
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("  [FAIL] " + ex.GetType().Name + ": " + ex.Message);
                return 1;
            }
        }

        private static void WriteEntry(ZipArchive zip, string name, string content)
        {
            var entry = zip.CreateEntry(name);
            using (var w = new StreamWriter(entry.Open(), new UTF8Encoding(false)))
                w.Write(content);
        }
    }
}
