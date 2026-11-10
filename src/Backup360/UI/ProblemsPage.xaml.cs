using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Backup360.Core;

namespace Backup360.UI;

public sealed record OpenProblemRow(List<long> Ids, string When, string Level, Brush Brush, string Job, string Item, string Count, string Message);

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
                return (last.Id, Row: new OpenProblemRow(g.Select(p => p.Id).ToList(), When(last.Time), text, brush, g.Key.Name, g.Key.Item.Length > 0 ? g.Key.Item : "(işin tamamı)", g.Count().ToString(), last.Message));
            })
            .OrderBy(x => x.Row.Level == "Hata" ? 0 : 1).ThenByDescending(x => x.Id)
            .Select(x => x.Row).ToList();

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
        OpenEmpty.Visibility = open.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var rows = Problems.List(Periods[Math.Max(0, Period.SelectedIndex)], OnlyOpen.IsChecked == true).Select(p =>
        {
            var (text, brush) = Level(p.Level);
            var parsed = DateTime.TryParse(p.Time, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d);
            return new ProblemLogRow(parsed ? d.ToString("dd.MM.yyyy ddd", Turkish) : p.Time, parsed ? d.ToString("HH:mm") : "", text, brush, p.Name, p.Item, p.Message, p.IsOpen ? "Açık" : "Çözüldü");
        }).ToList();
        LogGrid.ItemsSource = rows;
        LogEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Reload();
    private void Filter_Click(object sender, RoutedEventArgs e) => Reload();
    private void Period_Changed(object sender, SelectionChangedEventArgs e) => Reload();

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
