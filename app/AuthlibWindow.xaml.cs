using System;
using System.Windows;
using System.Windows.Input;

namespace Mcroute
{
    /// <summary>外置登录（Yggdrasil / 皮肤站）窗口。</summary>
    public partial class AuthlibWindow : Window
    {
        private readonly string _baseDir;

        public AuthlibWindow(string baseDir)
        {
            InitializeComponent();
            _baseDir = baseDir;

            AuthAccount cur;
            if (Authlib.TryLoad(baseDir, out cur) && cur != null)
            {
                ServerBox.Text = cur.ApiRoot;
                UserBox.Text = cur.Name;
                LogoutBtn.Visibility = Visibility.Visible;
                StatusText.Text = "当前已登录：" + cur.Name +
                                  (string.IsNullOrEmpty(cur.ServerName) ? "" : "（" + cur.ServerName + "）");
            }
        }

        private void CheckServer_Click(object sender, RoutedEventArgs e)
        {
            string root = Authlib.NormalizeRoot(ServerBox.Text);
            if (string.IsNullOrEmpty(root)) { StatusText.Text = "先填服务器地址"; return; }
            StatusText.Text = "正在读取服务器信息…";

            var task = new System.Threading.Tasks.Task(() =>
            {
                string name = Authlib.ServerName(root);
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    StatusText.Text = string.IsNullOrEmpty(name)
                        ? "读不到服务器信息（地址可能不对，或该站没开 Yggdrasil）"
                        : "服务器：" + name + " ✓";
                }));
            });
            task.Start();
        }

        private void Login_Click(object sender, RoutedEventArgs e)
        {
            string root = Authlib.NormalizeRoot(ServerBox.Text);
            string user = UserBox.Text.Trim();
            string pass = PassBox.Password;
            if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass))
            {
                StatusText.Text = "用户名和密码都要填";
                return;
            }

            StatusText.Text = "正在准备 authlib-injector…";
            var task = new System.Threading.Tasks.Task(() =>
            {
                string err;
                string jar = Authlib.EnsureInjector(_baseDir, out err);
                if (jar == null)
                {
                    Dispatcher.BeginInvoke(new Action(() => StatusText.Text = err ?? "准备 injector 失败"));
                    return;
                }

                Dispatcher.BeginInvoke(new Action(() => StatusText.Text = "正在登录…"));
                AuthAccount acct;
                bool ok = Authlib.Login(_baseDir, root, user, pass, out acct, out err);
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!ok)
                    {
                        StatusText.Text = err;
                        return;
                    }
                    StatusText.Text = "登录成功：" + acct.Name;
                    PassBox.Clear();
                    DialogResult = true;
                    Close();
                }));
            });
            task.Start();
        }

        private void Logout_Click(object sender, RoutedEventArgs e)
        {
            Authlib.Clear(_baseDir);
            StatusText.Text = "已退出外置登录（回到离线模式）";
            LogoutBtn.Visibility = Visibility.Collapsed;
            DialogResult = true;
            Close();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }

        private void Close_Click(object sender, RoutedEventArgs e) { Close(); }
    }
}
