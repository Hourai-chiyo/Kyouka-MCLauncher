using System;
using System.IO;

namespace Mcroute
{
    /// <summary>Core 里用到的 Trace 垫片（WPF 项目没有 WinForms 的 Program）。</summary>
    public static class Program
    {
        public static void Trace(string msg)
        {
            try
            {
                if (Environment.GetEnvironmentVariable("MCROUTE_TRACE") != "1") return;
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "mcroute-wpf-trace.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + "\r\n");
            }
            catch { }
        }
    }
}
