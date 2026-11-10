using System.IO;
using System.Windows;
using System.Windows.Controls;
using Backup360.Core;
using Microsoft.Win32;
using WUI = Wpf.Ui.Controls;

namespace Backup360.UI;

/// <summary>Hedef tanımı: tarayıcı, token/anahtar veya şifre ile giriş. Her tür için gereken sayfa tek tıkla açılır.</summary>
public partial class TargetEditorWindow
{
    private static readonly (string Key, string Label, (string Key, string Label)[] Auth)[] Types =
    {
        ("drive", "Google Drive", new[] { ("browser", "Tarayıcı ile giriş"), ("token", "Token ile") }),
        ("onedrive", "OneDrive", new[] { ("browser", "Tarayıcı ile giriş"), ("token", "Token ile") }),
        ("dropbox", "Dropbox", new[] { ("browser", "Tarayıcı ile giriş"), ("token", "Token ile") }),
        ("s3", "S3 uyumlu (AWS, MinIO, Wasabi...)", new[] { ("token", "Access Key / Secret Key") }),
        ("sftp", "SFTP", new[] { ("password", "Şifre ile") }),
        ("ftp", "FTP", new[] { ("password", "Şifre ile") }),
        ("webdav", "WebDAV", new[] { ("password", "Şifre ile"), ("token", "Bearer token ile") }),
        ("smb", "SMB ağ paylaşımı", new[] { ("password", "Şifre ile") }),
        ("alias", "Yerel / ağ klasörü", new[] { ("none", "Giriş gerekmez") }),
    };

    /// <summary>Kullanıcının anahtar veya uygulama kimliği alacağı sayfalar.</summary>
    public static readonly IReadOnlyDictionary<string, (string Label, string Url)[]> Pages = new Dictionary<string, (string, string)[]>
    {
        ["drive"] = new[]
        {
            ("1. Drive API'yi etkinleştir", "https://console.cloud.google.com/apis/library/drive.googleapis.com"),
            ("2. Client ID oluştur", "https://console.cloud.google.com/apis/credentials"),
        },
        ["onedrive"] = new[] { ("Azure'da uygulama kaydet", "https://portal.azure.com/#view/Microsoft_AAD_RegisteredApps/ApplicationsListBlade") },
        ["dropbox"] = new[] { ("Dropbox'ta uygulama oluştur", "https://www.dropbox.com/developers/apps/create") },
        ["s3"] = new[] { ("AWS erişim anahtarı oluştur", "https://console.aws.amazon.com/iam/home#/security_credentials") },
    };

    private CancellationTokenSource? _tokenWait;

    public TargetDefinition Definition { get; private set; } = new();

    public TargetEditorWindow()
    {
        InitializeComponent();
        Ui.FitToScreen(this);
        foreach (var t in Types)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            if (Brand.For(t.Key) is { } logo) row.Children.Add(new Image { Source = logo, Width = 18, Height = 18, Margin = new Thickness(0, 0, 8, 0) });
            row.Children.Add(new TextBlock { Text = t.Label, VerticalAlignment = VerticalAlignment.Center });
            TypeBox.Items.Add(row);
        }
        UseOwnClient.IsChecked = !GoogleClient.HasBuiltIn;
        TypeBox.SelectedIndex = 0;
        _watch.Tick += (_, _) => CheckDownload();
        Closed += (_, _) =>
        {
            _tokenWait?.Cancel();
            _watch.Stop();
        };
    }

    private readonly System.Windows.Threading.DispatcherTimer _watch = new() { Interval = TimeSpan.FromSeconds(2) };
    private DateTime _watchSince;

    private void Status(string text)
    {
        ClientStatus.Text = text;
        ClientStatus.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Google sayfası açıldıktan sonra inen kimlik dosyasını bekler; kullanıcı kopyala-yapıştır yapmaz.</summary>
    private void WatchDownloads()
    {
        if (!_watch.IsEnabled) _watchSince = DateTime.UtcNow;
        _watch.Start();
        Status("Client ID'yi oluşturunca \"JSON'u indir\"e basın; dosya indiği anda alanlar kendiliğinden dolar.");
    }

    private void CheckDownload()
    {
        if (DateTime.UtcNow - _watchSince > TimeSpan.FromMinutes(30)) { _watch.Stop(); return; }
        if (GoogleClient.FindNewDownload(GoogleClient.DownloadsFolder(), _watchSince) is { } file) UseJson(file);
    }

    private void UseJson(string file)
    {
        if (GoogleClient.ReadJson(file) is not { } client)
        {
            Status("Bu dosya bir Google kimlik dosyası değil.");
            return;
        }
        if (!client.Desktop)
        {
            _watchSince = DateTime.UtcNow;
            Status("İnen kimlik \"Web uygulaması\" türünde. Tür olarak \"Masaüstü uygulaması\" seçip yeniden oluşturun.");
            return;
        }
        _watch.Stop();
        UseOwnClient.IsChecked = true;
        ClientId.Text = client.Id;
        ClientSecret.Password = client.Secret;
        Sync();
        Status("Client ID indirilen dosyadan alındı. \"Kaydet ve giriş yap\"a basın.");
        Activate();
    }

    private void PickJson()
    {
        var d = new OpenFileDialog { Title = "Google kimlik dosyası", Filter = "Google kimlik dosyası|client_secret*.json;*.json", InitialDirectory = GoogleClient.DownloadsFolder() };
        if (d.ShowDialog(this) == true) UseJson(d.FileName);
    }

    private void Own_Changed(object sender, RoutedEventArgs e)
    {
        if (IsInitialized && TypeBox.SelectedIndex >= 0) Sync();
    }

    private string TypeKey => Types[Math.Max(0, TypeBox.SelectedIndex)].Key;
    private string AuthKey => Types[Math.Max(0, TypeBox.SelectedIndex)].Auth[Math.Max(0, AuthBox.SelectedIndex)].Key;
    private bool OwnGoogle => UseOwnClient.IsChecked == true || !GoogleClient.HasBuiltIn;

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
        var account = Cloud.UsesAccount(TypeKey);
        var visible = new List<string>();
        switch (TypeKey)
        {
            case "drive":
                if (GoogleClient.HasBuiltIn) visible.Add("customClient");
                if (OwnGoogle) visible.AddRange(new[] { "clientId", "clientSecret", "fullDrive" });
                break;
            case "onedrive" or "dropbox": visible.Add("clientId"); break;
            case "s3": visible.AddRange(new[] { "access", "secret", "path", "endpoint", "region" }); break;
            case "sftp": visible.AddRange(new[] { "host", "port", "user", "password" }); break;
            case "ftp": visible.AddRange(new[] { "host", "port", "user", "password", "tls" }); break;
            case "smb": visible.AddRange(new[] { "host", "path", "user", "password" }); break;
            case "webdav": visible.AddRange(AuthKey == "password" ? new[] { "url", "user", "password" } : new[] { "url", "token" }); break;
            case "alias": visible.Add("path"); break;
        }
        if (account && AuthKey == "token") visible.Add("token");
        foreach (var row in new FrameworkElement[] { customClient, clientId, clientSecret, fullDrive, token, host, port, url, user, password, tls, access, secret, endpoint, region, path })
            row.Visibility = visible.Contains(row.Name) ? Visibility.Visible : Visibility.Collapsed;

        tokenRow.Visibility = account ? Visibility.Visible : Visibility.Collapsed;
        ClientIdLabel.Text = TypeKey switch { "onedrive" => "Uygulama (istemci) kimliği", "dropbox" => "App key", _ => "Client ID" };
        PathLabel.Text = TypeKey switch { "s3" => "Bucket adı", "smb" => "Paylaşım adı (örn: Yedekler)", _ => "Klasör" };
        PickButton.Visibility = TypeKey == "alias" ? Visibility.Visible : Visibility.Collapsed;
        SaveButton.Content = account && AuthKey == "browser" ? "Kaydet ve giriş yap" : "Kaydet";

        Links.Children.Clear();
        if (Pages.TryGetValue(TypeKey, out var pages) && (TypeKey != "drive" || OwnGoogle))
            foreach (var (label, address) in pages)
            {
                var button = new WUI.Button { Content = label, Icon = new WUI.SymbolIcon(WUI.SymbolRegular.Open24), Margin = new Thickness(0, 0, 8, 6), ToolTip = address };
                button.Click += (_, _) =>
                {
                    Launcher.Open(address);
                    if (TypeKey == "drive") WatchDownloads();
                };
                Links.Children.Add(button);
            }
        if (TypeKey == "drive" && OwnGoogle)
        {
            var pick = new WUI.Button { Content = "İndirilen dosyadan al", Icon = new WUI.SymbolIcon(WUI.SymbolRegular.DocumentArrowDown20), Margin = new Thickness(0, 0, 8, 6) };
            pick.Click += (_, _) => PickJson();
            Links.Children.Add(pick);
        }
        else
        {
            _watch.Stop();
            Status("");
        }

        var hint = (TypeKey, AuthKey) switch
        {
            ("drive", _) when !OwnGoogle => "\"Kaydet ve giriş yap\"a basın: Google sayfası açılır, hesabınızı seçip izin verin. Onay kendiliğinden algılanır, başka bir şey gerekmez.",
            ("drive", _) => "Aşağıdaki iki düğmeyle Drive API'yi açın ve \"Masaüstü uygulaması\" türünde bir Client ID oluşturup JSON dosyasını indirin. Dosya indiği anda alanlar kendiliğinden dolar; sonra \"Kaydet ve giriş yap\"a basın.",
            ("onedrive", _) => $"Azure'da bir uygulama kaydedin. Platform olarak \"Mobil ve masaüstü uygulamaları\"nı seçip yönlendirme adresine {OAuth.FixedRedirect} yazın. Uygulama (istemci) kimliğini aşağıya girin.",
            ("dropbox", _) => $"Dropbox'ta \"Scoped access\" ve \"Full Dropbox\" seçerek uygulama oluşturun. Permissions sekmesinde files.content.write ve files.content.read'i işaretleyin, Redirect URI olarak {OAuth.FixedRedirect} ekleyin. App key'i aşağıya girin.",
            ("s3", _) => "Aşağıdaki düğme AWS'de erişim anahtarı sayfasını açar. Başka sağlayıcılarda (MinIO, Wasabi) anahtarı kendi panelinizden alıp Endpoint alanını doldurun.",
            ("smb", _) => "Sunucu adını veya IP adresini, paylaşım adını ve o paylaşıma yazabilen kullanıcıyı girin.",
            _ => "",
        };
        if (account && AuthKey == "token") hint += " \"Token al\" onay sayfasını açar ve token'ı alana kendisi yazar; başka bilgisayarda alınmış bir token'ı da yapıştırabilirsiniz.";
        Hint.Message = hint;
        Hint.IsOpen = hint.Length > 0;
    }

    private void Pick_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFolderDialog { Title = "Yedeklerin yazılacağı klasör" };
        if (d.ShowDialog(this) == true) PathBox.Text = d.FolderName;
    }

    private TargetDefinition Read()
    {
        var ownClient = TypeKey != "drive" || OwnGoogle;
        return new TargetDefinition
        {
            Name = NameBox.Text.Trim(), Type = TypeKey, Auth = AuthKey, ClientId = ownClient ? ClientId.Text.Trim() : "", ClientSecret = ownClient && TypeKey == "drive" ? ClientSecret.Password.Trim() : "",
            Token = Token.Text.Trim(), Host = Host.Text.Trim(), Port = (int)(Port.Value ?? 0), Url = Url.Text.Trim(), User = User.Text.Trim(), Password = PasswordBox.Password,
            Tls = Tls.IsChecked == true, FullAccess = ownClient && FullDrive.IsChecked == true, AccessKey = Access.Text.Trim(), SecretKey = Secret.Password.Trim(), Endpoint = Endpoint.Text.Trim(), Region = Region.Text.Trim(), Path = PathBox.Text.Trim(),
        };
    }

    /// <summary>Onay sayfasını açar, dönen token'ı alana yazar.</summary>
    private async void GetToken_Click(object sender, RoutedEventArgs e)
    {
        if (_tokenWait != null) return;
        _tokenWait = new CancellationTokenSource();
        TokenButton.IsEnabled = false;
        TokenStatus.Text = "Tarayıcıda onay bekleniyor...";
        try
        {
            var definition = Read();
            definition.Auth = "browser";
            if (definition.Name.Length == 0) definition.Name = "gecici";
            definition.Validate();
            Token.Text = await Cloud.AuthorizeAsync(definition, Launcher.Open, _tokenWait.Token);
            TokenStatus.Text = "Token alındı. Kaydet'e basın.";
            Activate();
        }
        catch (OperationCanceledException) { TokenStatus.Text = ""; }
        catch (Exception x) when (x is ArgumentException or IOException or System.Net.Http.HttpRequestException or System.ComponentModel.Win32Exception)
        {
            TokenStatus.Text = "";
            await Ui.Info(Cloud.Explain(x.Message), "Token alınamadı");
        }
        finally
        {
            _tokenWait.Dispose();
            _tokenWait = null;
            TokenButton.IsEnabled = true;
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Definition = Read();
            Definition.Validate();
            DialogResult = true;
        }
        catch (ArgumentException x)
        {
            await Ui.Info(x.Message, "Eksik bilgi");
        }
    }
}
