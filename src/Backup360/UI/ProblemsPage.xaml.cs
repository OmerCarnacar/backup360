using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Backup360.Core;
using WUI = Wpf.Ui.Controls;

namespace Backup360.UI;

public sealed record OpenProblemRow(List<long> Ids, string When, string Level, Brush Brush, string Job, string Item, string Count, string Message, string Advice, string Go, string Kind, long RefId);

public sealed record ProblemLogRow(string Day, string Clock, string Level, Brush Brush, string Job, string Item, string Message, string State);

public partial class ProblemsPage
{
    private static readonly int[] Periods = { 1, 7, 30, 0 };
    private static readonly CultureInfo Turkish = new("tr-TR");
    private bool _ready;

    public ProblemsPage()
    {
        InitializeComponent();
        Period.SelectedIndex = 1;
        _ready = true;
        Loaded += (_, _) => Reload();
    }

    private static (string Text, Brush Brush) Level(string level) => level == Problems.Error ? ("Hata", Fmt.Danger) : ("Uyarı", Fmt.Warning);

    private static string When(string time) =>
        DateTime.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.ToString("dd.MM.yyyy HH:mm", Turkish) : time;

    /// <summary>Aynı işin aynı öğesine ait açık kayıtları tek satırda toplar: liste, yapılacak işi gösterir.</summary>
    public static List<OpenProblemRow> GroupOpen(IEnumerable<Problem> problems) =>
        problems.Where(p => p.IsOpen)
            .GroupBy(p => (p.Kind, p.RefId, p.Name, p.Item, p.Level))
            .Select(g =>
            {
                var last = g.OrderByDescending(p => p.Id).First();
                var (text, brush) = Level(g.Key.Level);
                var (advice, go) = Problems.Advice(last.Message);
                return (last.Id, Row: new OpenProblemRow(g.Select(p => p.Id).ToList(), When(last.Time), text, brush, g.Key.Name, g.Key.Item.Length > 0 ? g.Key.Item : "(işin tamamı)", g.Count().ToString(), last.Message, advice, go, g.Key.Kind, g.Key.RefId));
            })
            .OrderBy(x => x.Row.Level == "Hata" ? 0 : 1).ThenByDescending(x => x.Id)
            .Select(x => x.Row).ToList();

    /// <summary>Sorunun çözüleceği sayfa: hedef sorunu Hedefler'e, iş ayarı sorunu o işin sayfasına götürür.</summary>
    public static Type PageFor(OpenProblemRow row)
    {
        switch (row.Go)
        {
            case Problems.GoTargets: return typeof(TargetsPage);
            case Problems.GoHealth: return typeof(HealthPage);
            case Problems.GoSettings: return typeof(SettingsPage);
        }
        if (row.Kind == "rule") return typeof(RulesPage);
        if (row.Kind == "audit") return typeof(HealthPage);
        if (row.Kind != "job" || Db.GetJob(row.RefId) is not { } job) return typeof(LogPage);
        return job.Mode switch
        {
            "database" => typeof(SqlBackupPage),
            "sync" => job.SyncKind == "sql" ? typeof(SqlSyncPage) : typeof(FolderSyncPage),
            _ => typeof(FolderBackupPage),
        };
    }

    private void Reload()
    {
        if (!_ready) return;
        Problems.Prune();
        var (errors, warnings) = Problems.OpenCounts();
        OpenErrors.Text = errors.ToString();
        OpenErrors.Foreground = errors > 0 ? Fmt.Danger : (Brush)FindResource("TextFillColorPrimaryBrush");
        OpenWarnings.Text = warnings.ToString();
        TodayCount.Text = Problems.List(1, false).Count.ToString();
        WeekCount.Text = Problems.List(7, false).Count.ToString();

        var open = GroupOpen(Problems.List(0, true));
        OpenGrid.ItemsSource = open;
        if (open.Count > 0) OpenGrid.SelectedIndex = 0;
        OpenEmpty.Visibility = open.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowAdvice();

        var rows = Problems.List(Periods[Math.Max(0, Period.SelectedIndex)], OnlyOpen.IsChecked == true).Select(p =>
        {
            var (text, brush) = Level(p.Level);
            var parsed = DateTime.TryParse(p.Time, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d);
            return new ProblemLogRow(parsed ? d.ToString("dd.MM.yyyy ddd", Turkish) : p.Time, parsed ? d.ToString("HH:mm") : "", text, brush, p.Name, p.Item, p.Message, p.IsOpen ? "Açık" : "Çözüldü");
        }).ToList();
        LogGrid.ItemsSource = rows;
        LogEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowAdvice()
    {
        var row = OpenGrid.SelectedItem as OpenProblemRow;
        AdviceBar.IsOpen = row != null;
        GoButton.IsEnabled = row != null;
        RetryButton.IsEnabled = row is { Kind: "job" or "rule" };
        if (row == null) return;
        AdviceBar.Title = $"{row.Job}  ·  {row.Item}";
        AdviceBar.Message = row.Advice;
        AdviceBar.Severity = row.Level == "Hata" ? WUI.InfoBarSeverity.Error : WUI.InfoBarSeverity.Warning;
    }

    private void Open_Changed(object sender, SelectionChangedEventArgs e) => ShowAdvice();
    private void Refresh_Click(object sender, RoutedEventArgs e) => Reload();
    private void Filter_Click(object sender, RoutedEventArgs e) => Reload();
    private void Period_Changed(object sender, SelectionChangedEventArgs e) => Reload();

    private void Go()
    {
        if (OpenGrid.SelectedItem is OpenProblemRow row) Ui.Navigate(this, PageFor(row));
    }

    private void Go_Click(object sender, RoutedEventArgs e) => Go();
    private void Open_DoubleClick(object sender, MouseButtonEventArgs e) => Go();

    private void Retry_Click(object sender, RoutedEventArgs e) => Ui.Guard(async () =>
    {
        if (OpenGrid.SelectedItem is not OpenProblemRow { Kind: "job" or "rule" } row) return;
        Launcher.Start(row.Kind, row.RefId);
        await Ui.Info($"{row.Job} yeniden başlatıldı. Öğe bu kez tamamlanırsa kaydı kendiliğinden kapanır.");
        Ui.Navigate(this, PageFor(row with { Go = Problems.GoJob }));
    });

    private void ResolveSelected_Click(object sender, RoutedEventArgs e)
    {
        Problems.Resolve(OpenGrid.SelectedItems.OfType<OpenProblemRow>().SelectMany(r => r.Ids));
        Reload();
    }

    private void ResolveAll_Click(object sender, RoutedEventArgs e) => Ui.Guard(async () =>
    {
        if (!await Ui.Confirm("Tüm açık sorunlar çözüldü olarak işaretlensin mi? Kayıtlar silinmez, günlükte kalır.")) return;
        Problems.ResolveAll();
        Reload();
    });
}
