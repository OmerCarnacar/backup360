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
    private static readonly string[] Days = { "Paz", "Pzt", "Sal", "Çar", "Per", "Cum", "Cmt" };
    private readonly DispatcherTimer _live = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _slow = new() { Interval = TimeSpan.FromSeconds(30) };
    private List<UpcomingVm> _upcoming = new();

    public DashboardPage()
    {
        InitializeComponent();
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
        LiveEmpty.Visibility = states.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static TimeSpan Window(Schedule s) => s.Type switch
    {
        "minutes" => TimeSpan.FromMinutes(Math.Max(1, s.Interval)),
        "hourly" => TimeSpan.FromHours(Math.Max(1, s.Interval)),
        "weekly" => TimeSpan.FromDays(7),
        _ => TimeSpan.FromDays(1),
    };

    private void DrawChart(List<RunRecord> recent)
    {
        var days = Enumerable.Range(0, 7).Select(i => DateTime.Today.AddDays(i - 6)).ToList();
        int Count(DateTime day, Func<string, bool> match) => recent.Count(r => DateTime.TryParse(r.Started, out var d) && d.Date == day && match(r.Status));
        var ink = new SolidColorPaint(ThemeService.IsDark ? SKColor.Parse("#CBD5E1") : SKColor.Parse("#475569"));
        var grid = new SolidColorPaint(ThemeService.IsDark ? SKColor.Parse("#334155") : SKColor.Parse("#E2E8F0")) { StrokeThickness = 1 };

        Chart.Series = new ISeries[]
        {
            new StackedColumnSeries<int> { Name = "Başarılı", Values = days.Select(d => Count(d, s => s == "success")).ToArray(), Fill = new SolidColorPaint(SKColor.Parse("#6366F1")), MaxBarWidth = 30, Rx = 4, Ry = 4 },
            new StackedColumnSeries<int> { Name = "Hata / uyarı", Values = days.Select(d => Count(d, s => s is "failed" or "warning")).ToArray(), Fill = new SolidColorPaint(SKColor.Parse("#D97706")), MaxBarWidth = 30, Rx = 4, Ry = 4 },
        };
        Chart.XAxes = new[] { new Axis { Labels = days.Select(d => Days[(int)d.DayOfWeek]).ToArray(), LabelsPaint = ink, TextSize = 12 } };
        Chart.YAxes = new[] { new Axis { MinLimit = 0, MinStep = 1, LabelsPaint = ink, SeparatorsPaint = grid, TextSize = 12 } };
        Chart.LegendTextPaint = ink;
    }

    public async Task RefreshAsync()
    {
        var all = Db.RecentRuns(1000);
        var recent = all.Where(r => DateTime.TryParse(r.Started, out var d) && d > DateTime.Now.AddDays(-7)).ToList();
        var failures = recent.Count(r => r.Status == "failed");
        StatOk.Text = recent.Count(r => r.Status == "success").ToString();
        StatBad.Text = $"{failures} / {recent.Count(r => r.Status == "warning")}";
        StatSent.Text = Fmt.Size(recent.Sum(r => r.Bytes));
        var seconds = recent.Where(r => r.Bytes > 0).Sum(Fmt.RunSeconds);
        StatSpeed.Text = seconds > 0 ? $"{Fmt.Size(recent.Sum(r => r.Bytes) / seconds)}/sn" : "—";
        DrawChart(recent);

        var feed = all.Take(5).Select(r =>
        {
            var (label, brush) = Fmt.Status(r.Status);
            var icon = r.Status switch { "success" => WUI.SymbolRegular.CheckmarkCircle24, "failed" => WUI.SymbolRegular.DismissCircle24, "warning" => WUI.SymbolRegular.Warning24, _ => WUI.SymbolRegular.Circle24 };
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
