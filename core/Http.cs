using System;
using System.Collections.Generic;
using System.Linq;
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
        /// <summary>UA 里的版本号从程序集版本读，避免每次发版忘改（之前就漏过一次）。</summary>
        private static string BuildUa()
        {
            string ver = "1.10.0";
            try
            {
                var v = typeof(Http).Assembly.GetName().Version;
                if (v != null) ver = v.Major + "." + v.Minor + "." + v.Build;
            }
            catch { }
            return "Kyouka-MCLauncher/" + ver + " (+https://github.com/" + Update.Repo + ")";
        }

        public static readonly string UA = BuildUa();
        private static HttpClient _client;
        private static readonly object _clientLock = new object();

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
                if (_client != null) return _client;
                lock (_clientLock)
                {
                    if (_client == null)
                    {
                        var handler = new HttpClientHandler
                        {
                            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                        };
                        var c = new HttpClient(handler);
                        c.Timeout = TimeSpan.FromSeconds(120);
                        c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UA);
                        _client = c;
                    }
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
                // 注意：所有调用都发生在后台线程；这里用 GetAwaiter 拿结果，
                // 避免 .Result 把超时/网络异常包成 AggregateException。
                if (!task.Wait(TimeSpan.FromSeconds(timeoutSeconds))) return null;
                return task.GetAwaiter().GetResult();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>POST 一段 JSON，返回响应文本（失败返回 null）。</summary>
        public static string PostJson(string url, string jsonBody, int timeoutSeconds = 30)
        {
            try
            {
                using (var content = new System.Net.Http.StringContent(
                    jsonBody ?? "", System.Text.Encoding.UTF8, "application/json"))
                {
                    var task = Client.PostAsync(url, content);
                    if (!task.Wait(TimeSpan.FromSeconds(timeoutSeconds))) return null;
                    var resp = task.GetAwaiter().GetResult();
                    if (!resp.IsSuccessStatusCode) return null;
                    var read = resp.Content.ReadAsStringAsync();
                    if (!read.Wait(TimeSpan.FromSeconds(timeoutSeconds))) return null;
                    return read.GetAwaiter().GetResult();
                }
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
                return task.GetAwaiter().GetResult();
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
        /// 流式下载到文件：边下边写边算 SHA1，校验通过才落到最终路径。
        ///
        /// 为什么不用 GetBytes：那会把整个文件读进内存（Java 安装包 100MB+、整合包上 GB），
        /// 大文件时会明显吃内存。这里固定 80KB 缓冲，内存占用与文件大小无关。
        /// </summary>
        // ================= 分段并行下载 =================
        /// <summary>低于这个大小不值得分段（连接开销占比太高）。</summary>
        public static long ParallelThreshold = 2 * 1024 * 1024;

        /// <summary>默认分几段。</summary>
        public static int ParallelSegments = 4;

        /// <summary>
        /// 全局开关。**默认关闭**：实测在本机网络下分段反而更慢（单连接 6.3 MB/s vs 4 段 1.44 MB/s），
        /// 瓶颈在链路总带宽而不是单连接。用 --parallel 或设置页开关可以打开。
        /// </summary>
        public static bool AllowParallel = false;
        /// <summary>自适应：采样多少字节来测单连接速度。</summary>
        public static long AdaptiveSampleBytes = 512 * 1024;

        /// <summary>自适应：单连接达到这个速度就不分段（MB/s）。</summary>
        public static double AdaptiveMinSpeedMbps = 1.0;

        /// <summary>自适应：小于这个大小直接单连接。</summary>
        public static long AdaptiveMinSize = 8 * 1024 * 1024;

        /// <summary>强制分段（调试用，环境变量 MCROUTE_PARALLEL_FORCE=1）。</summary>
        public static bool ForceParallel = Environment.GetEnvironmentVariable("MCROUTE_PARALLEL_FORCE") == "1";


        /// <summary>
        /// 分段并行下载：按字节区间切分，每段写独立临时文件，最后顺序合并并校验 SHA-1。
        ///
        /// 为什么每段写独立文件而不是直接写目标文件的不同偏移：
        ///   · 某段失败只需重试该段，不会整体重来
        ///   · 不依赖文件 seek 的并发安全
        ///   · 合并是一次纯本地顺序拷贝，几十 MB 也就几十毫秒
        ///
        /// 返回 false 时调用方应退回单连接下载（服务器不支持 Range / 文件太小 / 探测失败）。
        /// </summary>
        /// <summary>
        /// 测「单连接」在目标地址上的稳态速度。
        ///
        /// 注意坑：只测开头 512KB 会被 TLS 握手 + TCP 慢启动带偏
        /// （实测采样 0.52 MB/s，同一时刻完整下载却是 4.7 MB/s）。
        /// 所以先读满 WARMUP 字节不计时，再计后面一段的稳定速度。
        /// </summary>
        public static double MeasureSingleSpeed(string url, out string info)
        {
            info = "";
            const long Warmup = 256 * 1024;          // 预热（握手 + 慢启动）
            long sample = AdaptiveSampleBytes;       // 计时区间
            if (sample < 1024 * 1024) sample = 1024 * 1024;

            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.UserAgent = UA;
                req.Timeout = 30000;
                req.ReadWriteTimeout = 30000;
                req.AddRange(0, Warmup + sample - 1);

                long total = 0, timed = 0;
                var sw = new System.Diagnostics.Stopwatch();
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var net = resp.GetResponseStream())
                {
                    byte[] buf = new byte[65536];
                    int n;
                    while (total < Warmup + sample && (n = net.Read(buf, 0, buf.Length)) > 0)
                    {
                        total += n;
                        if (total > Warmup)
                        {
                            if (!sw.IsRunning) sw.Start();
                            timed += (total - Warmup) < n ? (total - Warmup) : n;
                        }
                    }
                }
                sw.Stop();

                if (timed <= 0 || sw.Elapsed.TotalSeconds <= 0) { info = "采样为 0"; return 0; }
                double mbps = (timed / 1024.0 / 1024.0) / sw.Elapsed.TotalSeconds;
                info = string.Format("预热 {0:F0}KB 后测 {1:F0}KB / {2:F2}s = {3:F2} MB/s",
                                     Warmup / 1024.0, timed / 1024.0, sw.Elapsed.TotalSeconds, mbps);
                return mbps;
            }
            catch (Exception ex)
            {
                info = "采样失败: " + ex.Message;
                return 0;
            }
        }

        public static bool DownloadParallel(string url, string dest, string expectedSha1, out string reason)
        {
            reason = "";
            if (!AllowParallel) { reason = "已关闭分段下载"; return false; }

            long size = 0;
            string etag = null;
            try
            {
                var probe = (HttpWebRequest)WebRequest.Create(url);
                probe.UserAgent = UA;
                probe.Timeout = 30000;
                probe.AddRange(0, 0);
                using (var resp = (HttpWebResponse)probe.GetResponse())
                {
                    if (resp.StatusCode != HttpStatusCode.PartialContent)
                    {
                        reason = "服务器不支持 Range（" + (int)resp.StatusCode + "）";
                        return false;
                    }
                    string cr = resp.Headers["Content-Range"];      // bytes 0-0/12345678
                    if (string.IsNullOrEmpty(cr))
                    {
                        reason = "没有 Content-Range";
                        return false;
                    }
                    int slash = cr.LastIndexOf('/');
                    if (slash < 0 || !long.TryParse(cr.Substring(slash + 1).Trim(), out size) || size <= 0)
                    {
                        reason = "解析 Content-Range 失败: " + cr;
                        return false;
                    }
                    etag = resp.Headers["ETag"];
                }
            }
            catch (Exception ex)
            {
                reason = "Range 探测失败: " + ex.Message;
                return false;
            }

            if (size < ParallelThreshold)
            {
                reason = "文件只有 " + (size / 1024) + " KB，不划算";
                return false;
            }

            // ---- 自适应：先测单连接在「这个地址」上的真实速度 ----
            // 只对够大的文件做（小于 8MB 分段意义不大）；偏保守：测不出就单连接
            if (!ForceParallel && size >= AdaptiveMinSize)
            {
                string sampleInfo;
                double single = MeasureSingleSpeed(url, out sampleInfo);
                if (Environment.GetEnvironmentVariable("MCROUTE_SEG_DEBUG") == "1")
                    Console.WriteLine("    自适应采样: " + sampleInfo + "（阈值 " + AdaptiveMinSpeedMbps + " MB/s）");
                if (single >= AdaptiveMinSpeedMbps)
                {
                    reason = string.Format("自适应：单连接已有 {0:F2} MB/s（{1}），不分段", single, sampleInfo);
                    return false;
                }
                if (single <= 0)
                {
                    reason = "自适应：测速失败（" + sampleInfo + "），保守用单连接";
                    return false;
                }
                reason = "";   // 继续走分段
            }

            int seg;
            int overrideSeg = 0;
            int.TryParse(Environment.GetEnvironmentVariable("MCROUTE_SEGMENTS"), out overrideSeg);
            try { seg = overrideSeg > 0 ? overrideSeg : (int)Math.Min(ParallelSegments, Math.Max(1, size / (512 * 1024))); }
            catch { seg = 1; }
            if (seg <= 1) { reason = "算出来只需 1 段"; return false; }
            if (seg > 8) seg = 8;

            var parts = new string[seg];
            var from = new long[seg];
            var to = new long[seg];
            long chunk = size / seg;

            for (int i = 0; i < seg; i++)
            {
                from[i] = i * chunk;
                to[i] = (i == seg - 1) ? size - 1 : (i + 1) * chunk - 1;
                parts[i] = dest + ".p" + i;
            }

            string tmpDest = dest + ".merging";
            string dir = Path.GetDirectoryName(dest);
            try
            {
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var errors = new System.Collections.Concurrent.ConcurrentBag<string>();
                var options = new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = seg };

                System.Threading.Tasks.Parallel.ForEach(
                    System.Linq.Enumerable.Range(0, seg), options, i =>
                {
                    if (CancelRequested) return;
                    string why = null;
                    var swSeg = System.Diagnostics.Stopwatch.StartNew();
                    for (int attempt = 1; attempt <= 3; attempt++)
                    {
                        if (CancelRequested) return;
                        why = FetchRange(url, from[i], to[i], parts[i], etag);
                        if (why == null)
                        {
                            swSeg.Stop();
                            if (Environment.GetEnvironmentVariable("MCROUTE_SEG_DEBUG") == "1")
                                Console.WriteLine(string.Format("    [段{0}] {1:F2}s 第{2}次尝试 {3:F1} MB",
                                    i, swSeg.Elapsed.TotalSeconds, attempt, (to[i] - from[i]) / 1024.0 / 1024.0));
                            return;
                        }
                        System.Threading.Thread.Sleep(300 * attempt);
                    }
                    errors.Add("段" + i + ": " + why);
                });

                if (CancelRequested)
                {
                    Cleanup(parts);
                    reason = "已取消";
                    return false;
                }
                if (!errors.IsEmpty)
                {
                    Cleanup(parts);
                    reason = string.Join("; ", errors.Take(3));
                    return false;
                }

                // 合并 + 边合并边算 SHA-1
                using (var outFs = new FileStream(tmpDest, FileMode.Create, FileAccess.Write, FileShare.None, 81920))
                using (var sha = System.Security.Cryptography.SHA1.Create())
                {
                    byte[] buf = new byte[81920];
                    foreach (string partFile in parts)
                    {
                        using (var fs = File.OpenRead(partFile))
                        {
                            int n;
                            while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                            {
                                outFs.Write(buf, 0, n);
                                sha.TransformBlock(buf, 0, n, null, 0);
                            }
                        }
                    }
                    sha.TransformFinalBlock(new byte[0], 0, 0);

                    if (!string.IsNullOrEmpty(expectedSha1))
                    {
                        var sb = new System.Text.StringBuilder();
                        foreach (byte b in sha.Hash) sb.Append(b.ToString("x2"));
                        string actual = sb.ToString();
                        if (!string.Equals(actual, expectedSha1, StringComparison.OrdinalIgnoreCase))
                        {
                            try { File.Delete(tmpDest); } catch { }
                            Cleanup(parts);
                            reason = "合并后 sha1 不符（期望 " + expectedSha1.Substring(0, Math.Min(8, expectedSha1.Length)) +
                                     "…，实际 " + actual.Substring(0, Math.Min(8, actual.Length)) + "…）";
                            return false;
                        }
                    }
                }

                long merged = new FileInfo(tmpDest).Length;
                if (merged != size)
                {
                    try { File.Delete(tmpDest); } catch { }
                    Cleanup(parts);
                    reason = "合并后大小不符（期望 " + size + "，实际 " + merged + "）";
                    return false;
                }

                AtomicMove(tmpDest, dest);
                Cleanup(parts);
                return true;
            }
            catch (Exception ex)
            {
                try { if (File.Exists(tmpDest)) File.Delete(tmpDest); } catch { }
                Cleanup(parts);
                reason = ex.Message;
                return false;
            }
        }

        private static void Cleanup(string[] parts)
        {
            foreach (string p in parts)
            {
                try { if (File.Exists(p)) File.Delete(p); } catch { }
            }
        }

        /// <summary>
        /// 原子替换：先写 .new，再 Replace 覆盖，失败时保留旧文件（不能先 Delete）。
        /// </summary>
        private static void AtomicMove(string tmp, string dest)
        {
            if (!File.Exists(dest))
            {
                File.Move(tmp, dest);
                return;
            }

            string bak = dest + ".bak-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            try
            {
                // 同卷、目标存在时用 Replace；失败说明跨卷或权限问题，退回 Copy
                File.Replace(tmp, dest, bak, true);
            }
            catch
            {
                // 不能直接 Copy 覆盖（可能截断）。先保住旧文件，再换。
                string bak2 = dest + ".bak2-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                try
                {
                    File.Move(dest, bak2);
                    try
                    {
                        File.Move(tmp, dest);
                        try { File.Delete(bak2); } catch { }
                    }
                    catch
                    {
                        try { File.Move(bak2, dest); } catch { }   // 换失败，恢复旧文件
                        throw;
                    }
                }
                catch
                {
                    if (!File.Exists(dest) && File.Exists(bak2))
                        try { File.Move(bak2, dest); } catch { }
                    throw;
                }
            }
            finally
            {
                try { if (File.Exists(bak)) File.Delete(bak); } catch { }
            }
        }

        /// <summary>下载一个区间到文件；成功返回 null，失败返回原因。</summary>
        private static string FetchRange(string url, long from, long to, string partFile, string etag)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.UserAgent = UA;
                req.Timeout = 60000;
                req.ReadWriteTimeout = 60000;
                req.AddRange(from, to);
                // 注意：不要加 If-Range。部分服务器（libraries.minecraft.net）在带 If-Range 时
                // 会改回 200 返回整个文件，导致分段失败。文件一致性由最后的 SHA-1 校验保证。

                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    if (resp.StatusCode != HttpStatusCode.PartialContent)
                        return "返回 " + (int)resp.StatusCode + "（不是 206）";

                    long expected = to - from + 1;
                    long got = 0;
                    using (var net = resp.GetResponseStream())
                    using (var fs = new FileStream(partFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920))
                    {
                        byte[] buf = new byte[81920];
                        int n;
                        while ((n = net.Read(buf, 0, buf.Length)) > 0)
                        {
                            if (CancelRequested) throw new OperationCanceledException();
                            fs.Write(buf, 0, n);
                            got += n;
                        }
                    }
                    if (got != expected)
                        return "长度不符（期望 " + expected + " 实际 " + got + "）";
                    return null;
                }
            }
            catch (OperationCanceledException) { return "已取消"; }
            catch (Exception ex) { return ex.Message; }
        }

        public static bool StreamToFile(string url, string dest, string expectedSha1, out string reason)
        {
            reason = "";
            string part = dest + ".part";
            try
            {
                string dir = Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
                req.UserAgent = UA;
                req.Timeout = 60000;
                req.ReadWriteTimeout = 60000;
                req.AllowAutoRedirect = true;

                string actualSha1;
                using (var resp = (System.Net.HttpWebResponse)req.GetResponse())
                using (var net = resp.GetResponseStream())
                using (var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920))
                using (var sha = System.Security.Cryptography.SHA1.Create())
                {
                    if (resp.StatusCode != System.Net.HttpStatusCode.OK)
                    {
                        reason = "HTTP " + (int)resp.StatusCode;
                        return false;
                    }

                    byte[] buf = new byte[81920];
                    int n;
                    while ((n = net.Read(buf, 0, buf.Length)) > 0)
                    {
                        if (CancelRequested)
                            throw new OperationCanceledException("用户取消下载");
                        file.Write(buf, 0, n);
                        sha.TransformBlock(buf, 0, n, null, 0);
                    }
                    sha.TransformFinalBlock(new byte[0], 0, 0);

                    var sb = new System.Text.StringBuilder();
                    foreach (byte b in sha.Hash) sb.Append(b.ToString("x2"));
                    actualSha1 = sb.ToString();
                }

                if (!string.IsNullOrEmpty(expectedSha1) &&
                    !string.Equals(actualSha1, expectedSha1, StringComparison.OrdinalIgnoreCase))
                {
                    reason = "sha1 不符（期望 " + expectedSha1.Substring(0, 8) + "…，实际 " + actualSha1.Substring(0, 8) + "…）";
                    try { File.Delete(part); } catch { }
                    return false;
                }

                AtomicMove(part, dest);
                return true;
            }
            catch (OperationCanceledException)
            {
                reason = "已取消";
                try { if (File.Exists(part)) File.Delete(part); } catch { }
                return false;
            }
            catch (Exception ex)
            {
                reason = ex.Message;
                try { if (File.Exists(part)) File.Delete(part); } catch { }
                return false;
            }
        }

        /// <summary>
        /// 下载一个文件到 dest（先试镜像/改写地址，再试官方）。
        /// expectedSha1 为空时跳过校验。返回 ok / skip / fail。
        /// </summary>
        public static string Download(string url, string dest, string expectedSha1, out string info,
                                      bool allowParallel = false)
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
                    string reason;
                    // 大文件先尝试分段并行；不支持 Range / 探测失败会自动退回单连接
                    if (allowParallel && DownloadParallel(url2, dest, expectedSha1, out reason)) return "ok";
                    if (StreamToFile(url2, dest, expectedSha1, out reason)) return "ok";
                    last = reason;
                }
                catch (Exception ex)
                {
                    last = ex.Message;
                }
            }
            info = last;
            return "fail";
        }

        /// <summary>置为 true 后，正在进行的下载会尽快停下并清理 .part 文件。</summary>
        public static volatile bool CancelRequested;

        /// <summary>下载进度回调：格式化文本, 已完成数, 总数（GUI 用来画进度条）。</summary>
        public static Action<string, int, int> Progress;

        /// <summary>并发下载一批任务（url, dest, sha1）。</summary>
        /// <summary>批量下载的结果：失败数 > 0 时调用方必须当成安装失败处理。</summary>
        public class DownloadBatchResult
        {
            public int Total;
            public int Ok;
            public int Skipped;
            public int Failed;
            public System.Collections.Generic.List<string> Failures = new System.Collections.Generic.List<string>();
            public bool AllOk { get { return Failed == 0; } }
        }

        public static DownloadBatchResult DownloadAll(IList<Tuple<string, string, string>> jobs, int workers,
                                       bool verbose = true)
        {
            int total = jobs.Count;
            if (total == 0)
            {
                Console.WriteLine("没有需要下载的文件");
                if (Progress != null) Progress("没有需要下载的文件", 0, 0);
                return new DownloadBatchResult { Total = 0 };
            }

            int ok = 0, skip = 0, fail = 0, done = 0;
            long bytes = 0;
            var failures = new List<string>();
            var pad = new object();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var lastReport = DateTime.Now;

            var options = new ParallelOptions { MaxDegreeOfParallelism = workers };
            int cancelled = 0;
            System.Threading.Tasks.Parallel.ForEach(jobs, options, job =>
            {
                if (CancelRequested)
                {
                    lock (pad) { done++; cancelled++; fail++; failures.Add(Path.GetFileName(job.Item2) + " -> 已取消"); }
                    return;
                }
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
            if (cancelled > 0)
                Console.WriteLine("[取消] " + cancelled + " 个文件未下载，不能当成安装完成");

            return new DownloadBatchResult { Total = total, Ok = ok, Skipped = skip, Failed = fail, Failures = failures };
        }
    }
}
