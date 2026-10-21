using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace Mcroute
{
    /// <summary>版本 json 的读取 / inheritsFrom 合并 / rules 判定 / 变量替换。</summary>
    public static class Version
    {
        /// <summary>
        /// 选数据目录。**永远不抛异常** —— 早版本这里一旦建目录失败就直接崩，
        /// 用户看到的现象就是"双击没反应"。
        /// 顺序：有 versions 的既有目录 → exe 旁边 → %LOCALAPPDATA% → %TEMP% → 当前目录。
        /// </summary>
        public static string BaseDir(string argBase)
        {
            if (!string.IsNullOrEmpty(argBase)) { var __r = Path.GetFullPath(argBase); Mirrors.ContextBaseDir = __r; return __r; }

            string exeDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            var candidates = new List<string>
            {
                Path.Combine(exeDir, "mcdata"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "mcroute", "mcdata"),
                Path.Combine(Path.GetTempPath(), "mcroute", "mcdata"),
                Path.Combine(Directory.GetCurrentDirectory(), "mcdata"),
            };

            // 第一轮：已经装了版本的数据目录优先（哪怕只读，至少能启动）
            foreach (string dir in candidates)
            {
                try { if (Directory.Exists(Path.Combine(dir, "versions"))) { var __r = dir; Mirrors.ContextBaseDir = __r; return __r; } } catch { }
            }

            // 第二轮：找一个能写的目录
            foreach (string dir in candidates)
            {
                if (CanWrite(dir)) { var __r = dir; Mirrors.ContextBaseDir = __r; return __r; }
            }

            // 全都不行：只能返回第一个（后续操作会报具体错误，但程序能开起来提示用户）
            { var __r = candidates[0]; Mirrors.ContextBaseDir = __r; return __r; }
        }

        /// <summary>目录能不能建/写（不抛异常）。</summary>
        private static bool CanWrite(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                string probe = Path.Combine(dir, "write_test.tmp");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }

        public static string VersionJsonPath(string baseDir, string ver)
        {
            return Path.Combine(baseDir, "versions", ver, ver + ".json");
        }

        /// <summary>读版本 json 并处理 inheritsFrom（递归合并）。失败返回 null。</summary>
        /// <summary>
        /// 从版本 json 推断「MC 版本 + 加载器」，给界面显示用（如 "1.20.1 · Forge 47.1.106"）。
        /// 识别顺序：库名（最可靠）→ mainClass。
        /// </summary>
        public static string Describe(string baseDir, string ver, out string mcVersion,
                                      out string loaderName, out string loaderVersion)
        {
            mcVersion = null; loaderName = null; loaderVersion = null;
            try
            {
                var json = Load(baseDir, ver);
                if (json == null) return ver;

                string id = J.Str(json, "id");
                string inherits = J.Str(json, "inheritsFrom");
                string mainClass = J.Str(json, "mainClass") ?? "";

                mcVersion = !string.IsNullOrEmpty(inherits) ? inherits : GuessMcVersion(id ?? ver);

                var names = new List<string>();
                foreach (object lib in AsList(J.Get(json, "libraries")))
                    names.Add(J.Str(lib, "name"));

                string fabric = FindLib(names, "net.fabricmc:fabric-loader:");
                string quilt = FindLib(names, "org.quiltmc:quilt-loader:");
                string neo = FindLib(names, "net.neoforged:neoforge:");
                string neoFml = FindLib(names, "net.neoforged.fancymodloader:loader");
                string forge = FindLib(names, "net.minecraftforge:forge:");
                string fml = FindLib(names, "net.minecraftforge:fmlloader:");

                if (fabric != null)
                {
                    loaderName = IsLegacyMc(mcVersion) ? "Legacy Fabric" : "Fabric";
                    loaderVersion = VersionAfter(fabric);
                }
                else if (quilt != null)
                {
                    loaderName = "Quilt";
                    loaderVersion = VersionAfter(quilt);
                }
                else if (neo != null)
                {
                    loaderName = "NeoForge";
                    loaderVersion = StripMc(VersionAfter(neo), mcVersion);
                }
                else if (neoFml != null)
                {
                    // NeoForge 1.20.1 只装 fancymodloader，版本号从 id 里取（neoforge 47.x）
                    loaderName = "NeoForge";
                    loaderVersion = DeriveFromId(id, mcVersion);
                }
                else if (forge != null)
                {
                    loaderName = "Forge";
                    loaderVersion = StripMc(VersionAfter(forge), mcVersion);
                }
                else if (fml != null)
                {
                    loaderName = "Forge";
                    loaderVersion = StripMc(VersionAfter(fml), mcVersion);
                }
                else if (mainClass.IndexOf("BootstrapLauncher", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    loaderName = "Forge";
                    loaderVersion = DeriveFromId(id, mcVersion);
                }
                else if (mainClass.IndexOf("launchwrapper", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    loaderName = "Forge";
                    loaderVersion = DeriveFromId(id, mcVersion);
                }

                if (loaderName != null && string.IsNullOrEmpty(loaderVersion))
                    loaderVersion = DeriveFromId(id, mcVersion);

                string sub = mcVersion ?? ver;
                if (!string.IsNullOrEmpty(loaderName))
                    sub += " · " + loaderName + (string.IsNullOrEmpty(loaderVersion) ? "" : " " + loaderVersion);
                return sub;
            }
            catch
            {
                return ver;
            }
        }

        private static string FindLib(List<string> names, string prefix)
        {
            foreach (string n in names)
                if (n != null && n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return n;
            return null;
        }

        /// <summary>"net.fabricmc:fabric-loader:0.19.5" -> "0.19.5"</summary>
        private static string VersionAfter(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            int i = name.LastIndexOf(':');
            return i >= 0 && i + 1 < name.Length ? name.Substring(i + 1) : "";
        }

        /// <summary>"1.12.2-14.23.5.2864" -> "14.23.5.2864"</summary>
        private static string StripMc(string ver, string mc)
        {
            if (string.IsNullOrEmpty(ver)) return ver;
            if (!string.IsNullOrEmpty(mc) && ver.StartsWith(mc + "-", StringComparison.Ordinal))
                return ver.Substring(mc.Length + 1);
            return ver;
        }

        /// <summary>从 id 里抠加载器版本：1.12.2-forge-14.23.5.2864 -> 14.23.5.2864</summary>
        private static string DeriveFromId(string id, string mc)
        {
            if (string.IsNullOrEmpty(id)) return "";
            string rest = id;
            if (!string.IsNullOrEmpty(mc) && rest.StartsWith(mc + "-", StringComparison.Ordinal))
                rest = rest.Substring(mc.Length + 1);

            // 官方启动器的名字可能是 1.18.2-Forge_40.3.12 / 26.1.2-NeoForge_26.1.2.106
            // 所以分隔符既可能是 - 也可能是 _
            var m = Regex.Match(rest, @"^(neoforge|forge|fabric-loader|fabric|quilt-loader|quilt)[-_]",
                                RegexOptions.IgnoreCase);
            if (m.Success) rest = rest.Substring(m.Length);
            // fabric-loader-0.19.5-1.19.2 -> 0.19.5
            if (!string.IsNullOrEmpty(mc) && rest.EndsWith("-" + mc, StringComparison.Ordinal))
                rest = rest.Substring(0, rest.Length - mc.Length - 1);
            return rest;
        }

        /// <summary>从 id（如 1.20.1-forge-47.1.106）里猜 MC 版本。</summary>
        private static string GuessMcVersion(string id)
        {
            var m = Regex.Match(id ?? "", @"^\d+\.\d+(\.\d+)?");
            return m.Success ? m.Value : null;
        }

        private static bool IsLegacyMc(string mc)
        {
            if (string.IsNullOrEmpty(mc)) return false;
            var m = Regex.Match(mc, @"^1\.(\d+)");
            if (!m.Success) return false;
            int minor;
            return int.TryParse(m.Groups[1].Value, out minor) && minor <= 13;
        }

        public static Dictionary<string, object> Load(string baseDir, string ver, int depth = 0)
        {
            string path = VersionJsonPath(baseDir, ver);
            if (!File.Exists(path) || depth > 5) return null;

            Dictionary<string, object> data;
            try { data = J.Obj(J.ParseFile(path)); }
            catch { return null; }
            if (data == null) return null;

            string parentId = J.Str(data, "inheritsFrom");
            if (string.IsNullOrEmpty(parentId)) return data;

            var parent = Load(baseDir, parentId, depth + 1);
            if (parent == null) return data;
            return Merge(parent, data);
        }

        /// <summary>把子版本合并到父版本上（libraries 按 artifact 去重，子覆盖父）。</summary>
        public static Dictionary<string, object> Merge(Dictionary<string, object> parent,
                                                       Dictionary<string, object> child)
        {
            var merged = new Dictionary<string, object>(parent);
            foreach (var kv in child)
            {
                if (kv.Key == "libraries")
                {
                    var order = new List<string>();
                    var table = new Dictionary<string, object>();

                    // 同 artifact 的两条记录怎么选：
                    //   有可下载 jar 的优先（1.12.2 里 lwjgl 2.9.4 有 jar、2.9.2 只有 natives，
                    //   盲目"后者覆盖"会把 lwjgl 整个弄丢）
                    void Put(object lib)
                    {
                        string key = LibKey(lib);
                        if (!table.ContainsKey(key)) order.Add(key);
                        if (table.ContainsKey(key) && !HasArtifact(lib) && HasArtifact(table[key]))
                            return;
                        table[key] = lib;
                    }

                    foreach (var lib in AsList(parent.ContainsKey("libraries") ? parent["libraries"] : null))
                        Put(lib);
                    foreach (var lib in AsList(kv.Value))
                        Put(lib);      // 子版本优先（在都能下载的前提下）
                    var list = new List<object>();
                    foreach (string k in order) list.Add(table[k]);
                    merged["libraries"] = list.ToArray();
                }
                else if (kv.Key == "arguments")
                {
                    var mergedArgs = new Dictionary<string, object>();
                    var baseArgs = parent.ContainsKey("arguments") ? J.Obj(parent["arguments"]) : null;
                    foreach (string bucket in new[] { "jvm", "game" })
                    {
                        var all = new List<object>();
                        if (baseArgs != null && baseArgs.ContainsKey(bucket))
                            all.AddRange(AsList(baseArgs[bucket]));
                        if (J.Obj(kv.Value) != null && J.Obj(kv.Value).ContainsKey(bucket))
                            all.AddRange(AsList(J.Obj(kv.Value)[bucket]));
                        mergedArgs[bucket] = all.ToArray();
                    }
                    merged["arguments"] = mergedArgs;
                }
                else
                {
                    merged[kv.Key] = kv.Value;
                }
            }
            return merged;
        }

        /// <summary>这条库有没有真正可下载的 jar（而不是只有 natives 分类器）。</summary>
        private static bool HasArtifact(object lib)
        {
            var downloads = J.Dict(lib, "downloads");
            var artifact = downloads == null ? null : J.Dict(downloads, "artifact");
            if (artifact != null)
            {
                if (!string.IsNullOrEmpty(J.Str(artifact, "path"))) return true;
                if (!string.IsNullOrEmpty(J.Str(artifact, "url"))) return true;
            }
            // Fabric 风格：name + 仓库根地址
            return !string.IsNullOrEmpty(J.Str(lib, "url")) && !string.IsNullOrEmpty(J.Str(lib, "name"));
        }

        /// <summary>库去重键：groupId:artifactId:classifier:有无natives（不含版本号）</summary>
        private static string LibKey(object lib)
        {
            var parts = J.Str(lib, "name").Split(':');
            string group = parts.Length > 0 ? parts[0] : "";
            string artifact = parts.Length > 1 ? parts[1] : "";
            string classifier = parts.Length > 3 ? parts[3] : "";
            bool natives = J.Dict(lib, "natives") != null;
            // rules 不同说明是互斥条目（比如 1.12.2 的 lwjgl：2.9.4 给 Windows、2.9.2 只给 osx），
            // 必须都保留，否则按 rules 过滤后会整条消失
            var rules = J.Get(lib, "rules");
            string rulesSig = rules == null ? "" : J.Serialize(rules);
            return group + ":" + artifact + ":" + classifier + ":" + (natives ? "1" : "0") + ":" + rulesSig;
        }

        public static List<object> AsList(object o)
        {
            var list = new List<object>();
            var arr = o as object[];
            if (arr != null) { list.AddRange(arr); return list; }
            var arr2 = o as List<object>;
            if (arr2 != null) { list.AddRange(arr2); return list; }
            return list;
        }

        // ---------------- rules ----------------
        public static bool RulesAllow(object lib)
        {
            var rules = AsList(J.Get(lib, "rules"));
            if (rules.Count == 0) return true;

            bool allow = false;
            foreach (var rule in rules)
            {
                if (!RuleMatches(rule)) continue;
                allow = J.Str(rule, "action", "allow") == "allow";
            }
            return allow;
        }

        private static bool RuleMatches(object rule)
        {
            var os = J.Dict(rule, "os");
            if (os != null)
            {
                string name = J.Str(os, "name");
                if (!string.IsNullOrEmpty(name) && name != "windows") return false;
                string arch = J.Str(os, "arch");
                if (!string.IsNullOrEmpty(arch) && arch != "x86_64" && arch != "x64") return false;
            }
            var features = J.Dict(rule, "features");
            if (features != null)
            {
                foreach (var kv in features)
                {
                    bool want = kv.Value is bool ? (bool)kv.Value : Convert.ToBoolean(kv.Value);
                    if (want) return false;   // 我们不启用任何 features
                }
            }
            return true;
        }

        // ---------------- 变量替换 ----------------
        public static string Subst(string template, Dictionary<string, string> ctx)
        {
            if (string.IsNullOrEmpty(template)) return template;
            return Regex.Replace(template, @"\$\{([^}]+)\}", m =>
            {
                string key = m.Groups[1].Value;
                string v;
                return ctx.TryGetValue(key, out v) ? v : m.Value;
            });
        }

        /// <summary>展开 arguments 条目（可能是字符串，也可能是带 rules 的对象）。</summary>
        public static List<string> ExpandArgs(object rawArgs, Dictionary<string, string> ctx,
                                              bool splitStrings)
        {
            var result = new List<string>();
            foreach (var item in AsList(rawArgs))
            {
                if (item is string)
                {
                    string s = (string)item;
                    if (splitStrings)
                    {
                        foreach (string piece in s.Split(' ')) if (piece.Length > 0) result.Add(piece);
                    }
                    else result.Add(s);
                    continue;
                }
                if (!RulesAllow(item)) continue;
                var value = J.Get(item, "value");
                if (value is string)
                {
                    result.Add((string)value);
                }
                else
                {
                    foreach (var v in AsList(value)) result.Add(Convert.ToString(v));
                }
            }
            // 替换变量
            for (int i = 0; i < result.Count; i++) result[i] = Subst(result[i], ctx);
            return result;
        }
    }
}
