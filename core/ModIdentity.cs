using System;
using System.Collections.Generic;
using System.Linq;

namespace Mcroute
{
    /// <summary>一个本地 mod 与 Modrinth 项目的对应关系。</summary>
    public class ModMatch
    {
        public ModInfo Local;
        public string ProjectId;
        public string Title;
        public string Slug;
        public string LatestVersion;
        public bool HasUpdate;
        public string Status;                 // 最新 / 可更新 / 未匹配 / 已禁用
        public List<string> MissingDeps = new List<string>();

        public string Url
        {
            get
            {
                if (string.IsNullOrEmpty(Slug)) return null;
                return "https://modrinth.com/mod/" + Slug;
            }
        }

        public string MissingText
        {
            get { return MissingDeps.Count == 0 ? "" : "缺依赖：" + string.Join(", ", MissingDeps); }
        }
    }

    /// <summary>哈希反查结果的一条。</summary>
    public class HashEntry
    {
        public string Hash;
        public string ProjectId;
        public string VersionNumber;
        public string VersionId;
        public List<string> RequiredProjectIds = new List<string>();
    }

    /// <summary>
    /// 本地 mod ↔ 联网 mod 的识别。
    ///
    /// 思路：算每个 jar 的 SHA1，用 Modrinth 的 `POST /v2/version_files` 反查
    /// （同一个文件才命中，不存在"名字像就乱配"），再拉项目信息和最新版本。
    /// 对不上的（自改版 / 非 Modrinth 来源 / CurseForge 独占）单独标"未匹配"。
    /// </summary>
    public static class ModIdentity
    {
        private const string Api = "https://api.modrinth.com/v2";

        /// <summary>
        /// 识别 + 检查更新。
        /// mcVersion 为空时只做识别，不判断更新。
        /// </summary>
        public static List<ModMatch> Match(string baseDir, string ver, string mcVersion, string loader,
                                           out string error)
        {
            error = null;
            var result = new List<ModMatch>();
            var local = Mods.Scan(baseDir, ver);
            if (local.Count == 0) { error = "mods 目录里没有 jar"; return result; }

            // 1) 算 SHA1
            var hashToLocal = new Dictionary<string, ModInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in local)
            {
                try
                {
                    if (!System.IO.File.Exists(m.FilePath)) continue;
                    string sha1 = Http.Sha1File(m.FilePath);
                    if (string.IsNullOrEmpty(sha1)) continue;
                    if (!hashToLocal.ContainsKey(sha1)) hashToLocal[sha1] = m;
                }
                catch { }
            }

            // 先全部标未匹配
            foreach (var m in local)
                result.Add(new ModMatch { Local = m, Status = m.Disabled ? "已禁用" : "未匹配" });

            if (hashToLocal.Count == 0) { error = "算不出文件哈希"; return result; }

            // 2) 批量哈希反查（一次最多 100 个左右，分批）
            var lookup = new Dictionary<string, HashEntry>(StringComparer.OrdinalIgnoreCase);
            var hashes = hashToLocal.Keys.ToList();
            for (int i = 0; i < hashes.Count; i += 80)
            {
                var batch = hashes.Skip(i).Take(80).ToList();
                string body = "{\"hashes\":[" + string.Join(",", batch.Select(h => "\"" + h + "\"")) +
                              "],\"algorithm\":\"sha1\"}";
                string text = Http.PostJson(Api + "/version_files", body, 30);
                if (string.IsNullOrEmpty(text)) continue;
                foreach (var e in ParseHashLookup(text))
                {
                    if (!string.IsNullOrEmpty(e.Hash)) lookup[e.Hash] = e;
                }
            }

            if (lookup.Count == 0) { error = "Modrinth 没有匹配到任何本地文件（可能都没上传过 / 网络失败）"; return result; }

            // 3) 项目信息
            var projectIds = lookup.Values.Select(v => v.ProjectId).Where(s => !string.IsNullOrEmpty(s))
                                          .Distinct().ToList();
            var titles = new Dictionary<string, string>();
            var slugs = new Dictionary<string, string>();
            for (int i = 0; i < projectIds.Count; i += 50)
            {
                var batch = projectIds.Skip(i).Take(50).ToList();
                string ids = "[" + string.Join(",", batch.Select(x => "\"" + x + "\"")) + "]";
                object arr = Http.GetJson(Api + "/projects?ids=" + Uri.EscapeDataString(ids), 30);
                foreach (var p in Version.AsList(arr))
                {
                    string id = J.Str(p, "id");
                    if (string.IsNullOrEmpty(id)) continue;
                    titles[id] = J.Str(p, "title");
                    slugs[id] = J.Str(p, "slug");
                }
            }

            // 4) 最新版本（按 mc + loader 过滤）
            var matchedProjectIds = new HashSet<string>();
            foreach (var kv in hashToLocal)
            {
                HashEntry entry;
                if (!lookup.TryGetValue(kv.Key, out entry)) continue;
                if (string.IsNullOrEmpty(entry.ProjectId)) continue;

                var row = result.FirstOrDefault(r => ReferenceEquals(r.Local, kv.Value));
                if (row == null) continue;

                matchedProjectIds.Add(entry.ProjectId);
                row.ProjectId = entry.ProjectId;
                row.Title = titles.ContainsKey(entry.ProjectId) ? titles[entry.ProjectId] : row.Local.Name;
                row.Slug = slugs.ContainsKey(entry.ProjectId) ? slugs[entry.ProjectId] : null;

                string latest = null;
                if (!string.IsNullOrEmpty(mcVersion))
                {
                    try
                    {
                        var versions = Modrinth.Versions(entry.ProjectId, mcVersion, loader, out _);
                        latest = PickLatestVersion(versions);
                    }
                    catch { }
                }
                row.LatestVersion = latest;

                if (string.IsNullOrEmpty(latest))
                {
                    row.Status = row.Local.Disabled ? "已禁用" : "已识别";
                }
                else if (!HasDigits(row.Local.Version))
                {
                    // ${mod_version} / ${file.jarVersion} 这类占位符：判断不了新旧
                    row.Status = row.Local.Disabled ? "已禁用" : "已识别";
                }
                else if (CompareVersions(latest, row.Local.Version) > 0)
                {
                    row.HasUpdate = true;
                    row.Status = "可更新";
                }
                else
                {
                    row.Status = "最新";
                }

                // 依赖：只报告"Modrinth 认为必需、但本地没匹配到"的
                foreach (string req in entry.RequiredProjectIds)
                {
                    if (string.IsNullOrEmpty(req) || matchedProjectIds.Contains(req)) continue;
                    string name = titles.ContainsKey(req) ? titles[req] : null;
                    if (!string.IsNullOrEmpty(name) && !row.MissingDeps.Contains(name))
                        row.MissingDeps.Add(name);
                }
            }

            return result.OrderByDescending(r => r.HasUpdate)
                         .ThenBy(r => r.Status == "未匹配" ? 1 : 0)
                         .ThenBy(r => r.Title ?? r.Local.Name, StringComparer.OrdinalIgnoreCase)
                         .ToList();
        }

        // ---------------- 纯函数（可单测，不联网） ----------------

        /// <summary>解析 POST /version_files 的返回：sha1 → 项目/版本/必需依赖。</summary>
        public static List<HashEntry> ParseHashLookup(string json)
        {
            var list = new List<HashEntry>();
            if (string.IsNullOrEmpty(json)) return list;
            object root;
            try { root = J.Parse(json); }
            catch { return list; }

            var dict = root as Dictionary<string, object>;
            if (dict == null) return list;

            foreach (var kv in dict)
            {
                var e = new HashEntry { Hash = kv.Key };
                e.ProjectId = J.Str(kv.Value, "project_id");
                e.VersionNumber = J.Str(kv.Value, "version_number");
                e.VersionId = J.Str(kv.Value, "id");
                foreach (var dep in Version.AsList(J.Get(kv.Value, "dependencies")))
                {
                    if (J.Str(dep, "dependency_type") != "required") continue;
                    string pid = J.Str(dep, "project_id");
                    if (!string.IsNullOrEmpty(pid)) e.RequiredProjectIds.Add(pid);
                }
                list.Add(e);
            }
            return list;
        }

        /// <summary>从版本数组里挑最新的 version_number（按 date_published，取不到就按顺序）。</summary>
        public static string PickLatestVersion(List<object> versions)
        {
            if (versions == null || versions.Count == 0) return null;
            object best = null;
            string bestDate = null;
            foreach (var v in versions)
            {
                string d = J.Str(v, "date_published");
                if (best == null || (d != null && (bestDate == null || string.CompareOrdinal(d, bestDate) > 0)))
                {
                    best = v;
                    bestDate = d;
                }
            }
            return best == null ? null : J.Str(best, "version_number");
        }

        /// <summary>
        /// 比较版本号。返回 &gt;0 表示 a 比 b 新，&lt;0 更旧，0 视为相同/无法判断。
        ///
        /// 处理真实世界的一堆写法：
        ///   1.2.0            vs 1.2.0-forge-1.12.2   → 相同（前缀）
        ///   1.12.2-3.14.0    vs 3.14.0               → 相同（后缀）
        ///   1.0.14           vs 1.0.14+mc1.12        → 相同（前缀）
        ///   1.12.3           vs 1.12.7.23            → 有更新
        ///   ${mod_version}   / 空 / 无数字            → 视为无法判断（0，不报更新）
        /// </summary>
        public static int CompareVersions(string a, string b)
        {
            var na = NumericCore(a);
            var nb = NumericCore(b);
            if (na.Count == 0 || nb.Count == 0) return 0;      // 占位符/无版本号 → 不判断

            if (SameCore(na, nb)) return 0;                     // 前缀/后缀/完全相同

            int n = Math.Min(na.Count, nb.Count);
            for (int i = 0; i < n; i++)
            {
                if (na[i] != nb[i]) return na[i] > nb[i] ? 1 : -1;
            }
            return 0;
        }

        private static bool HasDigits(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (char c in s) if (c >= '0' && c <= '9') return true;
            return false;
        }

        /// <summary>提取版本号里的数字段（忽略 1.12.2-Forge、+mc1.12、beta、${...} 等）。</summary>
        private static List<int> NumericCore(string v)
        {
            var list = new List<int>();
            if (string.IsNullOrEmpty(v)) return list;
            var parts = v.Split(new[] { '.', '-', '+', '_', ' ', '(', ')' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string part in parts)
            {
                int n;
                if (int.TryParse(part, out n))
                {
                    list.Add(n);
                    continue;
                }
                // 形如 "1.2.0" 被拆成的单段没数字，跳过；形如 "0.1.0b" 取前面的数字
                var m = System.Text.RegularExpressions.Regex.Match(part, @"^(\d+)");
                if (m.Success && int.TryParse(m.Groups[1].Value, out n)) list.Add(n);
            }
            return list;
        }

        private static bool SameCore(List<int> a, List<int> b)
        {
            if (a.Count == b.Count)
            {
                for (int i = 0; i < a.Count; i++) if (a[i] != b[i]) return false;
                return true;
            }
            // 短的是长者的前缀或后缀 → 视为同一版本（只是写法不同）
            var shorter = a.Count < b.Count ? a : b;
            var longer = a.Count < b.Count ? b : a;
            bool prefix = true, suffix = true;
            for (int i = 0; i < shorter.Count; i++)
            {
                if (longer[i] != shorter[i]) { prefix = false; break; }
            }
            for (int i = 0; i < shorter.Count; i++)
            {
                if (longer[longer.Count - shorter.Count + i] != shorter[i]) { suffix = false; break; }
            }
            return prefix || suffix;
        }
    }
}
