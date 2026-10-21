using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Mcroute
{
    /// <summary>
    /// 轻量 JSON 解析/序列化（自己写的递归下降解析器）。
    ///
    /// 为什么不用 JavaScriptSerializer：它在 System.Web.Extensions 里，
    /// 依赖老的 System.Web，WPF 项目的 XAML 编译器会因为它报 MC1000。
    /// 自己实现之后两个项目都干净，调用方式保持不变：
    ///   JSON 对象 -> Dictionary&lt;string, object&gt;，数组 -> object[]。
    /// </summary>
    public static class J
    {
        // ---------------- 解析 ----------------
        public static object Parse(string text)
        {
            if (text == null) return null;
            var parser = new Parser(text);
            object value = parser.ParseValue();
            parser.SkipWhitespace();
            return value;
        }

        public static object ParseFile(string path)
        {
            return Parse(File.ReadAllText(path, Encoding.UTF8));
        }

        // ---------------- 序列化 ----------------
        public static string Serialize(object value)
        {
            var sb = new StringBuilder();
            WriteValue(sb, value);
            return sb.ToString();
        }

        public static void SaveFile(string path, object value)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, Serialize(value), new UTF8Encoding(false));
        }

        private static void WriteValue(StringBuilder sb, object v)
        {
            if (v == null) { sb.Append("null"); return; }

            if (v is string) { WriteString(sb, (string)v); return; }
            if (v is bool) { sb.Append(((bool)v) ? "true" : "false"); return; }

            if (v is int || v is long || v is short || v is byte)
            {
                sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture));
                return;
            }
            if (v is double || v is float || v is decimal)
            {
                sb.Append(Convert.ToDouble(v, CultureInfo.InvariantCulture)
                            .ToString("R", CultureInfo.InvariantCulture));
                return;
            }

            var dict = v as IDictionary<string, object>;
            if (dict != null)
            {
                sb.Append('{');
                bool first = true;
                foreach (var kv in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, kv.Key);
                    sb.Append(':');
                    WriteValue(sb, kv.Value);
                }
                sb.Append('}');
                return;
            }

            var list = v as IEnumerable<object>;
            if (list != null)
            {
                sb.Append('[');
                bool first = true;
                foreach (var item in list)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteValue(sb, item);
                }
                sb.Append(']');
                return;
            }

            WriteString(sb, Convert.ToString(v, CultureInfo.InvariantCulture));
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
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
            sb.Append('"');
        }

        // ---------------- 取值助手（保持原有 API） ----------------
        public static Dictionary<string, object> Obj(object o)
        {
            return o as Dictionary<string, object>;
        }

        public static object Get(object o, string key)
        {
            var d = o as Dictionary<string, object>;
            object v;
            if (d != null && d.TryGetValue(key, out v)) return v;
            return null;
        }

        public static string Str(object o, string key, string fallback = "")
        {
            object v = Get(o, key);
            return v == null ? fallback : Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static int Int(object o, string key, int fallback = 0)
        {
            object v = Get(o, key);
            if (v == null) return fallback;
            try { return Convert.ToInt32(v, CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        public static bool Bool(object o, string key, bool fallback = false)
        {
            object v = Get(o, key);
            if (v == null) return fallback;
            try { return Convert.ToBoolean(v, CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        public static List<object> Arr(object o, string key)
        {
            var list = new List<object>();
            var arr = Get(o, key) as object[];
            if (arr != null) list.AddRange(arr);
            return list;
        }

        public static Dictionary<string, object> Dict(object o, string key)
        {
            return Get(o, key) as Dictionary<string, object>;
        }

        // ---------------- 解析器 ----------------
        private class Parser
        {
            private readonly string _s;
            private int _i;

            public Parser(string s) { _s = s; _i = 0; }

            public void SkipWhitespace()
            {
                while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++;
            }

            public object ParseValue()
            {
                SkipWhitespace();
                if (_i >= _s.Length) throw new FormatException("JSON 意外结束");

                char c = _s[_i];
                switch (c)
                {
                    case '{': return ParseObject();
                    case '[': return ParseArray();
                    case '"': return ParseString();
                    case 't': Expect("true"); return true;
                    case 'f': Expect("false"); return false;
                    case 'n': Expect("null"); return null;
                    default: return ParseNumber();
                }
            }

            private Dictionary<string, object> ParseObject()
            {
                var dict = new Dictionary<string, object>();
                _i++;   // {
                SkipWhitespace();
                if (_i < _s.Length && _s[_i] == '}') { _i++; return dict; }

                while (true)
                {
                    SkipWhitespace();
                    string key = ParseString();
                    SkipWhitespace();
                    if (_i >= _s.Length || _s[_i] != ':') throw new FormatException("JSON 对象缺少 ':'");
                    _i++;
                    dict[key] = ParseValue();
                    SkipWhitespace();
                    if (_i >= _s.Length) throw new FormatException("JSON 对象未闭合");
                    if (_s[_i] == ',') { _i++; continue; }
                    if (_s[_i] == '}') { _i++; break; }
                    throw new FormatException("JSON 对象出现意外字符: " + _s[_i]);
                }
                return dict;
            }

            private object[] ParseArray()
            {
                var list = new List<object>();
                _i++;   // [
                SkipWhitespace();
                if (_i < _s.Length && _s[_i] == ']') { _i++; return list.ToArray(); }

                while (true)
                {
                    list.Add(ParseValue());
                    SkipWhitespace();
                    if (_i >= _s.Length) throw new FormatException("JSON 数组未闭合");
                    if (_s[_i] == ',') { _i++; continue; }
                    if (_s[_i] == ']') { _i++; break; }
                    throw new FormatException("JSON 数组出现意外字符: " + _s[_i]);
                }
                return list.ToArray();
            }

            private string ParseString()
            {
                if (_i >= _s.Length || _s[_i] != '"') throw new FormatException("JSON 字符串应以引号开始");
                _i++;
                var sb = new StringBuilder();
                while (_i < _s.Length)
                {
                    char c = _s[_i++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }

                    if (_i >= _s.Length) break;
                    char esc = _s[_i++];
                    switch (esc)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (_i + 4 > _s.Length) throw new FormatException("JSON \\u 转义不完整");
                            string hex = _s.Substring(_i, 4);
                            _i += 4;
                            sb.Append((char)Convert.ToInt32(hex, 16));
                            break;
                        default: sb.Append(esc); break;
                    }
                }
                throw new FormatException("JSON 字符串未闭合");
            }

            private object ParseNumber()
            {
                int start = _i;
                while (_i < _s.Length && "+-0123456789.eE".IndexOf(_s[_i]) >= 0) _i++;
                string text = _s.Substring(start, _i - start);
                if (text.Length == 0) throw new FormatException("JSON 数字为空");

                if (text.IndexOf('.') < 0 && text.IndexOf('e') < 0 && text.IndexOf('E') < 0)
                {
                    long l;
                    if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out l))
                    {
                        if (l >= int.MinValue && l <= int.MaxValue) return (int)l;
                        return l;
                    }
                }
                double d;
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
                throw new FormatException("无法解析的 JSON 数字: " + text);
            }

            private void Expect(string literal)
            {
                if (_i + literal.Length > _s.Length ||
                    string.CompareOrdinal(_s, _i, literal, 0, literal.Length) != 0)
                    throw new FormatException("JSON 期望 " + literal);
                _i += literal.Length;
            }
        }
    }
}
