using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using Backup360.Core;

namespace Backup360.UI;

public sealed record HistoryRow(long Id, string Started, string Duration, string Kind, string Name, string Status, Brush Brush, string Size, string Speed, string Detail);

public partial class HistoryPage
{
    public HistoryPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Reload();
    }

    private void Reload()
    {
        Grid_.ItemsSource = Db.RecentRuns().Select(r =>
        {
            var (text, brush) = Fmt.Status(r.Status);
            var seconds = Fmt.RunSeconds(r);
            return new HistoryRow(r.Id, r.Started, r.Ended == null ? "" : Fmt.Duration((long)seconds), r.Kind switch { "job" => "Yedek", "audit" => "Denetim", "guard" => "Koruma", "restore" => "Geri yükleme", _ => "Taşıma" }, r.Name, text, brush,
                r.Bytes > 0 ? Fmt.Size(r.Bytes) : "", r.Bytes > 0 && seconds > 0 ? $"{Fmt.Size(r.Bytes / seconds)}/sn" : "", r.Detail);
        }).ToList();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Reload();
    private void Logs_Click(object sender, RoutedEventArgs e) => Process.Start("explorer.exe", AppPaths.LogDir);

    /// <summary>Seçilen çalışmanın kendi günlüğü; ayrı günlük tutulmadan önceki çalışmalarda aylık günlük açılır.</summary>
    private void RunLog_Click(object sender, RoutedEventArgs e)
    {
        if (Grid_.SelectedItem is not HistoryRow row)
        {
            MessageBox.Show("Önce listeden bir çalışma seçin.", "Backup360", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var file = RunLog.RunFile(row.Id);
        if (!File.Exists(file))
        {
            MessageBox.Show("Bu çalışma için ayrı günlük yok (bu özellikten önce çalışmış). Aylık günlük açılıyor.", "Backup360", MessageBoxButton.OK, MessageBoxImage.Information);
            file = Path.Combine(AppPaths.LogDir, $"backup360-{row.Started[..4]}{row.Started[5..7]}.log");
        }
        if (File.Exists(file)) new LogViewWindow($"{row.Name} · {row.Started}", file) { Owner = Window.GetWindow(this) }.Show();
    }

    private void Grid_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => RunLog_Click(sender, e);
}
