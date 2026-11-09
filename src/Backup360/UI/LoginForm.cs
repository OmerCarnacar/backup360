using System.Drawing.Drawing2D;
using Backup360.Core;

namespace Backup360.UI;

public sealed class LoginForm : Form
{
    private readonly bool _firstRun = !Auth.HasUsers();
    private readonly TextBox _user = new() { Width = 300, Font = new Font("Segoe UI", 11f) };
    private readonly TextBox _pass = new() { Width = 300, Font = new Font("Segoe UI", 11f), UseSystemPasswordChar = true };
    private readonly TextBox _pass2 = new() { Width = 300, Font = new Font("Segoe UI", 11f), UseSystemPasswordChar = true };
    private readonly Label _message = new() { AutoSize = false, Width = 300, Height = 40, ForeColor = Theme.Danger, Font = Theme.Small };

    public string UserName => _user.Text.Trim();

    public LoginForm()
    {
        Text = "Backup360";
        Icon = Theme.AppIcon;
        ClientSize = new Size(820, 500);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Surface;
        Font = Theme.Body;

        var brand = new Panel { Dock = DockStyle.Left, Width = 370 };
        Ui.Enable(brand);
        brand.Paint += (_, e) =>
        {
            using var b = new LinearGradientBrush(brand.ClientRectangle, Theme.Sidebar, Theme.Primary, 55f);
            e.Graphics.FillRectangle(b, brand.ClientRectangle);
        };
        var logo = new LogoView { Size = new Size(190, 190), Location = new Point(90, 76) };
        var title = new Label { Text = "Backup360", Font = new Font("Segoe UI Semibold", 25f), ForeColor = Color.White, BackColor = Color.Transparent, AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Bounds = new Rectangle(0, 280, 370, 54) };
        var slogan = new Label { Text = "Sıkıştır · Zamanla · Buluta gönder", Font = Theme.Body, ForeColor = Color.FromArgb(203, 213, 225), BackColor = Color.Transparent, AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Bounds = new Rectangle(0, 334, 370, 26) };
        var by = new Label { Text = AppInfo.Author, Font = Theme.Small, ForeColor = Color.FromArgb(148, 163, 184), BackColor = Color.Transparent, AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Bounds = new Rectangle(0, 458, 370, 24) };
        brand.Controls.AddRange(new Control[] { logo, title, slogan, by });

        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(70, _firstRun ? 44 : 84, 20, 10), BackColor = Theme.Surface };
        flow.Controls.Add(new Label { Text = _firstRun ? "Hesabınızı oluşturun" : "Hoş geldiniz", Font = Theme.Heading, ForeColor = Theme.Text, AutoSize = true, Margin = new Padding(0, 0, 0, 2) });
        flow.Controls.Add(new Label { Text = _firstRun ? "İlk açılış: bu hesap uygulamayı korur." : "Devam etmek için giriş yapın.", ForeColor = Theme.Muted, AutoSize = true, Margin = new Padding(0, 0, 0, 20) });
        flow.Controls.Add(Ui.Label("Kullanıcı adı"));
        flow.Controls.Add(_user);
        flow.Controls.Add(Ui.Label("Parola"));
        flow.Controls.Add(_pass);
        if (_firstRun)
        {
            flow.Controls.Add(Ui.Label("Parola (tekrar)"));
            flow.Controls.Add(_pass2);
        }
        flow.Controls.Add(_message);
        var submit = Ui.Button(_firstRun ? "Hesabı oluştur" : "Giriş yap", true, async (_, _) => await SubmitAsync());
        submit.AutoSize = false;
        submit.Size = new Size(300, 42);
        submit.Font = Theme.Bold;
        flow.Controls.Add(submit);
        AcceptButton = submit;

        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 66, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(70, 4, 10, 0), BackColor = Theme.Surface };
        footer.Controls.Add(new Label { Text = $"Sürüm {AppInfo.Version}   ·   Yapı kodu {AppInfo.BuildCode}", AutoSize = true, Font = Theme.Small, ForeColor = Theme.Muted, Margin = new Padding(0) });
        footer.Controls.Add(Ui.MailRow());
        Controls.Add(flow);
        Controls.Add(footer);
        Controls.Add(brand);
    }

    private async Task SubmitAsync()
    {
        _message.Text = "";
        try
        {
            if (_firstRun)
            {
                if (_pass.Text != _pass2.Text) { _message.Text = "Parolalar aynı değil."; return; }
                Auth.AddUser(_user.Text, _pass.Text);
            }
            var (user, pass) = (_user.Text, _pass.Text);
            var result = await Task.Run(() => Auth.Login(user, pass));
            switch (result)
            {
                case LoginResult.Ok: DialogResult = DialogResult.OK; Close(); break;
                case LoginResult.Locked: _message.Text = "Çok fazla hatalı deneme. 1 dakika sonra tekrar deneyin."; break;
                default: _message.Text = "Kullanıcı adı veya parola hatalı."; break;
            }
        }
        catch (ArgumentException e)
        {
            _message.Text = e.Message;
        }
    }
}
