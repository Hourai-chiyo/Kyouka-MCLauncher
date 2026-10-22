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
        public string Source;                // Modrinth / CurseForge（空 = 未识别）
        public long CfModId;                 // CurseForge 项目 id
        public string UrlOverride;           // CurseForge 项目页地址
        public List<string> MissingDeps = new List<string>();
        public List<string> DependencyProjectIds = new List<string>();   // 内部用：Modrinth 要求的依赖项目 id

        public string Url
        {
            get
            {
                if (!string.IsNullOrEmpty(UrlOverride)) return UrlOverride;
                if (string.IsNullOrEmpty(Slug)) return null;
                return "https://modrinth.com/mod/" + Slug;
            }
        }

        public string MissingText
        {
            get { return MissingDeps.Count == 0 ? "" : "依赖未找到：" + string.Join(", ", MissingDeps); }
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
                row.Source = "Modrinth";
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

                // 依赖先记下来，等所有 mod 都识别完再统一判断（否则会误报：先处理的 mod 看不到后处理的）
                foreach (string req in entry.RequiredProjectIds)
                {
                    if (!string.IsNullOrEmpty(req) && !row.DependencyProjectIds.Contains(req))
                        row.DependencyProjectIds.Add(req);
                }
            }

            // 5) CurseForge 兜底：Modrinth 没匹配到的，用文件指纹再试一次（需要 API Key）
            try
            {
                if (CurseForge.HasKey(baseDir))
                {
                    var pending = result.Where(r => string.IsNullOrEmpty(r.ProjectId) && !r.Local.Disabled).ToList();
                    if (pending.Count > 0)
                    {
                        // sha1 → 行（复用前面算过的哈希）
                        var sha1ToRow = new Dictionary<string, ModMatch>(StringComparer.OrdinalIgnoreCase);
                        foreach (var kv in hashToLocal)
                        {
                            var row = result.FirstOrDefault(r => ReferenceEquals(r.Local, kv.Value));
                            if (row != null && string.IsNullOrEmpty(row.ProjectId)) sha1ToRow[kv.Key] = row;
                        }

                        var fps = new List<long>();
                        var fpToRow = new Dictionary<long, ModMatch>();
                        foreach (var row in pending)
                        {
                            try
                            {
                                if (!System.IO.File.Exists(row.Local.FilePath)) continue;
                                long fp = Murmur2.Fingerprint(row.Local.FilePath);
                                if (fpToRow.ContainsKey(fp)) continue;
                                fpToRow[fp] = row;
                                fps.Add(fp);
                            }
                            catch { }
                        }

                        if (fps.Count > 0)
                        {
                            string cfErr;
                            var hits = CurseForge.MatchFingerprints(baseDir, fps, out cfErr);
                            var ids = hits.Select(h => h.ModId).Distinct().ToList();
                            var metas = CurseForge.ModMeta(baseDir, ids, out cfErr);

                            foreach (var hit in hits)
                            {
                                // 按 sha1 对回本地行
                                ModMatch row = null;
                                foreach (string sha in hit.Sha1)
                                {
                                    if (sha1ToRow.TryGetValue(sha, out row)) break;
                                }
                                if (row == null) continue;

                                row.Source = "CurseForge";
                                row.ProjectId = hit.ModId.ToString();
                                row.CfModId = hit.ModId;
                                if (metas.ContainsKey(hit.ModId))
                                {
                                    var m = metas[hit.ModId];
                                    row.Title = string.IsNullOrEmpty(m.Name) ? row.Local.Name : m.Name;
                                    row.Slug = m.Slug;
                                    row.UrlOverride = m.Url;
                                }
                                else
                                {
                                    row.Title = row.Local.Name;
                                }

                                // 最新文件：sha1 不同 → 可更新
                                try
                                {
                                    string lerr;
                                    object latest = CurseForge.LatestFile(baseDir, hit.ModId, mcVersion, loader, out lerr);
                                    if (latest != null)
                                    {
                                        string latestName = J.Str(latest, "displayName");
                                        if (string.IsNullOrWhiteSpace(latestName) || latestName == "-")
                                            latestName = J.Str(latest, "fileName");
                                        if (string.IsNullOrEmpty(latestName))
                                            latestName = J.Str(latest, "fileName");
                                        row.LatestVersion = latestName;
                                        string latestSha1 = null;
                                        foreach (var h in Version.AsList(J.Get(latest, "hashes")))
                                        {
                                            if (J.Int(h, "algo", -1) == 1) { latestSha1 = J.Str(h, "value"); break; }
                                        }
                                        bool same = !string.IsNullOrEmpty(latestSha1) &&
                                                    hit.Sha1.Any(x => string.Equals(x, latestSha1, StringComparison.OrdinalIgnoreCase));
                                        bool newer = false;
                                        if (!same)
                                        {
                                            // CF 的文件名常带版本号，能比就比较；比不了就按"文件不同"算（保守：只有版本更高才提示）
                                            newer = string.IsNullOrEmpty(latestName)
                                                    ? true
                                                    : CompareVersions(latestName, row.Local.Version) > 0;
                                        }
                                        row.HasUpdate = newer;
                                        row.Status = same ? "最新" : (newer ? "可更新" : "最新");
                                    }
                                    else
                                    {
                                        row.Status = "已识别";
                                    }
                                }
                                catch { row.Status = "已识别"; }
                            }
                        }
                    }
                }
            }
            catch { }

            // 6) 统一判断缺依赖：等所有 mod 识别完，再和"已识别集合 + 本地文件名"比对
            try
            {
                var matchedIds = new HashSet<string>(
                    result.Where(r => r.Source == "Modrinth").Select(r => r.ProjectId));

                var depIds = new HashSet<string>();
                foreach (var row in result)
                {
                    if (row.Source != "Modrinth") continue;
                    foreach (string id in row.DependencyProjectIds)
                        if (!matchedIds.Contains(id)) depIds.Add(id);
                }

                if (depIds.Count > 0)
                {
                    var depTitles = new Dictionary<string, string>();
                    var depList = depIds.ToList();
                    for (int i = 0; i < depList.Count; i += 50)
                    {
                        var batch = depList.Skip(i).Take(50).ToList();
                        string ids = "[" + string.Join(",", batch.Select(x => "\"" + x + "\"")) + "]";
                        object arr = Http.GetJson(Api + "/projects?ids=" + Uri.EscapeDataString(ids), 30);
                        foreach (var p in Version.AsList(arr))
                        {
                            string id = J.Str(p, "id");
                            if (!string.IsNullOrEmpty(id)) depTitles[id] = J.Str(p, "title");
                        }
                    }

                    // 本地已有的名字（含 CF 识别的 title / 文件名），用于抑制"其实装了但没匹配到 Modrinth"的误报
                    var localNames = result.SelectMany(r => new[]
                    {
                        r.Local.Name, r.Local.FileName, r.Title,
                    }).Where(x => !string.IsNullOrEmpty(x)).ToList();

                    foreach (var row in result)
                    {
                        if (row.Source != "Modrinth") continue;
                        foreach (string id in row.DependencyProjectIds)
                        {
                            if (matchedIds.Contains(id)) continue;
                            string name;
                            if (!depTitles.TryGetValue(id, out name) || string.IsNullOrEmpty(name)) continue;
                            if (IsProbablyInstalled(name, localNames)) continue;
                            if (!row.MissingDeps.Contains(name)) row.MissingDeps.Add(name);
                        }
                    }
                }
            }
            catch { }

            return result.OrderByDescending(r => r.HasUpdate)
                         .ThenBy(r => r.Status == "未匹配" ? 1 : 0)
                         .ThenBy(r => r.Title ?? r.Local.Name, StringComparer.OrdinalIgnoreCase)
                         .ToList();
        }

        // ---------------- 纯函数（可单测，不联网） ----------------

        /// <summary>
        /// 本地已有？按"依赖名里的有效词根"匹配本地 jar/名称。
        ///
        /// 例：
        ///   "AutoRegLib"        → jar "AutoRegLib-1.3-32.jar"       命中
        ///   "Galacticraft-Legacy" → jar "Galacticraft-1.12.2-4.0.7.jar" 命中（legacy 是限定词，不算）
        ///   "RFTools (Legacy)"  → 需要 rftools，本地没有 → 不命中（真缺）
        /// </summary>
        public static bool IsProbablyInstalled(string depName, IEnumerable<string> localNames)
        {
            if (string.IsNullOrWhiteSpace(depName) || localNames == null) return false;

            var tokens = DepTokens(depName);
            var acronyms = DepAcronyms(depName);
            if (tokens.Count == 0 && acronyms.Count == 0) return false;

            var locals = new List<string>();
            foreach (string raw in localNames)
            {
                if (string.IsNullOrEmpty(raw)) continue;
                locals.Add(Normalize(raw));
            }

            // 缩写命中（如 "Just Enough Items (JEI)" → jei_1.12.2-...jar）
            foreach (string hay in locals)
            {
                foreach (string a in acronyms)
                {
                    if (hay.IndexOf(a, StringComparison.Ordinal) >= 0) return true;
                }
            }

            if (tokens.Count == 0) return false;

            // 全部词根都在同一个本地名字里出现 → 视为已安装
            foreach (string hay in locals)
            {
                bool all = true;
                foreach (string t in tokens)
                {
                    if (hay.IndexOf(t, StringComparison.Ordinal) < 0) { all = false; break; }
                }
                if (all) return true;
            }
            return false;
        }

        private static readonly HashSet<string> DepStopWords = new HashSet<string>(StringComparer.Ordinal)
        {
            "legacy", "forge", "fabric", "neoforge", "quilt", "unofficial", "core", "mod", "mods",
            "api", "lib", "library", "edition", "port", "fork", "the", "for", "and", "continued",
            "reforged", "classic", "lite", "plus", "extra", "addon", "addons", "compat",
        };

        /// <summary>依赖名 → 有效词根（>=4 字符，去掉限定词）。</summary>
        public static List<string> DepTokens(string depName)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(depName)) return list;
            var parts = depName.Split(new[] { ' ', '-', '_', '.', '(', ')', ':', '/', '+', ',' },
                                      StringSplitOptions.RemoveEmptyEntries);
            foreach (string p in parts)
            {
                string t = Normalize(p);
                if (t.Length < 4) continue;
                if (DepStopWords.Contains(t)) continue;
                if (!list.Contains(t)) list.Add(t);
            }
            return list;
        }

        /// <summary>依赖名里的缩写（全大写、2~5 字符），如 JEI / API / TFC。</summary>
        public static List<string> DepAcronyms(string depName)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(depName)) return list;
            var parts = depName.Split(new[] { ' ', '-', '_', '.', '(', ')', ':', '/', '+', ',' },
                                      StringSplitOptions.RemoveEmptyEntries);
            foreach (string p in parts)
            {
                if (p.Length < 2 || p.Length > 5) continue;
                bool allUpper = true;
                foreach (char c in p) if (c < 'A' || c > 'Z') { allUpper = false; break; }
                if (!allUpper) continue;
                string t = Normalize(p);
                if (t.Length >= 2 && !list.Contains(t)) list.Add(t);
            }
            return list;
        }

        private static string Normalize(string s)
        {
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s.ToLowerInvariant())
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) sb.Append(c);
            return sb.ToString();
        }

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
