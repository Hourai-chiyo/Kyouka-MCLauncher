using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace Mcroute
{
    /// <summary>
    /// JSON 小工具：.NET Framework 4.8 自带 JavaScriptSerializer 就够了（不引 NuGet）。
    /// JSON 对象 -> Dictionary&lt;string, object&gt;，数组 -> object[]。
    /// </summary>
    public static class J
    {
        private static readonly JavaScriptSerializer Ser = new JavaScriptSerializer
        {
            MaxJsonLength = int.MaxValue,
            RecursionLimit = 200,
        };

        public static object Parse(string text)
        {
            return Ser.DeserializeObject(text);
        }

        public static object ParseFile(string path)
        {
            return Parse(File.ReadAllText(path, Encoding.UTF8));
        }

        public static string Serialize(object value)
        {
            return Ser.Serialize(value);
        }

        public static void SaveFile(string path, object value)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, Serialize(value), new UTF8Encoding(false));
        }

        // ---------- 取值助手 ----------
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
    }
}
