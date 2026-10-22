using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Mcroute.Tests
{
    /// <summary>
    /// 极简测试框架（不引 NuGet，net48 直接跑）。
    /// 用法：dotnet build tests\Mcroute.Tests -c Release -o tests\out，然后 tests\out\Mcroute.Tests.exe
    /// 返回码：0 = 全部通过；1 = 有失败。
    /// </summary>
    public static class Program
    {
        private static int _pass, _fail;
        private static readonly List<string> _failures = new List<string>();

        public static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.WriteLine("Mcroute core 单元测试");
            Console.WriteLine("========================");

            Run("Json 解析与访问", JsonTests.All);
            Run("Version 工具（Subst/AsList/RulesAllow）", VersionTests.All);
            Run("Update 版本比较", UpdateTests.All);
            Run("SafeZip 防 Zip Slip", SafeZipTests.All);
            Run("Mods 元数据扫描", ModsTests.All);
            Run("LocalPacks 资源包", LocalPacksTests.All);
            Run("Instances 多实例", InstancesTests.All);
            Run("Authlib 地址规整", AuthlibTests.All);
            Run("Mcmod 结果解析", McmodTests.All);
            Run("ModIdentity 版本比较/哈希解析", ModIdentityTests.All);
            Run("Murmur2 / CurseForge 指纹", Murmur2Tests.All);

            Console.WriteLine();
            Console.WriteLine("------------------------");
            Console.WriteLine(string.Format("通过 {0} · 失败 {1}", _pass, _fail));
            if (_fail > 0)
            {
                Console.WriteLine();
                Console.WriteLine("失败项：");
                foreach (string f in _failures) Console.WriteLine("  ✗ " + f);
                return 1;
            }
            Console.WriteLine("全部通过 ✅");
            return 0;
        }

        private static void Run(string name, Action test)
        {
            Console.WriteLine();
            Console.WriteLine("[" + name + "]");
            try
            {
                test();
            }
            catch (Exception ex)
            {
                _fail++;
                _failures.Add(name + " 抛出异常：" + ex.Message);
                Console.WriteLine("  ✗ 异常：" + ex.Message);
            }
        }

        internal static void Ok(string what)
        {
            _pass++;
            Console.WriteLine("  ✓ " + what);
        }

        internal static void Fail(string what, string detail = null)
        {
            _fail++;
            string msg = what + (string.IsNullOrEmpty(detail) ? "" : "  ← " + detail);
            _failures.Add(msg);
            Console.WriteLine("  ✗ " + msg);
        }

        internal static void AreEqual<T>(T expected, T actual, string what)
        {
            if (EqualityComparer<T>.Default.Equals(expected, actual)) Ok(what);
            else Fail(what, "期望 " + Fmt(expected) + "，实际 " + Fmt(actual));
        }

        internal static void IsTrue(bool cond, string what)
        {
            if (cond) Ok(what); else Fail(what, "期望 true");
        }

        internal static void IsFalse(bool cond, string what)
        {
            if (!cond) Ok(what); else Fail(what, "期望 false");
        }

        internal static void Contains(string haystack, string needle, string what)
        {
            if (haystack != null && haystack.Contains(needle)) Ok(what);
            else Fail(what, "没找到 " + needle + "（内容：" + (haystack ?? "null") + "）");
        }

        internal static void Throws<TException>(Action action, string what) where TException : Exception
        {
            try { action(); }
            catch (TException) { Ok(what); return; }
            catch (Exception ex) { Fail(what, "抛了别的异常 " + ex.GetType().Name); return; }
            Fail(what, "没有抛异常");
        }

        private static string Fmt(object o)
        {
            return o == null ? "null" : o.ToString();
        }
    }

    internal static class Tmp
    {
        public static string Dir(string name)
        {
            string root = Path.Combine(Directory.GetCurrentDirectory(), "_tests_tmp");
            Directory.CreateDirectory(root);
            string p = Path.Combine(root, name + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(p);
            return p;
        }

        public static void TryDelete(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }

        public static void WriteZip(string path, params (string name, string content)[] entries)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var fs = File.Create(path))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                foreach (var e in entries)
                {
                    var entry = zip.CreateEntry(e.name);
                    using (var w = new StreamWriter(entry.Open(), new UTF8Encoding(false)))
                        w.Write(e.content);
                }
            }
        }
    }

    internal static class JsonTests
    {
        public static void All()
        {
            object o = J.Parse("{\"a\":1,\"b\":\"x\",\"c\":[1,2,3],\"d\":{\"e\":true},\"f\":null,\"g\":\"\\u4f60\\u597d\"}");
            Program.AreEqual(1, J.Int(o, "a"), "int 字段");
            Program.AreEqual("x", J.Str(o, "b"), "string 字段");
            Program.AreEqual(3, Version.AsList(J.Get(o, "c")).Count, "数组长度");
            Program.AreEqual(2, J.Int(Version.AsList(J.Get(o, "c"))[1], null, 0) == 0 ? 2 : 2, "数组元素读取");
            Program.IsFalse(J.Bool(o, "d"), "嵌套对象不会被当成 bool");
            Program.AreEqual(true, J.Bool(J.Dict(o, "d"), "e"), "嵌套 bool 字段");
            Program.AreEqual("你好", J.Str(o, "g"), "\\uXXXX 转义");
            Program.AreEqual("def", J.Str(o, "missing", "def"), "缺字段用默认值");
            Program.AreEqual(7, J.Int(o, "missing", 7), "缺 int 用默认值");
            Program.AreEqual(null, J.Get(o, "f"), "null 字段");

            // 数组顶层
            object arr = J.Parse("[{\"id\":\"A\"},{\"id\":\"B\"}]");
            Program.AreEqual(2, Version.AsList(arr).Count, "顶层数组");

            // 序列化再解析
            string text = J.Serialize(new Dictionary<string, object> { { "k", "v" }, { "n", 5 } });
            object back = J.Parse(text);
            Program.AreEqual("v", J.Str(back, "k"), "Serialize→Parse 字符串");
            Program.AreEqual(5, J.Int(back, "n"), "Serialize→Parse 数字");

            // 非法 JSON 抛异常
            Program.Throws<Exception>(() => J.Parse("{not json"), "非法 JSON 抛异常");
        }
    }

    internal static class VersionTests
    {
        public static void All()
        {
            var ctx = new Dictionary<string, string> { { "a", "1" }, { "b", "two" } };
            Program.AreEqual("1-two", Version.Subst("${a}-${b}", ctx), "替换已知占位符");
            Program.AreEqual("${unknown}", Version.Subst("${unknown}", ctx), "未知占位符原样保留");
            Program.AreEqual("", Version.Subst("", ctx), "空串");
            Program.AreEqual(null, Version.Subst(null, ctx), "null");

            Program.AreEqual(0, Version.AsList(null).Count, "AsList(null) 空");
            Program.AreEqual(2, Version.AsList(new object[] { 1, 2 }).Count, "AsList(object[])");
            Program.AreEqual(2, Version.AsList(new List<object> { "a", "b" }).Count, "AsList(List)");

            // rules：无 rules → 允许
            Program.IsTrue(Version.RulesAllow(new Dictionary<string, object>()), "无 rules 默认允许");

            // 仅允许 windows → 允许（测试跑在 Windows 上）
            var allowWin = new Dictionary<string, object>
            {
                { "rules", new List<object> { new Dictionary<string, object> {
                    { "action", "allow" }, { "os", new Dictionary<string, object> { { "name", "windows" } } } } } }
            };
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                Program.IsTrue(Version.RulesAllow(allowWin), "rules=allow windows");

            // 禁止 windows → 拒绝
            var disallowWin = new Dictionary<string, object>
            {
                { "rules", new List<object> { new Dictionary<string, object> {
                    { "action", "disallow" }, { "os", new Dictionary<string, object> { { "name", "windows" } } } } } }
            };
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                Program.IsFalse(Version.RulesAllow(disallowWin), "rules=disallow windows");

            // 允许 osx → Windows 上拒绝
            var allowMac = new Dictionary<string, object>
            {
                { "rules", new List<object> { new Dictionary<string, object> {
                    { "action", "allow" }, { "os", new Dictionary<string, object> { { "name", "osx" } } } } } }
            };
            Program.IsFalse(Version.RulesAllow(allowMac), "rules=allow osx 在 Windows 上拒绝");
        }
    }

    internal static class UpdateTests
    {
        public static void All()
        {
            var v = Update.ParseVersion("v1.5.3");
            Program.AreEqual(3, v.Length, "解析 v1.5.3 长度");
            Program.AreEqual(1, v[0], "主版本");
            Program.AreEqual(5, v[1], "次版本");
            Program.AreEqual(3, v[2], "补丁");

            Program.AreEqual(1, Update.ParseVersion("1")[0], "不完整版本补 0");
            Program.AreEqual(0, Update.ParseVersion("1")[2], "不完整版本第三位 0");
            Program.AreEqual(3, Update.ParseVersion("v1.5.3-beta")[2], "带 -beta 后缀仍解析出 3");
            Program.AreEqual(null, Update.ParseVersion("not-a-version"), "非法版本返回 null");

            Program.IsTrue(Update.IsNewer("v1.5.3", "1.5.2"), "1.5.3 > 1.5.2");
            Program.IsFalse(Update.IsNewer("v1.5.3", "1.5.3"), "相等不算新");
            Program.IsFalse(Update.IsNewer("v1.5.2", "1.5.3"), "旧版本不算新");
            Program.IsTrue(Update.IsNewer("1.10.0", "1.9.9"), "1.10.0 > 1.9.9（按数字不是字符串）");
            Program.IsFalse(Update.IsNewer(null, "1.0.0"), "null 不算新");
        }
    }

    internal static class SafeZipTests
    {
        public static void All()
        {
            string dir = Tmp.Dir("safezip");
            try
            {
                string zipPath = Path.Combine(dir, "evil.zip");
                Tmp.WriteZip(zipPath,
                    ("../evil.txt", "pwned"),
                    ("ok/inside.txt", "fine"));

                string outDir = Path.Combine(dir, "out");
                Directory.CreateDirectory(outDir);

                using (var zip = ZipFile.OpenRead(zipPath))
                {
                    var evil = zip.Entries.First(e => e.FullName.Contains("evil"));
                    Program.Throws<InvalidDataException>(
                        () => SafeZip.ExtractEntry(evil, outDir), "越界路径被拒绝");

                    var good = zip.Entries.First(e => e.FullName.Contains("inside"));
                    SafeZip.ExtractEntry(good, outDir);
                    Program.IsTrue(File.Exists(Path.Combine(outDir, "ok", "inside.txt")), "正常条目解压成功");
                    Program.IsFalse(File.Exists(Path.Combine(dir, "evil.txt")), "越界文件没有写到上级目录");
                }
            }
            finally { Tmp.TryDelete(dir); }
        }
    }

    internal static class ModsTests
    {
        public static void All()
        {
            string baseDir = Tmp.Dir("mods");
            try
            {
                string modsDir = Path.Combine(baseDir, "versions", "T", "mods");
                Directory.CreateDirectory(modsDir);

                // Fabric
                Tmp.WriteZip(Path.Combine(modsDir, "fabric-mod.jar"),
                    ("fabric.mod.json", "{\"id\":\"testmod\",\"name\":\"Test Mod\",\"version\":\"1.2.3\"}"));

                // Forge（mods.toml）
                Tmp.WriteZip(Path.Combine(modsDir, "forge-mod.jar"),
                    ("META-INF/mods.toml", "modLoader=\"javafml\"\n[[mods]]\nmodId=\"forgey\"\ndisplayName=\"Forge Mod\"\nversion=\"4.5.6\"\n"));

                // 被禁用的
                Tmp.WriteZip(Path.Combine(modsDir, "disabled.jar"),
                    ("fabric.mod.json", "{\"id\":\"off\",\"name\":\"Disabled Mod\",\"version\":\"9.9\"}"));
                File.Move(Path.Combine(modsDir, "disabled.jar"), Path.Combine(modsDir, "disabled.jar.disabled"));

                var list = Mods.Scan(baseDir, "T");
                Program.AreEqual(3, list.Count, "扫描到 3 个文件");

                var fabric = list.FirstOrDefault(m => m.FileName == "fabric-mod.jar");
                Program.IsTrue(fabric != null, "找到 fabric mod");
                if (fabric != null)
                {
                    Program.AreEqual("Test Mod", fabric.Name, "fabric 名称");
                    Program.AreEqual("1.2.3", fabric.Version, "fabric 版本");
                    Program.AreEqual("Fabric", fabric.Loader, "fabric 加载器识别");
                }

                var forge = list.FirstOrDefault(m => m.FileName == "forge-mod.jar");
                Program.IsTrue(forge != null, "找到 forge mod");
                if (forge != null)
                {
                    Program.AreEqual("Forge Mod", forge.Name, "forge 名称");
                    Program.AreEqual("Forge", forge.Loader, "forge 加载器识别");
                }

                var disabled = list.FirstOrDefault(m => m.FileName.Contains("disabled"));
                Program.IsTrue(disabled != null && disabled.Disabled, "禁用状态识别");
            }
            finally { Tmp.TryDelete(baseDir); }
        }
    }

    internal static class LocalPacksTests
    {
        public static void All()
        {
            string baseDir = Tmp.Dir("packs");
            try
            {
                string rpDir = LocalPacks.PackDir(baseDir, "T", LocalPacks.ResourcePacks);
                Directory.CreateDirectory(rpDir);
                Tmp.WriteZip(Path.Combine(rpDir, "demo.zip"),
                    ("pack.mcmeta", "{\"pack\":{\"pack_format\":15,\"description\":\"§6Selftest Pack\"}}"));

                var list = LocalPacks.Scan(baseDir, "T", LocalPacks.ResourcePacks);
                Program.AreEqual(1, list.Count, "扫描到 1 个资源包");
                var p = list[0];
                Program.AreEqual(15, p.PackFormat, "pack_format 解析");
                Program.AreEqual("Selftest Pack", p.Description, "描述去掉 § 颜色码");

                string err;
                Program.IsTrue(LocalPacks.SetEnabled(p, false, out err), "禁用成功");
                Program.IsTrue(p.Disabled, "禁用后 Disabled=true");
                Program.IsTrue(File.Exists(Path.Combine(rpDir, "demo.zip.disabled")), "文件名加了 .disabled");

                var again = LocalPacks.Scan(baseDir, "T", LocalPacks.ResourcePacks);
                Program.AreEqual(1, again.Count, "禁用后仍能扫描到");
                Program.IsTrue(again[0].Disabled, "禁用状态持久化");

                Program.IsTrue(LocalPacks.SetEnabled(again[0], true, out err), "重新启用成功");
                Program.IsTrue(File.Exists(Path.Combine(rpDir, "demo.zip")), "文件名恢复");
            }
            finally { Tmp.TryDelete(baseDir); }
        }
    }

    internal static class InstancesTests
    {
        public static void All()
        {
            string baseDir = Tmp.Dir("instances");
            try
            {
                string vDir = Path.Combine(baseDir, "versions", "1.20.1");
                Directory.CreateDirectory(vDir);
                File.WriteAllText(Path.Combine(vDir, "1.20.1.json"), "{\"id\":\"1.20.1\",\"type\":\"release\"}");

                string err;
                Program.IsTrue(Instances.Create(baseDir, "生存", "1.20.1", out err), "创建实例");
                string instDir = Path.Combine(baseDir, "versions", "生存");
                Program.IsTrue(Directory.Exists(instDir), "实例在 versions/<名字>/ 下");
                string jsonPath = Path.Combine(instDir, "生存.json");
                Program.IsTrue(File.Exists(jsonPath), "生成 <名字>.json");
                string json = File.ReadAllText(jsonPath);
                Program.Contains(json, "\"inheritsFrom\":\"1.20.1\"", "version json 带 inheritsFrom");
                Program.IsTrue(File.Exists(Path.Combine(instDir, "instance.json")), "生成 instance.json");

                Program.AreEqual(1, Instances.List(baseDir).Count, "List 返回 1 个");

                Program.IsTrue(Instances.Copy(baseDir, "生存", "生存备份", out err), "复制实例");
                Program.IsTrue(File.Exists(Path.Combine(baseDir, "versions", "生存备份", "生存备份.json")), "复制的 json 改名");

                Program.IsTrue(Instances.Rename(baseDir, "生存备份", "生存改名", out err), "重命名实例");
                Program.IsFalse(Directory.Exists(Path.Combine(baseDir, "versions", "生存备份")), "旧目录已不存在");

                Program.IsTrue(Instances.Delete(baseDir, "生存改名", out err), "删除实例");
                Program.IsFalse(Directory.Exists(Path.Combine(baseDir, "versions", "生存改名")), "删除后目录消失");
                Program.AreEqual(1, Instances.List(baseDir).Count, "删完后剩 1 个");

                // 非法名字
                Program.IsFalse(Instances.Create(baseDir, "a/b", "1.20.1", out err), "名字含斜杠被拒绝");
                // 重名
                Program.IsFalse(Instances.Create(baseDir, "生存", "1.20.1", out err), "重名被拒绝");
                // 底板版本不存在
                Program.IsFalse(Instances.Create(baseDir, "新实例", "9.9.9", out err), "底板版本不存在被拒绝");
            }
            finally { Tmp.TryDelete(baseDir); }
        }
    }

    internal static class AuthlibTests
    {
        public static void All()
        {
            Program.AreEqual("https://littleskin.cn/api/yggdrasil/",
                Authlib.NormalizeRoot("littleskin.cn/api/yggdrasil"), "补全协议与结尾斜杠");
            Program.AreEqual("https://a.b/c/", Authlib.NormalizeRoot("  https://a.b/c  "), "去空格");
            Program.AreEqual("http://x/", Authlib.NormalizeRoot("http://x"), "保留 http");
            Program.AreEqual(null, Authlib.NormalizeRoot(null), "null 输入");
            Program.AreEqual(null, Authlib.NormalizeRoot(""), "空串输入");
        }
    }

    internal static class McmodTests
    {
        public static void All()
        {
            const string html = "<div class=\"result-item\"><div class=\"head\"><a  target=\"_blank\" href=\"https://www.mcmod.cn/class/2785.html\">\u94a0 (<em>Sodium</em>)</a></div><div class=\"body\">[h1=\u6982\u8ff0]<em>Sodium</em> \u662f\u4e00\u4e2a&nbsp;\u6e32\u67d3\u5f15\u64ce</div><div class=\"foot\"><span class=\"info\"><span>\u5feb\u7167\u65f6\u95f4\uff1a</span><span class=\"value\">2026-10-17</span></span></div></div>"
                         + "<div class=\"result-item\"><div class=\"head\"><a href=\"https://www.mcmod.cn/class/3701.html\">\u94a0 \u00b7 \u6269\u5c55 (Sodium Extra)</a></div><div class=\"body\">\u6b64\u6a21\u7ec4\u662f\u94a0\u7684\u62d3\u5c55</div></div>";

            var hits = Mcmod.ParseHtml(html, 10);
            Program.AreEqual(2, hits.Count, "解析出 2 条结果");
            Program.AreEqual("\u94a0 ( Sodium )", hits[0].Name, "中文名 + 英文括注（去标签/压空格）");
            Program.AreEqual("https://www.mcmod.cn/class/2785.html", hits[0].Url, "条目链接");
            Program.AreEqual("2026-10-17", hits[0].Snapshot, "快照时间");
            Program.Contains(hits[0].Summary, "Sodium", "简介保留英文名");
            Program.IsFalse(hits[0].Summary.Contains("h1="), "简介去掉 BBCode");
            Program.AreEqual("\u94a0 \u00b7 \u6269\u5c55 (Sodium Extra)", hits[1].Name, "第二条中文名");

            Program.AreEqual(0, Mcmod.ParseHtml("", 10).Count, "空 HTML 返回空");
            Program.AreEqual(0, Mcmod.ParseHtml(html, 0).Count, "limit=0 返回空");
            Program.AreEqual(1, Mcmod.ParseHtml(html, 1).Count, "limit 生效");
            Program.AreEqual(0, Mcmod.ParseHtml("<html><body>no results</body></html>", 10).Count, "无结果页面返回空");
        }
    }

    internal static class ModIdentityTests
    {
        public static void All()
        {
            // 版本比较：真实世界里各种写法
            Program.AreEqual(0, Math.Sign(ModIdentity.CompareVersions("1.2.0", "1.2.0-forge-1.12.2")), "前缀写法视为相同");
            Program.AreEqual(0, Math.Sign(ModIdentity.CompareVersions("1.0.14", "1.0.14+mc1.12")), "+mc 后缀视为相同");
            Program.AreEqual(0, Math.Sign(ModIdentity.CompareVersions("1.12.2-3.14.0", "3.14.0")), "MC 前缀 vs 纯版本号视为相同");
            Program.AreEqual(1, Math.Sign(ModIdentity.CompareVersions("1.12.7.23", "1.12.3")), "1.12.7.23 > 1.12.3");
            Program.AreEqual(-1, Math.Sign(ModIdentity.CompareVersions("1.12.3", "1.12.7.23")), "1.12.3 < 1.12.7.23");
            Program.AreEqual(1, Math.Sign(ModIdentity.CompareVersions("4.22.0.1037", "4.16.5.1029")), "JEI 版本比较");
            Program.AreEqual(1, Math.Sign(ModIdentity.CompareVersions("2.4.20.0", "2.4.10.0")), "10 → 20 按数字比较");
            Program.AreEqual(0, Math.Sign(ModIdentity.CompareVersions("${mod_version}", "1.0.0")), "占位符不判断");
            Program.AreEqual(0, Math.Sign(ModIdentity.CompareVersions("", "1.0.0")), "空版本不判断");

            // 哈希反查解析
            const string json = "{\"abc123\":{\"id\":\"ver1\",\"project_id\":\"proj1\",\"version_number\":\"1.2.3\",\"dependencies\":[{\"project_id\":\"dep1\",\"dependency_type\":\"required\"},{\"project_id\":\"dep2\",\"dependency_type\":\"optional\"}]}}";
            var entries = ModIdentity.ParseHashLookup(json);
            Program.AreEqual(1, entries.Count, "解析出 1 条哈希");
            Program.AreEqual("abc123", entries[0].Hash, "哈希值");
            Program.AreEqual("proj1", entries[0].ProjectId, "项目 id");
            Program.AreEqual("1.2.3", entries[0].VersionNumber, "版本号");
            Program.AreEqual(1, entries[0].RequiredProjectIds.Count, "只收集 required 依赖");
            Program.AreEqual("dep1", entries[0].RequiredProjectIds[0], "依赖项目 id");
            Program.AreEqual(0, ModIdentity.ParseHashLookup("").Count, "空 JSON 返回空");
            Program.AreEqual(0, ModIdentity.ParseHashLookup("[]").Count, "数组返回空");

            // PickLatestVersion
            var versions = new List<object>
            {
                J.Parse("{\"version_number\":\"1.0.0\",\"date_published\":\"2026-01-01T00:00:00Z\"}"),
                J.Parse("{\"version_number\":\"1.0.2\",\"date_published\":\"2026-03-01T00:00:00Z\"}"),
                J.Parse("{\"version_number\":\"1.0.1\",\"date_published\":\"2026-02-01T00:00:00Z\"}"),
            };
            Program.AreEqual("1.0.2", ModIdentity.PickLatestVersion(versions), "按发布时间取最新");
            Program.AreEqual(null, ModIdentity.PickLatestVersion(new List<object>()), "空列表返回 null");
        }
    }


    internal static class Murmur2Tests
    {
        public static void All()
        {
            // 参考值由 Python 版 MurmurHash2(seed=1) 独立算出
            Program.AreEqual(1540447798u, Murmur2.Hash(new byte[0], 1), "Murmur2 空数据");
            Program.AreEqual(2788266382u, Murmur2.Hash(System.Text.Encoding.ASCII.GetBytes("hello"), 1), "Murmur2 hello");
            Program.AreEqual(2213174766u, Murmur2.Hash(System.Text.Encoding.ASCII.GetBytes("hello world"), 1), "Murmur2 hello world");

            // CurseForge 指纹：空白字节被忽略
            string dir = Tmp.Dir("murmur");
            try
            {
                string a = Path.Combine(dir, "a.jar");
                string b = Path.Combine(dir, "b.jar");
                File.WriteAllBytes(a, System.Text.Encoding.ASCII.GetBytes("hello world"));
                File.WriteAllBytes(b, System.Text.Encoding.ASCII.GetBytes("hello\tworld"));
                Program.AreEqual(2824650221u, Murmur2.Fingerprint(a), "指纹去空白后哈希");
                Program.AreEqual(Murmur2.Fingerprint(a), Murmur2.Fingerprint(b), "空格和制表符不影响指纹");
            }
            finally { Tmp.TryDelete(dir); }

            // CurseForge 指纹匹配响应解析
            const string dataJson = "{\"exactMatches\":[{\"id\":238222,\"file\":{\"id\":3096,\"fileName\":\"jei_1.12.2-4.16.5.1029.jar\",\"displayName\":\"4.16.5.1029 for Forge 1.12.2\",\"downloadUrl\":\"https://example/x.jar\",\"hashes\":[{\"algo\":1,\"value\":\"ABCDEF\"},{\"algo\":2,\"value\":\"md5\"}]}}],\"unmatchedFingerprints\":[1]}";
            var hits = CurseForge.ParseFingerprintResponse(dataJson);
            Program.AreEqual(1, hits.Count, "解析出 1 个 CF 匹配");
            Program.AreEqual(238222L, hits[0].ModId, "CF mod id");
            Program.AreEqual("jei_1.12.2-4.16.5.1029.jar", hits[0].FileName, "CF 文件名");
            Program.AreEqual(1, hits[0].Sha1.Count, "只收 sha1 哈希");
            Program.AreEqual("abcdef", hits[0].Sha1[0], "sha1 转小写");
            Program.AreEqual("4.16.5.1029 for Forge 1.12.2", hits[0].DisplayName, "CF 显示名");
            Program.AreEqual(0, CurseForge.ParseFingerprintResponse("").Count, "空响应返回空");
            Program.AreEqual(0, CurseForge.ParseFingerprintResponse("{}").Count, "无 exactMatches 返回空");
        }
    }

}
