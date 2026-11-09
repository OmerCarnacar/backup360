using System.Windows;
using Backup360.Core;

namespace Backup360.UI;

public partial class LoginWindow
{
    private readonly bool _firstRun = !Auth.HasUsers();

    public string UserName => UserBox.Text.Trim();

    public LoginWindow()
    {
        InitializeComponent();
        VersionText.Text = $"Sürüm {AppInfo.Version}   ·   Yapı kodu {AppInfo.BuildCode}";
        MailLink.Content = AppInfo.Email;
        if (_firstRun)
        {
            Heading.Text = "Hesabınızı oluşturun";
            SubHeading.Text = "İlk açılış: bu hesap uygulamayı korur.";
            Pass2Label.Visibility = Pass2Box.Visibility = Visibility.Visible;
            Submit.Content = "Hesabı oluştur";
        }
        Loaded += (_, _) => UserBox.Focus();
    }

    private async void Submit_Click(object sender, RoutedEventArgs e)
    {
        Message.Text = "";
        try
        {
            if (_firstRun)
            {
                if (PassBox.Password != Pass2Box.Password) { Message.Text = "Parolalar aynı değil."; return; }
                Auth.AddUser(UserBox.Text, PassBox.Password);
            }
            var (user, pass) = (UserBox.Text, PassBox.Password);
            Submit.IsEnabled = false;
            var result = await Task.Run(() => Auth.Login(user, pass));
            Submit.IsEnabled = true;
            switch (result)
            {
                case LoginResult.Ok: DialogResult = true; break;
                case LoginResult.Locked: Message.Text = "Çok fazla hatalı deneme. 1 dakika sonra tekrar deneyin."; break;
                default: Message.Text = "Kullanıcı adı veya parola hatalı."; break;
            }
        }
        catch (ArgumentException x)
        {
            Message.Text = x.Message;
        }
    }

    private void Mail_Click(object sender, RoutedEventArgs e) => Launcher.Open($"mailto:{AppInfo.Email}?subject=Backup360");

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(AppInfo.Email);
        CopyButton.Content = "Kopyalandı";
    }
}
