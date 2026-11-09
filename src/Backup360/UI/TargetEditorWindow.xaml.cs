using System.Windows;
using System.Windows.Controls;
using Backup360.Core;
using Microsoft.Win32;

namespace Backup360.UI;

/// <summary>Hedef tanımı: tarayıcı, token/anahtar veya şifre ile giriş.</summary>
public partial class TargetEditorWindow
{
    private static readonly (string Key, string Label, (string Key, string Label)[] Auth)[] Types =
    {
        ("drive", "Google Drive", new[] { ("browser", "Tarayıcı ile giriş"), ("token", "Token ile") }),
        ("onedrive", "OneDrive", new[] { ("browser", "Tarayıcı ile giriş") }),
        ("dropbox", "Dropbox", new[] { ("browser", "Tarayıcı ile giriş"), ("token", "Token ile") }),
        ("s3", "S3 uyumlu (AWS, MinIO, Wasabi...)", new[] { ("token", "Access Key / Secret Key") }),
        ("sftp", "SFTP", new[] { ("password", "Şifre ile") }),
        ("ftp", "FTP", new[] { ("password", "Şifre ile") }),
        ("webdav", "WebDAV", new[] { ("password", "Şifre ile"), ("token", "Bearer token ile") }),
        ("smb", "SMB ağ paylaşımı", new[] { ("password", "Şifre ile") }),
        ("alias", "Yerel / ağ klasörü", new[] { ("none", "Giriş gerekmez") }),
    };

    public TargetDefinition Definition { get; private set; } = new();

    public TargetEditorWindow()
    {
        InitializeComponent();
        foreach (var t in Types)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            if (Brand.For(t.Key) is { } logo) row.Children.Add(new Image { Source = logo, Width = 18, Height = 18, Margin = new Thickness(0, 0, 8, 0) });
            row.Children.Add(new TextBlock { Text = t.Label, VerticalAlignment = VerticalAlignment.Center });
            TypeBox.Items.Add(row);
        }
        TypeBox.SelectedIndex = 0;
    }

    private string TypeKey => Types[Math.Max(0, TypeBox.SelectedIndex)].Key;
    private string AuthKey => Types[Math.Max(0, TypeBox.SelectedIndex)].Auth[Math.Max(0, AuthBox.SelectedIndex)].Key;

    private void Type_Changed(object sender, SelectionChangedEventArgs e)
    {
        AuthBox.Items.Clear();
        foreach (var a in Types[TypeBox.SelectedIndex].Auth) AuthBox.Items.Add(a.Label);
        AuthBox.SelectedIndex = 0;
        Sync();
    }

    private void Auth_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (AuthBox.SelectedIndex >= 0) Sync();
    }

    private void Sync()
    {
        var visible = (TypeKey, AuthKey) switch
        {
            ("drive", "browser") => new[] { "clientId", "clientSecret", "fullDrive" },
            ("drive", "token") or ("dropbox", "token") => new[] { "token" },
            ("s3", _) => new[] { "access", "secret", "endpoint", "region" },
            ("sftp", _) or ("smb", _) => new[] { "host", "port", "user", "password" },
            ("ftp", _) => new[] { "host", "port", "user", "password", "tls" },
            ("webdav", "password") => new[] { "url", "user", "password" },
            ("webdav", "token") => new[] { "url", "token" },
            ("alias", _) => new[] { "path" },
            _ => Array.Empty<string>(),
        };
        foreach (var row in new FrameworkElement[] { clientId, clientSecret, fullDrive, token, host, port, url, user, password, tls, access, secret, endpoint, region, path })
            row.Visibility = visible.Contains(row.Name) ? Visibility.Visible : Visibility.Collapsed;

        var hint = (TypeKey, AuthKey) switch
        {
            ("drive", "browser") => "Kaydedince tarayıcıda Google onayı açılır. Client ID boşsa rclone'un 2026 içinde kapanacak ortak kimliği kullanılır.",
            (_, "browser") => "Kaydedince tarayıcıda hesap onayı açılır.",
            ("drive", "token") or ("dropbox", "token") => "\"rclone authorize\" komutunun verdiği JSON token'ı yapıştırın.",
            _ => "",
        };
        Hint.Message = hint;
        Hint.IsOpen = hint.Length > 0;
    }

    private void Pick_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFolderDialog { Title = "Yedeklerin yazılacağı klasör" };
        if (d.ShowDialog(this) == true) PathBox.Text = d.FolderName;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Definition = new TargetDefinition
            {
                Name = NameBox.Text.Trim(), Type = TypeKey, Auth = AuthKey, ClientId = ClientId.Text.Trim(), ClientSecret = ClientSecret.Password.Trim(),
                Token = Token.Text.Trim(), Host = Host.Text.Trim(), Port = (int)(Port.Value ?? 0), Url = Url.Text.Trim(), User = User.Text.Trim(), Password = PasswordBox.Password,
                Tls = Tls.IsChecked == true, FullAccess = FullDrive.IsChecked == true, AccessKey = Access.Text.Trim(), SecretKey = Secret.Password.Trim(), Endpoint = Endpoint.Text.Trim(), Region = Region.Text.Trim(), Path = PathBox.Text.Trim(),
            };
            Definition.Validate();
            DialogResult = true;
        }
        catch (ArgumentException x)
        {
            await Ui.Info(x.Message, "Eksik bilgi");
        }
    }
}
