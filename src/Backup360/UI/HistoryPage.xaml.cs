using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using Backup360.Core;

namespace Backup360.UI;

public sealed record HistoryRow(string Started, string Duration, string Kind, string Name, string Status, Brush Brush, string Size, string Speed, string Detail);

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
            return new HistoryRow(r.Started, r.Ended == null ? "" : Fmt.Duration((long)seconds), r.Kind switch { "job" => "Yedek", "audit" => "Denetim", "guard" => "Koruma", "restore" => "Geri yükleme", _ => "Taşıma" }, r.Name, text, brush,
                r.Bytes > 0 ? Fmt.Size(r.Bytes) : "", r.Bytes > 0 && seconds > 0 ? $"{Fmt.Size(r.Bytes / seconds)}/sn" : "", r.Detail);
        }).ToList();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Reload();
    private void Logs_Click(object sender, RoutedEventArgs e) => Process.Start("explorer.exe", AppPaths.LogDir);
}
