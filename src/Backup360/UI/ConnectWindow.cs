using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using Backup360.Core;
using WUI = Wpf.Ui.Controls;

namespace Backup360.UI;

/// <summary>Hedef tanımlanırken ilerlemeyi, tarayıcı bağlantısını ve hatayı gösterir.</summary>
public sealed class ConnectWindow : WUI.FluentWindow
{
    private static readonly Regex AuthLink = new(@"http://127\.0\.0\.1:\d+/auth\S*", RegexOptions.Compiled);
    private readonly TargetDefinition _definition;
    private readonly CancellationTokenSource _cts = new();
    private readonly WUI.HyperlinkButton _link = new() { Content = "Tarayıcı açılmadıysa buraya tıklayın", Visibility = Visibility.Collapsed, Padding = new Thickness(0, 4, 0, 4) };
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
            Text = definition.Auth == "browser" ? "Tarayıcıda açılan sayfadan hesabınızı seçip izin verin. Onay gelene kadar bu pencere açık kalır." : "Hedef kaydediliyor...",
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 16, 0, 8),
        });
        _link.HorizontalAlignment = HorizontalAlignment.Center;
        _link.Click += (_, _) => { if (_url != null) Launcher.Open(_url); };
        body.Children.Add(_link);
        var cancel = new WUI.Button { Content = "İptal", HorizontalAlignment = HorizontalAlignment.Center, MinWidth = 110, Margin = new Thickness(0, 10, 0, 0) };
        cancel.Click += (_, _) => _cts.Cancel();
        body.Children.Add(cancel);
        root.Children.Add(body);
        Content = root;
        Loaded += async (_, _) => await RunAsync();
    }

    private void OnLine(string line)
    {
        var match = AuthLink.Match(line);
        if (!match.Success) return;
        Dispatcher.Invoke(() =>
        {
            _url = match.Value;
            _link.Visibility = Visibility.Visible;
        });
    }

    private async Task RunAsync()
    {
        try
        {
            await Cloud.CreateTargetAsync(_definition, _cts.Token, OnLine);
        }
        catch (OperationCanceledException)
        {
            Error = "İşlem iptal edildi; hedef kaydedilmedi.";
        }
        catch (Exception e) when (e is IOException or ArgumentException or InvalidOperationException)
        {
            Error = e.Message;
            new RunLog("Hedef tanımlama", null).Write($"HATA ({_definition.Type}): {e.Message}");
        }
        Close();
    }
}
