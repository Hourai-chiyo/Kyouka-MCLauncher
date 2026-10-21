using System;
using System.Windows;
using System.Windows.Media;

namespace Mcroute
{
    /// <summary>
    /// 主题切换（浅色 / 深色 / 跟随系统）。
    ///
    /// 做法：所有颜色都走 DynamicResource，运行时替换 Application.Resources 里的画刷即可，
    /// 不需要重启窗口。键名与 App.xaml 里定义的保持一致。
    /// </summary>
    public static class Theme
    {
        public const string Light = "light";
        public const string Dark = "dark";
        public const string Auto = "auto";

        /// <summary>当前实际生效的主题（"light" / "dark"）。</summary>
        public static string Effective { get; private set; } = Light;

        private static readonly string[] Keys =
        {
            "Bg", "Surface", "Border", "Hairline", "Primary", "PrimaryDim", "PrimaryEdge",
            "Text", "TextSub", "TextMuted", "TextFaint", "CardOverlay", "InputBg", "ScrollTrack", "Hover",
            "SurfaceGlass", "BorderGlass", "InputBgGlass", "FieldBg", "FieldBorder", "PageBg", "PageBgSoft",
        };

        private static readonly string[] LightColors =
        {
            "#F7F8FA", "#FFFFFF", "#E4E7EC", "#EDEFF2", "#2F6FEB", "#EEF4FF", "#CFE0FF",
            "#101828", "#344054", "#667085", "#98A2B3", "#F2FFFFFF", "#FAFBFC", "#EDEFF2", "#FAFBFE",
            "#99FFFFFF", "#66000000", "#B3FFFFFF", "#FFFFFF", "#E4E7EC", "#F7F8FA", "#D9F7F8FA",
        };

        private static readonly string[] DarkColors =
        {
            "#10141A", "#171C24", "#28303C", "#222A35", "#4C8DFF", "#1E2C42", "#3A5C8C",
            "#E9EEF6", "#B7C2D0", "#8B97A8", "#6D7889", "#E6171C24", "#1D232C", "#2A3340", "#1F2733",
            "#99171C24", "#4DFFFFFF", "#B31D232C", "#171C24", "#28303C", "#10141A", "#D910141A",
        };

        /// <summary>应用主题：mode = "light" | "dark" | "auto"。</summary>
        public static void Apply(string mode)
        {
            bool dark = string.Equals(mode, Dark, StringComparison.OrdinalIgnoreCase) ||
                        ((string.IsNullOrEmpty(mode) || string.Equals(mode, Auto, StringComparison.OrdinalIgnoreCase))
                         && IsSystemDark());

            Effective = dark ? Dark : Light;
            var res = Application.Current != null ? Application.Current.Resources : null;
            if (res == null) return;

            string[] colors = dark ? DarkColors : LightColors;
            for (int i = 0; i < Keys.Length; i++)
            {
                try { res[Keys[i]] = Frozen(colors[i]); }
                catch { }
            }
        }

        /// <summary>系统是不是深色（读注册表 AppsUseLightTheme，缺省当浅色）。</summary>
        public static bool IsSystemDark()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (key == null) return false;
                    object v = key.GetValue("AppsUseLightTheme");
                    if (v is int) return (int)v == 0;
                }
            }
            catch { }
            return false;
        }

        private static SolidColorBrush Frozen(string hex)
        {
            Color c = (Color)ColorConverter.ConvertFromString(hex);
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
    }
}
