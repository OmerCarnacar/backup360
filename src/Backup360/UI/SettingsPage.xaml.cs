using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Backup360.Core;
using Microsoft.Win32;

namespace Backup360.UI;

public partial class SettingsPage
{
    private static readonly string[] Securities = { "auto", "ssl", "starttls", "none" };
    private bool _loading;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Reload();
        foreach (var (key, label, color) in ThemeService.Accents)
        {
            var dot = new Button { Width = 34, Height = 34, Margin = new Thickness(0, 0, 8, 0), ToolTip = label, Padding = new Thickness(0), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Content = new Ellipse { Width = 24, Height = 24, Fill = new SolidColorBrush(color) } };
            dot.Click += (_, _) => ThemeService.Set(ThemeBox.SelectedIndex == 0, key);
            AccentRow.Children.Add(dot);
        }
    }

    private void Reload()
    {
        _loading = true;
        ThemeBox.SelectedIndex = ThemeService.IsDark ? 0 : 1;
        _loading = false;

        Users.ItemsSource = Auth.Users();
        DbPath.Text = AppPaths.Database;
        static string S(string? p) => p != null ? "hazır" : "bulunamadı";
        ToolsText.Text = $"7-Zip: {S(Tools.SevenZip())}   ·   rclone: {S(Tools.Rclone())}   ·   WinRAR: {S(Tools.Rar())}   ·   mysqldump: {S(MySqlEngine.BinDir())}";

        var s = Notifier.Load();
        MailOn.IsChecked = s.Enabled;
        OnWarn.IsChecked = s.OnWarning;
        OnOk.IsChecked = s.OnSuccess;
        Host.Text = s.Host;
        Port.Text = s.Port.ToString();
        Security.SelectedIndex = Math.Max(0, Array.IndexOf(Securities, s.Security));
        SmtpUser.Text = s.User;
        From.Text = s.From;
        To.Text = s.To;
    }

    private void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading) ThemeService.Set(ThemeBox.SelectedIndex == 0, ThemeService.AccentKey);
    }

    private SmtpSettings ReadMail()
    {
        if (!int.TryParse(Port.Text, out var port) || port is < 1 or > 65535) throw new ArgumentException("Port 1-65535 arasında olmalı.");
        return new SmtpSettings
        {
            Enabled = MailOn.IsChecked == true, OnWarning = OnWarn.IsChecked == true, OnSuccess = OnOk.IsChecked == true,
            Host = Host.Text.Trim(), Port = port, Security = Securities[Math.Max(0, Security.SelectedIndex)],
            User = SmtpUser.Text.Trim(), From = From.Text.Trim(), To = To.Text.Trim(),
            PasswordEnc = SmtpPass.Password.Length > 0 ? Secrets.Protect(SmtpPass.Password) : Notifier.Load().PasswordEnc,
        };
    }

    private void SaveMail_Click(object sender, RoutedEventArgs e) => Ui.Guard(() =>
    {
        Notifier.Save(ReadMail());
        SmtpPass.Password = "";
        MailResult.Text = "Kaydedildi.";
        return Task.CompletedTask;
    });

    private async void TestMail_Click(object sender, RoutedEventArgs e)
    {
        MailResult.Text = "Gönderiliyor...";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            await Notifier.SendAsync(ReadMail(), "[Backup360] Test e-postası", $"SMTP ayarlarınız çalışıyor.\nBilgisayar: {Environment.MachineName}", cts.Token);
            MailResult.Text = "Test e-postası gönderildi.";
        }
        catch (Exception x) when (x is not OutOfMemoryException)
        {
            MailResult.Text = x.Message;
        }
    }

    private string? AskPassword(string title)
    {
        var window = new PromptWindow(title, $"En az {Auth.MinPasswordLength} karakter:", "", password: true) { Owner = Ui.Owner(this) };
        return window.ShowDialog() == true ? window.Value : null;
    }

    private void ChangePassword_Click(object sender, RoutedEventArgs e) => Ui.Guard(async () =>
    {
        var p = AskPassword("Yeni parola");
        if (p == null) return;
        Auth.ChangePassword(MainWindow.CurrentUser, p);
        await Ui.Info("Parola değiştirildi.");
    });

    private void AddUser_Click(object sender, RoutedEventArgs e) => Ui.Guard(() =>
    {
        var name = Ui.Prompt(Ui.Owner(this), "Kullanıcı ekle", "Kullanıcı adı:");
        if (string.IsNullOrEmpty(name)) return Task.CompletedTask;
        var p = AskPassword("Parola");
        if (p != null) { Auth.AddUser(name, p); Reload(); }
        return Task.CompletedTask;
    });

    private void DeleteUser_Click(object sender, RoutedEventArgs e) => Ui.Guard(async () =>
    {
        if (Users.SelectedItem is not string name) return;
        if (string.Equals(name, MainWindow.CurrentUser, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Açık oturumdaki kullanıcı silinemez.");
        if (!await Ui.Confirm($"{name} silinsin mi?")) return;
        Auth.DeleteUser(name);
        Reload();
    });

    private void Export_Click(object sender, RoutedEventArgs e) => Ui.Guard(async () =>
    {
        var d = new SaveFileDialog { FileName = $"backup360-{DateTime.Now:yyyyMMdd}.db", Filter = "SQLite veritabanı|*.db" };
        if (d.ShowDialog() != true) return;
        Db.ExportTo(d.FileName);
        await Ui.Info("Veritabanı dışa aktarıldı.");
    });

    private void OpenData_Click(object sender, RoutedEventArgs e) => Process.Start("explorer.exe", AppPaths.DataDir);
}
