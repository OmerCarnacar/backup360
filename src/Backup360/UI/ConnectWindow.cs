using System.IO;
using System.Windows;
using System.Windows.Controls;
using Backup360.Core;
using WUI = Wpf.Ui.Controls;

namespace Backup360.UI;

/// <summary>Hedef tanımlanırken hesap onay sayfasını açar, onayı bekler ve hatayı gösterir.</summary>
public sealed class ConnectWindow : WUI.FluentWindow
{
    private readonly TargetDefinition _definition;
    private readonly CancellationTokenSource _cts = new();
    private readonly WUI.Button _link = new() { Content = "Onay sayfasını yeniden aç", Visibility = Visibility.Collapsed, MinWidth = 200 };
    private string? _url;

    public string? Error { get; private set; }

    public ConnectWindow(TargetDefinition definition)
    {
        _definition = definition;
        Title = "Hedef tanımlanıyor";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = WUI.WindowBackdropType.Mica;

        var root = new StackPanel();
        root.Children.Add(new WUI.TitleBar { Title = Title, ShowMaximize = false, ShowMinimize = false, ShowClose = false });
        var body = new StackPanel { Margin = new Thickness(28, 16, 28, 22) };
        body.Children.Add(new WUI.ProgressRing { IsIndeterminate = true, HorizontalAlignment = HorizontalAlignment.Center });
        body.Children.Add(new TextBlock
        {
            Text = definition.Auth == "browser" ? "Onay sayfası tarayıcınızda açılıyor. Hesabınızı seçip izin verin; onay gelince bu pencere kendiliğinden kapanır." : "Hedef kaydediliyor...",
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 16, 0, 8),
        });
        _link.HorizontalAlignment = HorizontalAlignment.Center;
        _link.Click += (_, _) => OpenPage();
        body.Children.Add(_link);
        var cancel = new WUI.Button { Content = "İptal", HorizontalAlignment = HorizontalAlignment.Center, MinWidth = 110, Margin = new Thickness(0, 10, 0, 0) };
        cancel.Click += (_, _) => _cts.Cancel();
        body.Children.Add(cancel);
        root.Children.Add(body);
        Content = root;
        Loaded += async (_, _) => await RunAsync();
    }

    private void OnUrl(string url) => Dispatcher.Invoke(() =>
    {
        _url = url;
        _link.Visibility = Visibility.Visible;
        OpenPage();
    });

    private void OpenPage()
    {
        if (_url == null) return;
        try { Launcher.Open(_url); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Clipboard.SetText(_url);
            _link.Content = "Tarayıcı açılamadı, adres panoya kopyalandı";
        }
    }

    private async Task RunAsync()
    {
        try
        {
            await Cloud.CreateTargetAsync(_definition, _cts.Token, OnUrl);
        }
        catch (OperationCanceledException)
        {
            Error = "İşlem iptal edildi; hedef kaydedilmedi.";
        }
        catch (Exception e) when (e is IOException or ArgumentException or InvalidOperationException or System.Net.Http.HttpRequestException)
        {
            Error = Cloud.Explain(e.Message);
            new RunLog("Hedef tanımlama", null).Write($"HATA ({_definition.Type}): {e.Message}");
        }
        Close();
    }
}
