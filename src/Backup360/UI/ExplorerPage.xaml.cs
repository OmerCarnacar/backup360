using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Backup360.Core;
using Microsoft.Win32;
using WUI = Wpf.Ui.Controls;

namespace Backup360.UI;

public sealed record FileRow(RemoteEntry Entry, WUI.SymbolRegular Icon, Brush Tint, string Name, string SizeText, string When);

public partial class ExplorerPage
{
    private string _path = "";
    private CancellationTokenSource? _download;
    private string? Target => (TargetBox.SelectedItem as Target)?.Name;

    public ExplorerPage()
    {
        InitializeComponent();
        TargetBox.DisplayMemberPath = nameof(Core.Target.Name);
        Loaded += (_, _) => Ui.Guard(InitAsync);
    }

    private async Task InitAsync()
    {
        var selected = Target;
        var targets = await Cloud.ListTargetsAsync();
        TargetBox.ItemsSource = targets;
        TargetBox.SelectedItem = targets.FirstOrDefault(t => t.Name == selected) ?? targets.FirstOrDefault();
        Empty.Visibility = targets.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (targets.Count == 0) Empty.Text = "Henüz hedef yok. Hedefler sayfasından tanımlayın.";
    }

    private void Target_Changed(object sender, SelectionChangedEventArgs e)
    {
        _path = "";
        Ui.Guard(LoadAsync);
    }

    private async Task LoadAsync()
    {
        if (Target is not { } target) return;
        PathText.Text = $"{target}:/{_path}";
        Busy.Visibility = Visibility.Visible;
        Empty.Visibility = Visibility.Collapsed;
        Files.ItemsSource = null;
        try
        {
            var entries = await CloudExplorer.ListAsync(target, _path, CancellationToken.None);
            Files.ItemsSource = entries.Select(e => new FileRow(e, e.IsDir ? WUI.SymbolRegular.Folder24 : WUI.SymbolRegular.Document24, e.IsDir ? Fmt.Warning : Fmt.Neutral, e.Name,
                e.IsDir ? "" : Fmt.Size(e.Size), e.Modified?.ToString("dd.MM.yyyy HH:mm") ?? "")).ToList();
            Empty.Text = "Bu klasör boş.";
            Empty.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception x) when (x is IOException or InvalidOperationException)
        {
            Notify(x.Message, WUI.InfoBarSeverity.Error);
        }
        finally { Busy.Visibility = Visibility.Collapsed; }
    }

    private void Notify(string message, WUI.InfoBarSeverity severity)
    {
        Notice.Message = message;
        Notice.Severity = severity;
        Notice.IsOpen = true;
    }

    private void Up_Click(object sender, RoutedEventArgs e)
    {
        if (_path.Length == 0) return;
        var cut = _path.LastIndexOf('/');
        _path = cut < 0 ? "" : _path[..cut];
        Ui.Guard(LoadAsync);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Ui.Guard(LoadAsync);

    private void Files_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Files.SelectedItem is not FileRow { Entry.IsDir: true } row) return;
        _path = row.Entry.Path;
        Ui.Guard(LoadAsync);
    }

    private void NewFolder_Click(object sender, RoutedEventArgs e) => Ui.Guard(async () =>
    {
        if (Target is not { } target) return;
        var name = Ui.Prompt(Ui.Owner(this), "Klasör oluştur", "Klasör adı:");
        if (string.IsNullOrEmpty(name)) return;
        await Cloud.CreateFolderAsync(target, _path.Length == 0 ? name : $"{_path}/{name}", CancellationToken.None);
        await LoadAsync();
    });

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (Target is not { } target) return;
        var rows = Files.SelectedItems.OfType<FileRow>().ToList();
        if (rows.Count == 0) { Notify("Önce listeden dosya veya klasör seçin.", WUI.InfoBarSeverity.Informational); return; }
        if (_download != null) { Notify("Bir indirme zaten sürüyor.", WUI.InfoBarSeverity.Informational); return; }
        var dialog = new OpenFolderDialog { Title = "İndirilecek klasörü seçin" };
        if (dialog.ShowDialog(Ui.Owner(this)) != true) return;

        _download = new CancellationTokenSource();
        Transfer.Visibility = Visibility.Visible;
        try
        {
            for (var i = 0; i < rows.Count; i++)
            {
                var entry = rows[i].Entry;
                TransferTitle.Text = $"İndiriliyor ({i + 1}/{rows.Count}): {entry.Name}";
                TransferBar.IsIndeterminate = true;
                TransferPercent.Text = "";
                TransferStats.Text = "";
                await CloudExplorer.DownloadAsync(target, entry, dialog.FolderName, null, s => Dispatcher.Invoke(() => ShowStats(s)), _download.Token);
            }
            Notify($"{rows.Count} öğe indirildi: {dialog.FolderName}", WUI.InfoBarSeverity.Success);
        }
        catch (OperationCanceledException) { Notify("İndirme iptal edildi.", WUI.InfoBarSeverity.Warning); }
        catch (Exception x) when (x is IOException or InvalidOperationException) { Notify(x.Message, WUI.InfoBarSeverity.Error); }
        finally
        {
            _download.Dispose();
            _download = null;
            Transfer.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowStats(TransferStats s)
    {
        if (s.Total > 0)
        {
            var percent = (int)(s.Bytes * 100 / s.Total);
            TransferBar.IsIndeterminate = false;
            TransferBar.Value = percent;
            TransferPercent.Text = $"%{percent}";
        }
        var parts = new List<string>();
        if (s.Speed > 0) parts.Add($"{Fmt.Size(s.Speed)}/sn");
        if (s.Eta >= 0) parts.Add($"Kalan {Fmt.Duration(s.Eta)}");
        if (s.Total > 0) parts.Add($"{Fmt.Size(s.Bytes)} / {Fmt.Size(s.Total)}");
        TransferStats.Text = string.Join("  ·  ", parts);
    }

    private void CancelDownload_Click(object sender, RoutedEventArgs e) => _download?.Cancel();
}
