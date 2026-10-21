using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Mcroute
{
    /// <summary>下载：带重试、镜像优先、sha1 校验。</summary>
    public static class Http
    {
        public const string UA = "mcroute/0.2 (C# port, learning project)";
        private static HttpClient _client;

        static Http()
        {
            // .NET Framework 默认可能用 TLS 1.0，必须显式打开 1.2
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            ServicePointManager.DefaultConnectionLimit = 32;
        }

        private static HttpClient Client
        {
            get
            {
                if (_client == null)
                {
                    var handler = new HttpClientHandler
                    {
                        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                    };
                    _client = new HttpClient(handler);
                    _client.Timeout = TimeSpan.FromSeconds(120);
                    _client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UA);
                }
                return _client;
            }
        }

        /// <summary>取一段文本（不重试，返回 null 表示失败）。</summary>
        public static string GetString(string url, int timeoutSeconds = 30)
        {
            try
            {
                var task = Client.GetStringAsync(url);
                if (!task.Wait(TimeSpan.FromSeconds(timeoutSeconds))) return null;
                return task.Result;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>取 JSON（解析失败返回 null）。</summary>
        public static object GetJson(string url, int timeoutSeconds = 30)
        {
            string text = GetString(url, timeoutSeconds);
            if (text == null) return null;
            try { return J.Parse(text); }
            catch { return null; }
        }

        public static byte[] GetBytes(string url, int timeoutSeconds = 90)
        {
            try
            {
                var task = Client.GetByteArrayAsync(url);
                if (!task.Wait(TimeSpan.FromSeconds(timeoutSeconds)))
                    throw new TimeoutException("timeout");
                return task.Result;
            }
            catch (Exception ex)
            {
                throw new IOException("下载失败: " + url + " (" + ex.Message + ")", ex);
            }
        }

        public static string Sha1File(string path)
        {
            using (var sha = SHA1.Create())
            using (var fs = File.OpenRead(path))
            {
                byte[] hash = sha.ComputeHash(fs);
                var sb = new StringBuilder();
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        public static string Sha1Bytes(byte[] data)
        {
            using (var sha = SHA1.Create())
            {
                byte[] hash = sha.ComputeHash(data);
                var sb = new StringBuilder();
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>
        /// 下载一个文件到 dest（先试镜像/改写地址，再试官方）。
        /// expectedSha1 为空时跳过校验。返回 ok / skip / fail。
        /// </summary>
        public static string Download(string url, string dest, string expectedSha1, out string info)
        {
            info = "";
            if (File.Exists(dest))
            {
                if (string.IsNullOrEmpty(expectedSha1) || Sha1File(dest) == expectedSha1)
                    return "skip";
            }

            var candidates = Meta.Candidates(url);
            string last = "所有地址都失败";
            foreach (string url2 in candidates)
            {
                try
                {
                    byte[] data = GetBytes(url2);
                    if (!string.IsNullOrEmpty(expectedSha1) && Sha1Bytes(data) != expectedSha1)
                    {
                        last = "sha1 不符: " + url2;
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    File.WriteAllBytes(dest, data);
                    return "ok";
                }
                catch (Exception ex)
                {
                    last = ex.Message;
                }
            }
            info = last;
            return "fail";
        }

        /// <summary>下载进度回调：格式化文本, 已完成数, 总数（GUI 用来画进度条）。</summary>
        public static Action<string, int, int> Progress;

        /// <summary>并发下载一批任务（url, dest, sha1）。</summary>
        public static void DownloadAll(IList<Tuple<string, string, string>> jobs, int workers,
                                       bool verbose = true)
        {
            int total = jobs.Count;
            if (total == 0)
            {
                Console.WriteLine("没有需要下载的文件");
                if (Progress != null) Progress("没有需要下载的文件", 0, 0);
                return;
            }

            int ok = 0, skip = 0, fail = 0, done = 0;
            long bytes = 0;
            var failures = new List<string>();
            var pad = new object();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var lastReport = DateTime.Now;

            var options = new ParallelOptions { MaxDegreeOfParallelism = workers };
            System.Threading.Tasks.Parallel.ForEach(jobs, options, job =>
            {
                string info;
                long before = File.Exists(job.Item2) ? new FileInfo(job.Item2).Length : 0;
                string status = Download(job.Item1, job.Item2, job.Item3, out info);
                long after = File.Exists(job.Item2) ? new FileInfo(job.Item2).Length : 0;
                lock (pad)
                {
                    done++;
                    if (status == "ok")
                    {
                        ok++;
                        bytes += Math.Max(0, after - before);
                    }
                    else if (status == "skip") skip++;
                    else
                    {
                        fail++;
                        if (failures.Count < 20) failures.Add(Path.GetFileName(job.Item2) + " -> " + info);
                    }

                    // 每 250ms 汇报一次，避免界面刷新太频繁
                    if ((DateTime.Now - lastReport).TotalMilliseconds > 250 || done == total)
                    {
                        lastReport = DateTime.Now;
                        double sec = Math.Max(0.001, sw.Elapsed.TotalSeconds);
                        double rate = bytes / 1024.0 / 1024.0 / sec;
                        string text = string.Format("{0}/{1}   新下载 {2}  已存在 {3}  失败 {4}   {5:F1} MB  {6:F1} MB/s",
                            done, total, ok, skip, fail, bytes / 1024.0 / 1024.0, rate);
                        if (verbose) Console.WriteLine("  " + text);
                        if (Progress != null) Progress(text, done, total);
                    }
                }
            });

            string summary = string.Format("完成：新下载 {0}，跳过 {1}，失败 {2}", ok, skip, fail);
            Console.WriteLine(summary);
            if (Progress != null) Progress(summary, total, total);
            foreach (string f in failures) Console.WriteLine("  [fail] " + f);
        }
    }
}
