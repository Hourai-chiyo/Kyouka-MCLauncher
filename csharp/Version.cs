using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace Mcroute
{
    /// <summary>版本 json 的读取 / inheritsFrom 合并 / rules 判定 / 变量替换。</summary>
    public static class Version
    {
        public static string BaseDir(string argBase)
        {
            if (!string.IsNullOrEmpty(argBase)) return Path.GetFullPath(argBase);

            // 1) 便携版：exe 旁边已经有 mcdata\versions 就用它
            string exeDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            string beside = Path.Combine(exeDir, "mcdata");
            if (Directory.Exists(Path.Combine(beside, "versions")))
            {
                try
                {
                    string probe = Path.Combine(beside, "write_test.tmp");
                    File.WriteAllText(probe, "ok");
                    File.Delete(probe);
                    return beside;
                }
                catch { }
            }

            // 2) 否则用用户目录（安装版 / 只读目录都适用）
            string user = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "mcroute", "mcdata");
            Directory.CreateDirectory(user);
            return user;
        }

        public static string VersionJsonPath(string baseDir, string ver)
        {
            return Path.Combine(baseDir, "versions", ver, ver + ".json");
        }

        /// <summary>读版本 json 并处理 inheritsFrom（递归合并）。失败返回 null。</summary>
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
