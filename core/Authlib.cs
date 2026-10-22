using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Mcroute
{
    /// <summary>外置登录（Yggdrasil / authlib-injector）账号。</summary>
    public class AuthAccount
    {
        public string ApiRoot;       // 例如 https://littleskin.cn/api/yggdrasil/
        public string ServerName;    // 显示用，登录时从元数据取
        public string AccessToken;
        public string Uuid;
        public string Name;
        public string ClientToken;
    }

    /// <summary>
    /// 外置登录（皮肤站 / Yggdrasil）支持。
    ///
    /// 原理：启动时加 `-javaagent:authlib-injector.jar=&lt;api_root&gt;`，
    /// 由 authlib-injector 把游戏里的 Mojang 验证重定向到皮肤站。
    /// 我们只负责：下载 agent、按 Yggdrasil 协议登录拿 token、把 token 塞进启动参数。
    ///
    /// 合法：不碰 Mojang 客户端凭据，不伪造正版；用户用的是皮肤站自己的账号。
    /// </summary>
    public static class Authlib
    {
        private const string AccountFile = "authlib_account.json";
        private const string InjectorFile = @"tools\authlib-injector.jar";

        /// <summary>GitHub 上 authlib-injector 的 release API（用于取最新 jar）。</summary>
        private const string ReleasesApi = "https://api.github.com/repos/yushijinhun/authlib-injector/releases/latest";
        /// <summary>取不到最新版时的兜底地址。</summary>
        private const string FallbackUrl =
            "https://github.com/yushijinhun/authlib-injector/releases/download/v1.2.8/authlib-injector-1.2.8.jar";

        public static string InjectorPath(string baseDir)
        {
            return Path.Combine(baseDir, InjectorFile);
        }

        /// <summary>把地址规整成以 / 结尾、去掉多余空格。</summary>
        public static string NormalizeRoot(string apiRoot)
        {
            if (string.IsNullOrEmpty(apiRoot)) return null;
            string s = apiRoot.Trim();
            if (!s.StartsWith("http://") && !s.StartsWith("https://")) s = "https://" + s;
            if (!s.EndsWith("/")) s += "/";
            return s;
        }

        /// <summary>确保 authlib-injector.jar 存在；返回路径，失败返回 null。</summary>
        public static string EnsureInjector(string baseDir, out string error)
        {
            error = null;
            try
            {
                string path = InjectorPath(baseDir);
                if (File.Exists(path) && new FileInfo(path).Length > 100 * 1024)
                    return path;

                Directory.CreateDirectory(Path.GetDirectoryName(path));

                string url = null;
                try
                {
                    var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(ReleasesApi);
                    req.UserAgent = "KyoukaMCLauncher/1.0.3";
                    req.Accept = "application/vnd.github+json";
                    req.Timeout = 20000;
                    string text;
                    using (var resp = (System.Net.HttpWebResponse)req.GetResponse())
                    using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                        text = sr.ReadToEnd();
                    object json = J.Parse(text);
                    foreach (object a in Version.AsList(J.Get(json, "assets")) ?? new List<object>())
                    {
                        string name = J.Str(a, "name");
                        string dl = J.Str(a, "browser_download_url");
                        if (!string.IsNullOrEmpty(name) && name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
                            && !name.Contains("sources") && !string.IsNullOrEmpty(dl))
                        {
                            url = dl;
                            break;
                        }
                    }
                }
                catch { }

                if (string.IsNullOrEmpty(url)) url = FallbackUrl;

                string info;
                string status = Http.Download(url, path, null, out info, false);
                if (status == "fail" || !File.Exists(path) || new FileInfo(path).Length < 100 * 1024)
                {
                    error = "下载 authlib-injector 失败：" + info;
                    try { if (File.Exists(path)) File.Delete(path); } catch { }
                    return null;
                }
                return path;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        /// <summary>登录（Yggdrasil authenticate）。成功返回账号并写入磁盘。</summary>
        public static bool Login(string baseDir, string apiRoot, string username, string password,
                                 out AuthAccount account, out string error)
        {
            account = null;
            error = null;

            string root = NormalizeRoot(apiRoot);
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            {
                error = "服务器地址 / 用户名 / 密码 都要填";
                return false;
            }

            try
            {
                string clientToken = Guid.NewGuid().ToString("N");
                string body = "{\"username\":" + Esc(username) +
                              ",\"password\":" + Esc(password) +
                              ",\"clientToken\":" + Esc(clientToken) +
                              ",\"agent\":{\"name\":\"Minecraft\",\"version\":1},\"requestUser\":false}";

                string url = root.TrimEnd('/') + "/authserver/authenticate";
                string json = PostJson(url, body);
                object obj = J.Parse(json);

                string token = J.Str(obj, "accessToken");
                object profile = J.Get(obj, "selectedProfile");
                string name = J.Str(profile, "name");
                string uuid = J.Str(profile, "id");
                if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(name))
                {
                    error = "登录失败：返回里没有 accessToken / 角色名";
                    return false;
                }

                account = new AuthAccount
                {
                    ApiRoot = root,
                    ServerName = ServerName(root),
                    AccessToken = token,
                    Uuid = (uuid ?? "").Replace("-", ""),
                    Name = name,
                    ClientToken = clientToken,
                };
                Save(baseDir, account);
                return true;
            }
            catch (System.Net.WebException wex)
            {
                string detail = "";
                try
                {
                    using (var resp = wex.Response as System.Net.HttpWebResponse)
                    using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                        detail = sr.ReadToEnd();
                }
                catch { }

                string reason = "";
                try
                {
                    object e = J.Parse(detail);
                    reason = J.Str(e, "errorMessage");
                }
                catch { }

                error = "登录失败：" + (string.IsNullOrEmpty(reason) ? wex.Message : reason);
                return false;
            }
            catch (Exception ex)
            {
                error = "登录失败：" + ex.Message;
                return false;
            }
        }

        /// <summary>读服务器名（失败就返回 null，不阻塞）。</summary>
        public static string ServerName(string apiRoot)
        {
            try
            {
                var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(NormalizeRoot(apiRoot));
                req.UserAgent = "KyoukaMCLauncher/1.0.3";
                req.Timeout = 8000;
                string text;
                using (var resp = (System.Net.HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    text = sr.ReadToEnd();
                object json = J.Parse(text);
                string n = J.Str(J.Dict(json, "meta"), "serverName");
                return string.IsNullOrEmpty(n) ? null : n;
            }
            catch { return null; }
        }

        public static bool TryLoad(string baseDir, out AuthAccount account)
        {
            account = null;
            try
            {
                string path = Path.Combine(baseDir, AccountFile);
                if (!File.Exists(path)) return false;
                object json = J.Parse(File.ReadAllText(path, Encoding.UTF8));
                var a = new AuthAccount
                {
                    ApiRoot = J.Str(json, "apiRoot"),
                    ServerName = J.Str(json, "serverName"),
                    AccessToken = J.Str(json, "accessToken"),
                    Uuid = J.Str(json, "uuid"),
                    Name = J.Str(json, "name"),
                    ClientToken = J.Str(json, "clientToken"),
                };
                if (string.IsNullOrEmpty(a.ApiRoot) || string.IsNullOrEmpty(a.AccessToken) || string.IsNullOrEmpty(a.Name))
                    return false;
                account = a;
                return true;
            }
            catch { return false; }
        }

        public static void Save(string baseDir, AuthAccount a)
        {
            try
            {
                Directory.CreateDirectory(baseDir);
                string json = "{\"apiRoot\":" + Esc(a.ApiRoot) +
                              ",\"serverName\":" + Esc(a.ServerName) +
                              ",\"accessToken\":" + Esc(a.AccessToken) +
                              ",\"uuid\":" + Esc(a.Uuid) +
                              ",\"name\":" + Esc(a.Name) +
                              ",\"clientToken\":" + Esc(a.ClientToken) + "}";
                File.WriteAllText(Path.Combine(baseDir, AccountFile), json, new UTF8Encoding(false));
            }
            catch { }
        }

        public static void Clear(string baseDir)
        {
            try
            {
                string path = Path.Combine(baseDir, AccountFile);
                if (File.Exists(path)) File.Delete(path);
            }
            catch { }
        }

        private static string Esc(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        private static string PostJson(string url, string body)
        {
            var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
            req.Method = "POST";
            req.UserAgent = "KyoukaMCLauncher/1.0.3";
            req.ContentType = "application/json";
            req.Accept = "application/json";
            req.Timeout = 20000;
            byte[] data = Encoding.UTF8.GetBytes(body);
            req.ContentLength = data.Length;
            using (var s = req.GetRequestStream()) s.Write(data, 0, data.Length);
            using (var resp = (System.Net.HttpWebResponse)req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return sr.ReadToEnd();
        }
    }
}
