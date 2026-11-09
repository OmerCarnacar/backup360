using System.Windows;

namespace Backup360.UI;

public partial class AboutPage
{
    public AboutPage()
    {
        InitializeComponent();
        VersionText.Text = $"Sürüm {AppInfo.Version}  ·  Yapı kodu {AppInfo.BuildCode}";
        AuthorText.Text = AppInfo.Author;
        MailLink.Content = AppInfo.Email;
    }

    private void Mail_Click(object sender, RoutedEventArgs e) => Launcher.Open($"mailto:{AppInfo.Email}?subject=Backup360");

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(AppInfo.Email);
        CopyButton.Content = "Kopyalandı";
    }

    private void Link_Click(object sender, RoutedEventArgs e) => Launcher.Open(((FrameworkElement)sender).Tag switch
    {
        "linkedin" => AppInfo.LinkedIn,
        "twitter" => AppInfo.Twitter,
        "github" => AppInfo.GitHub,
        _ => AppInfo.Releases,
    });
}
