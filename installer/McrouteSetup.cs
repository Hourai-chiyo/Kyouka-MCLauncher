// mcroute 安装程序（单文件，不需要管理员权限）
//
// 编译方式见 build_installer.bat：
//   csc /target:winexe /resource:payload.zip,payload.zip McrouteSetup.cs
//
// 做的事：
//   1. 把内嵌的 payload.zip 解压到 %LOCALAPPDATA%\mcroute
//   2. 在开始菜单和桌面创建快捷方式（用 wscript 启动，不弹黑窗口）
//   3. 在「设置 → 应用」里登记卸载项
//   4. 启动 mcroute
//
// 支持 /S 静默安装（不弹任何窗口，测试和批量部署用）

using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;

class McrouteSetup
{
    static bool silent = false;
    static readonly string tracePath = Path.Combine(Path.GetTempPath(), "mcroute_setup_trace.log");

    static void Trace(string msg)
    {
        try { File.AppendAllText(tracePath, DateTime.Now.ToString("HH:mm:ss") + "  " + msg + "\r\n", System.Text.Encoding.UTF8); }
        catch { }
    }

    [STAThread]
    static void Main(string[] args)
    {
        foreach (string a in args)
        {
            if (a == "/S" || a == "/s" || a == "-s") silent = true;
        }

        try { File.Delete(tracePath); } catch { }
        Trace("start silent=" + silent + " args=" + string.Join("|", args));

        string target = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "mcroute");
        string exe = Path.Combine(target, "mcroute.exe");
        string vbs = Path.Combine(target, "start-mcroute.vbs");

        if (!silent)
        {
            DialogResult r = MessageBox.Show(
                "将把 mcroute 安装到：\n" + target + "\n\n继续安装吗？",
                "mcroute 安装程序", MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
            if (r != DialogResult.OK) return;
        }

        try
        {
            // ---- 1) 内嵌 payload 落盘到临时文件 ----
            string tmp = Path.Combine(Path.GetTempPath(), "mcroute_payload_" + Guid.NewGuid().ToString("N") + ".zip");

            // 资源名不写死（编译时的命名可能带前缀），找第一个 .zip 就用它
            Assembly asm = Assembly.GetExecutingAssembly();
            string resName = null;
            foreach (string n in asm.GetManifestResourceNames())
            {
                if (n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) { resName = n; break; }
            }
            if (resName == null) throw new Exception("安装包损坏：找不到内嵌的 payload");
            Trace("resource=" + resName);

            using (Stream s = asm.GetManifestResourceStream(resName))
            {
                if (s == null) throw new Exception("安装包损坏：找不到内嵌的 payload");
                using (FileStream f = File.Create(tmp)) { s.CopyTo(f); }
            }

            // ---- 2) 解压（用系统自带的 PowerShell，Win10+ 一定有）----
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.CreateDirectory(target);

            string psArgs = "-NoProfile -ExecutionPolicy Bypass -Command \"Expand-Archive -LiteralPath '" +
                            tmp + "' -DestinationPath '" + target + "' -Force\"";
            ProcessStartInfo psi = new ProcessStartInfo("powershell.exe", psArgs);
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            psi.WindowStyle = ProcessWindowStyle.Hidden;
            Trace("extracting ...");
            Process p = Process.Start(psi);
            p.WaitForExit();
            Trace("powershell exit=" + p.ExitCode);
            try { File.Delete(tmp); } catch { }

            if (!File.Exists(exe))
                throw new Exception("unzip failed: mcroute.exe not found");
            Trace("exe ok, making shortcuts");

            // ---- 3) 快捷方式 ----
            MakeShortcut(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "mcroute.lnk"),
                vbs, target, exe);
            MakeShortcut(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "mcroute.lnk"),
                vbs, target, exe);

            // ---- 4) 卸载信息（设置 → 应用 里能看到）----
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall\mcroute"))
            {
                k.SetValue("DisplayName", "mcroute (Minecraft 启动器)");
                k.SetValue("DisplayVersion", "0.1");
                k.SetValue("Publisher", "mcroute");
                k.SetValue("InstallLocation", target);
                k.SetValue("DisplayIcon", exe);
                k.SetValue("UninstallString", "\"" + Path.Combine(target, "uninstall.bat") + "\"");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }

            // ---- 5) 启动 ----
            Trace("launching app");
            Process.Start(new ProcessStartInfo("wscript.exe", "\"" + vbs + "\""));

            if (!silent)
            {
                MessageBox.Show(
                    "安装完成！\n\n位置：" + target + "\n开始菜单和桌面都已创建快捷方式。",
                    "mcroute", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        catch (Exception ex)
        {
            Trace("FAILED: " + ex.ToString());
            if (!silent)
                MessageBox.Show("安装失败：\n" + ex.Message, "mcroute", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Environment.Exit(1);
        }
    }

    static void MakeShortcut(string lnkPath, string vbs, string workDir, string icon)
    {
        object sh = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
        object lnk = sh.GetType().InvokeMember("CreateShortcut",
            BindingFlags.InvokeMethod, null, sh, new object[] { lnkPath });

        Type t = lnk.GetType();
        t.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { "wscript.exe" });
        t.InvokeMember("Arguments", BindingFlags.SetProperty, null, lnk, new object[] { "\"" + vbs + "\"" });
        t.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { workDir });
        t.InvokeMember("IconLocation", BindingFlags.SetProperty, null, lnk, new object[] { icon + ",0" });
        t.InvokeMember("Description", BindingFlags.SetProperty, null, lnk, new object[] { "mcroute - Minecraft 启动器" });
        t.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
    }
}
