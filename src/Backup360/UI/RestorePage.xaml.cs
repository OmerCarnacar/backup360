using System.Windows;
using System.Windows.Controls;
using Backup360.Core;
using Microsoft.Win32;
using WUI = Wpf.Ui.Controls;

namespace Backup360.UI;

public sealed record RestoreRow(RestorePoint Point, string When, string Item, string Kind, string Target, string Size);

public partial class RestorePage
{
    private List<RestorePoint> _points = new();
    private CancellationTokenSource? _cts;

    public RestorePage()
    {
        InitializeComponent();
        Jobs.DisplayMemberPath = nameof(Job.Name);
        Loaded += (_, _) =>
        {
            var selected = (Jobs.SelectedItem as Job)?.Id;
            var jobs = Db.Jobs();
            Jobs.ItemsSource = jobs;
            Jobs.SelectedItem = jobs.FirstOrDefault(j => j.Id == selected) ?? jobs.FirstOrDefault();
        };
    }

    private void Job_Changed(object sender, SelectionChangedEventArgs e) => Ui.Guard(LoadAsync);
    private void Refresh_Click(object sender, RoutedEventArgs e) => Ui.Guard(LoadAsync);

    private async Task LoadAsync()
    {
        if (Jobs.SelectedItem is not Job job) { Empty.Visibility = Visibility.Visible; return; }
        Points.ItemsSource = null;
        Empty.Visibility = Visibility.Collapsed;
        Busy.Visibility = Visibility.Visible;
        try
        {
            _points = await RestoreCatalog.ListAsync(job, CancellationToken.None);
            Points.ItemsSource = _points.Select(p => new RestoreRow(p, p.When, p.Item, p.IsArchive ? p.Extension : "Klasör", p.Target, p.Size > 0 ? Fmt.Size(p.Size) : "")).ToList();
            Empty.Visibility = _points.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception x) when (x is IOException or InvalidOperationException)
        {
            Notify(x.Message, WUI.InfoBarSeverity.Error);
        }
        finally { Busy.Visibility = Visibility.Collapsed; }
    }

    private void Point_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (Points.SelectedItem is not RestoreRow row) return;
        var db = row.Point.IsDatabase;
        DbOptions.Visibility = db ? Visibility.Visible : Visibility.Collapsed;
        FileOptions.Visibility = db ? Visibility.Collapsed : Visibility.Visible;
        if (!db) return;
        var firebird = row.Point.Job.DbEngine == "firebird";
        DbLabel.Text = firebird ? "Yeni veritabanı dosyasının tam yolu (sunucuda)" : "Yeni veritabanı adı (mevcut bir adla çakışamaz)";
        var name = row.Point.Item.Contains('_') && row.Point.Job.DbEngine == "mssql" ? row.Point.Item[..row.Point.Item.LastIndexOf('_')] : row.Point.Item;
        NewName.Text = firebird ? "" : $"{name}_geri_{DateTime.Now:yyyyMMdd_HHmm}".Replace('-', '_');
    }

    private void Pick_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFolderDialog { Title = "Geri yüklenecek klasör" };
        if (d.ShowDialog(Ui.Owner(this)) == true) Destination.Text = d.FolderName;
    }

    private void Notify(string text, WUI.InfoBarSeverity severity)
    {
        Notice.Message = text;
        Notice.Severity = severity;
        Notice.IsOpen = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (Points.SelectedItem is not RestoreRow row) { Notify("Önce listeden bir geri yükleme noktası seçin.", WUI.InfoBarSeverity.Informational); return; }
        var point = row.Point;
        if (!point.IsDatabase && string.IsNullOrWhiteSpace(Destination.Text)) { Notify("Geri yüklenecek klasörü seçin.", WUI.InfoBarSeverity.Informational); return; }

        var lines = new List<string>();
        void Write(string line) => Dispatcher.Invoke(() =>
        {
            lines.Add(line);
            Log.Text = string.Join("\n", lines.TakeLast(6));
        });
        void Percent(int p) => Dispatcher.Invoke(() => { Bar.IsIndeterminate = false; Bar.Value = p; });

        _cts = new CancellationTokenSource();
        RestoreButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        Bar.Visibility = Visibility.Visible;
        Bar.IsIndeterminate = true;
        Notice.IsOpen = false;
        var run = Db.StartRun("restore", point.Job.Id, $"Geri yükleme: {point.Job.Name}");
        try
        {
            string message;
            if (point.IsDatabase)
            {
                await RestoreEngine.RestoreDatabaseAsync(point, _points, NewName.Text, Write, Percent, _cts.Token);
                message = $"Veritabanı geri yüklendi: {NewName.Text.Trim()}";
            }
            else message = "Dosyalar geri yüklendi: " + await RestoreEngine.RestoreFilesAsync(point, Destination.Text.Trim(), Write, Percent, _cts.Token);
            Db.FinishRun(run, "success", message, point.Size);
            Write("Tamamlandı.");
            Notify(message, WUI.InfoBarSeverity.Success);
        }
        catch (OperationCanceledException)
        {
            Db.FinishRun(run, "canceled", "Kullanıcı iptal etti", 0);
            Notify("Geri yükleme iptal edildi.", WUI.InfoBarSeverity.Warning);
        }
        catch (BackupCorruptException x)
        {
            Db.FinishRun(run, "corrupt", x.Message, 0);
            Notify(x.Message, WUI.InfoBarSeverity.Error);
        }
        catch (Exception x) when (x is not OutOfMemoryException)
        {
            Db.FinishRun(run, "failed", x.Message, 0);
            Notify(Cloud.Explain(x.Message), WUI.InfoBarSeverity.Error);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            RestoreButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
            Bar.Visibility = Visibility.Collapsed;
        }
    }
}
