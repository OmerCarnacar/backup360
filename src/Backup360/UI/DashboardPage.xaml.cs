using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Backup360.Core;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using WUI = Wpf.Ui.Controls;

namespace Backup360.UI;

public sealed record StatusRow(WUI.SymbolRegular Icon, Brush Brush, string Label, int Count);

public sealed record TopRow(string Name, string Value, double Ratio);

public sealed record FeedItem(WUI.SymbolRegular Icon, Brush Brush, string Name, string Label, string When);

public sealed class UpcomingVm : Observable
{
    private readonly TimeSpan _window;
    private readonly DateTime? _next;
    private double _percent;
    private string _remaining = "";

    public string Name { get; }
    public string Plan { get; }
    public double Percent { get => _percent; private set => Set(ref _percent, value); }
    public string Remaining { get => _remaining; private set => Set(ref _remaining, value); }
    public DateTime SortKey => _next ?? DateTime.MaxValue;

    public UpcomingVm(string name, string plan, DateTime? next, TimeSpan window)
    {
        (Name, Plan, _next, _window) = (name, plan, next, window);
        Tick();
    }

    public void Tick()
    {
        if (_next is not { } next) { Remaining = "Bekliyor"; Percent = 0; return; }
        var left = next - DateTime.Now;
        Remaining = left.TotalSeconds <= 0 ? "Şimdi" : Fmt.Duration((long)left.TotalSeconds) + " sonra";
        Percent = Math.Clamp(100 * (1 - left.TotalSeconds / _window.TotalSeconds), 2, 100);
    }
}

public partial class DashboardPage
{
    private readonly DispatcherTimer _live = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _slow = new() { Interval = TimeSpan.FromSeconds(30) };
    private List<UpcomingVm> _upcoming = new();

    public DashboardPage()
    {
        InitializeComponent();
        _heroBrush = HeroBanner.Background;
        _live.Tick += (_, _) => RefreshLive();
        _clock.Tick += (_, _) => _upcoming.ForEach(u => u.Tick());
        _slow.Tick += (_, _) => Ui.Guard(RefreshAsync);
        Loaded += (_, _) =>
        {
            _live.Start();
            _clock.Start();
            _slow.Start();
            RefreshLive();
            Ui.Guard(RefreshAsync);
        };
        Unloaded += (_, _) => { _live.Stop(); _clock.Stop(); _slow.Stop(); };
    }

    public void RefreshLive()
    {
        var states = LiveFiles.ReadAll();
        var keys = states.Select(s => s.Key).ToHashSet();
        foreach (var view in LivePanel.Children.OfType<ActivityView>().Where(v => !keys.Contains(v.Key)).ToList()) LivePanel.Children.Remove(view);
        foreach (var state in states)
        {
            var view = LivePanel.Children.OfType<ActivityView>().FirstOrDefault(v => v.Key == state.Key);
            if (view == null) LivePanel.Children.Add(new ActivityView(state));
            else view.Update(state);
        }
        // Birden çok iş yan yana durur; pano aşağı doğru uzamaz.
        LivePanel.Columns = states.Count >= 2 && ActualWidth >= 1100 ? 2 : 1;
        LiveTitle.Visibility = states.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var (errors, warnings) = Problems.OpenCounts();
        ProblemBar.IsOpen = errors + warnings > 0;
        ProblemBar.Severity = errors > 0 ? Wpf.Ui.Controls.InfoBarSeverity.Error : Wpf.Ui.Controls.InfoBarSeverity.Warning;
        ProblemBar.Message = $"{errors} açık hata, {warnings} açık uyarı var. Hangi öğelerin yedeklenemediğini veya aktarılamadığını Sorunlar sayfasında görebilirsiniz.";
    }

    private static TimeSpan Window(Schedule s) => s.Type switch
    {
        "minutes" => TimeSpan.FromMinutes(Math.Max(1, s.Interval)),
        "hourly" => TimeSpan.FromHours(Math.Max(1, s.Interval)),
        "weekly" => TimeSpan.FromDays(7),
        _ => TimeSpan.FromDays(1),
    };

    private List<RunRecord> _runs = new();
    private Brush? _heroBrush;

    private void Chart_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (IsLoaded) DrawChart();
    }

    private void DrawChart()
    {
        var range = RangeBox.SelectedIndex switch { 1 => "weekly", 2 => "monthly", _ => "daily" };
        var bytes = MetricBox.SelectedIndex != 1;
        var buckets = RunStats.Build(_runs, range, DateTime.Today);
        ChartTitle.Text = (bytes ? "Aktarılan veri" : "Çalışma sayısı") + range switch { "weekly" => " · son 12 hafta", "monthly" => " · son 12 ay", _ => " · son 14 gün" };

        var ink = new SolidColorPaint(ThemeService.IsDark ? SKColor.Parse("#CBD5E1") : SKColor.Parse("#475569"));
        var grid = new SolidColorPaint(ThemeService.IsDark ? SKColor.Parse("#334155") : SKColor.Parse("#E2E8F0")) { StrokeThickness = 1 };
        var indigo = new SolidColorPaint(SKColor.Parse("#6366F1"));

        if (bytes)
        {
            Chart.Series = new ISeries[] { new ColumnSeries<double> { Name = "Aktarılan", Values = buckets.Select(b => (double)b.Bytes).ToArray(), Fill = indigo, MaxBarWidth = 34, Rx = 4, Ry = 4, YToolTipLabelFormatter = p => Fmt.Size(p.Coordinate.PrimaryValue) } };
            Chart.LegendPosition = LiveChartsCore.Measure.LegendPosition.Hidden;
            Chart.YAxes = new[] { new Axis { MinLimit = 0, Labeler = v => v <= 0 ? "0" : Fmt.Size(v), LabelsPaint = ink, SeparatorsPaint = grid, TextSize = 12 } };
        }
        else
        {
            Chart.Series = new ISeries[]
            {
                new StackedColumnSeries<int> { Name = "Başarılı", Values = buckets.Select(b => b.Ok).ToArray(), Fill = indigo, MaxBarWidth = 34, Rx = 4, Ry = 4 },
                new StackedColumnSeries<int> { Name = "Hata / uyarı", Values = buckets.Select(b => b.Problem).ToArray(), Fill = new SolidColorPaint(SKColor.Parse("#D97706")), MaxBarWidth = 34, Rx = 4, Ry = 4 },
            };
            Chart.LegendPosition = LiveChartsCore.Measure.LegendPosition.Top;
            Chart.LegendTextPaint = ink;
            Chart.YAxes = new[] { new Axis { MinLimit = 0, MinStep = 1, LabelsPaint = ink, SeparatorsPaint = grid, TextSize = 12 } };
        }
        Chart.XAxes = new[] { new Axis { Labels = buckets.Select(b => b.Label).ToArray(), LabelsPaint = ink, TextSize = 11, LabelsRotation = range == "daily" ? 0 : 0 } };
    }

    public async Task RefreshAsync()
    {
        var all = Db.RecentRuns(20000);
        _runs = all;
        var recent = all.Where(r => DateTime.TryParse(r.Started, out var d) && d > DateTime.Now.AddDays(-7)).ToList();
        var failures = recent.Count(r => r.Status is "failed" or "corrupt");
        var allJobs = Db.Jobs();
        StatOk.Text = $"{allJobs.Count(j => j.Schedule.Type != "none")} / {allJobs.Count}";
        StatBad.Text = (await Cloud.ListTargetsAsync()).Count.ToString();
        StatSent.Text = Fmt.Size(recent.Sum(r => r.Bytes));
        var seconds = recent.Where(r => r.Bytes > 0).Sum(Fmt.RunSeconds);
        StatSpeed.Text = seconds > 0 ? $"{Fmt.Size(recent.Sum(r => r.Bytes) / seconds)}/sn" : "—";
        DrawChart();

        var running = LiveFiles.ReadAll().Where(s => s.Kind == "job").Select(s => s.RefId).ToHashSet();
        var lastStatus = allJobs.Select(j => running.Contains(j.Id) ? "running" : Db.LastRun("job", j.Id)?.Status ?? "none").ToList();
        int Of(params string[] s) => lastStatus.Count(s.Contains);
        StatusList.ItemsSource = new[]
        {
            new StatusRow(WUI.SymbolRegular.PlayCircle24, Fmt.Indigo, "Çalışıyor", Of("running")),
            new StatusRow(WUI.SymbolRegular.CheckmarkCircle24, Fmt.Success, "Başarılı", Of("success")),
            new StatusRow(WUI.SymbolRegular.Warning24, Fmt.Warning, "Uyarılı", Of("warning")),
            new StatusRow(WUI.SymbolRegular.ShieldError24, Fmt.Danger, "Yedek bozuk", Of("corrupt")),
            new StatusRow(WUI.SymbolRegular.LockClosed24, Fmt.Danger, "Fidye şüphesi", Of("ransomware")),
            new StatusRow(WUI.SymbolRegular.DismissCircle24, Fmt.Danger, "Hatalı", Of("failed")),
            new StatusRow(WUI.SymbolRegular.Prohibited24, Fmt.Neutral, "İptal edildi", Of("canceled")),
            new StatusRow(WUI.SymbolRegular.Circle24, Fmt.Neutral, "Henüz çalışmadı", Of("none")),
        };

        var totals = RunStats.PerJob(all);
        var maxBytes = Math.Max(1, totals.Select(x => x.Bytes).DefaultIfEmpty(1).Max());
        var maxSeconds = Math.Max(1, totals.Select(x => x.Seconds).DefaultIfEmpty(1).Max());
        var biggest = totals.Where(x => x.Bytes > 0).OrderByDescending(x => x.Bytes).Take(4).Select(x => new TopRow(x.Name, Fmt.Size(x.Bytes), 100d * x.Bytes / maxBytes)).ToList();
        var longest = totals.Where(x => x.Seconds > 0).OrderByDescending(x => x.Seconds).Take(4).Select(x => new TopRow(x.Name, Fmt.Duration((long)x.Seconds), 100 * x.Seconds / maxSeconds)).ToList();
        Biggest.ItemsSource = biggest;
        Longest.ItemsSource = longest;
        BiggestEmpty.Visibility = biggest.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LongestEmpty.Visibility = longest.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var feed = all.Take(4).Select(r =>
        {
            var (label, brush) = Fmt.Status(r.Status);
            var icon = r.Status switch { "success" => WUI.SymbolRegular.CheckmarkCircle24, "corrupt" => WUI.SymbolRegular.ShieldError24, "failed" => WUI.SymbolRegular.DismissCircle24, "warning" => WUI.SymbolRegular.Warning24, _ => WUI.SymbolRegular.Circle24 };
            return new FeedItem(icon, brush, r.Name, label, r.Started.Length >= 16 ? r.Started[5..16] : r.Started);
        }).ToList();
        Feed.ItemsSource = feed;
        FeedEmpty.Visibility = feed.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var times = await TaskPlanner.ReadTimesAsync();
        DateTime? Next(string task) => times.TryGetValue(task, out var t) ? t.Next : null;
        var jobs = Db.Jobs();
        var list = new List<UpcomingVm>();
        list.AddRange(jobs.Where(j => j.Schedule.Type != "none").Select(j => new UpcomingVm(j.Name, $"{j.Group} · {j.Schedule.Describe()}", Next(TaskPlanner.JobTask(j.Id)), Window(j.Schedule))));
        list.AddRange(Db.Rules().Where(r => r.Schedule.Type != "none").Select(r => new UpcomingVm(r.Name, $"{r.Group} · {r.Schedule.Describe()}", Next(TaskPlanner.RuleTask(r.Id)), Window(r.Schedule))));
        _upcoming = list.OrderBy(u => u.SortKey).ToList();
        Upcoming.ItemsSource = _upcoming;
        UpcomingEmpty.Visibility = _upcoming.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var next = _upcoming.FirstOrDefault(u => u.SortKey != DateTime.MaxValue);
        var nextText = next != null ? $"Sıradaki: {next.Name}, {next.Remaining}" : "Zamanlanmış çalışma yok";
        if (!string.IsNullOrEmpty(Db.GetSetting("guard_alert")) || lastStatus.Contains("ransomware"))
        {
            SetHero("FİDYE YAZILIMI ŞÜPHESİ", "Yedekleme durduruldu, sağlam yedeklere dokunulmadı. Kaynak klasörleri kontrol edin.", WUI.SymbolRegular.ShieldError24);
            HeroBanner.Background = Fmt.Danger;
            return;
        }
        var corrupted = allJobs.Where((_, i) => lastStatus[i] == "corrupt").Select(j => j.Name).ToList();
        if (Db.LastRun("audit", 0)?.Status == "corrupt") corrupted.Add("hedefteki yedekler");
        if (corrupted.Count > 0)
        {
            SetHero($"YEDEK BOZUK: {string.Join(", ", corrupted.Take(3))}", "Ayrıntılar için Yedek Sağlığı ve Geçmiş sayfalarına bakın.", WUI.SymbolRegular.ShieldError24);
            HeroBanner.Background = Fmt.Danger;
            return;
        }
        HeroBanner.ClearValue(System.Windows.Controls.Border.BackgroundProperty);
        HeroBanner.Background = _heroBrush;
        if (failures > 0) SetHero($"{failures} çalışma hata verdi", "Ayrıntılar için Geçmiş sayfasına bakın  ·  " + nextText, WUI.SymbolRegular.Warning24);
        else if (jobs.Count == 0) SetHero("Hoş geldiniz", "Başlamak için bir hedef tanımlayın ve ilk yedekleme işinizi oluşturun", WUI.SymbolRegular.Rocket24);
        else SetHero("Yedekleriniz sağlıklı", "Son 7 günde hata yok  ·  " + nextText, WUI.SymbolRegular.ShieldCheckmark24);
    }

    private void SetHero(string title, string sub, WUI.SymbolRegular icon)
    {
        HeroTitle.Text = title;
        HeroSub.Text = sub;
        HeroIcon.Symbol = icon;
    }
}
