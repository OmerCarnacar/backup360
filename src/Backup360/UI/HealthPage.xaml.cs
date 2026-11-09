using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Backup360.Core;
using WUI = Wpf.Ui.Controls;

namespace Backup360.UI;

public sealed record HealthRow(string Status, Brush Brush, string Job, string Target, string File, string Size, string Checked, string Detail);

public partial class HealthPage
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly ScheduleEditor _schedule;
    private bool _wasRunning;

    public HealthPage()
    {
        InitializeComponent();
        _schedule = new ScheduleEditor(TaskPlanner.AuditSchedule());
        ScheduleHost.Content = _schedule;
        DeepBox.IsChecked = Db.GetSetting("audit_deep") == "1";
        _timer.Tick += (_, _) => Poll();
        Loaded += (_, _) => { Reload(); _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();
    }

    private void Reload()
    {
        var files = Db.BackupFiles();
        Files.ItemsSource = files.OrderBy(f => f.Status == BackupAudit.Ok ? 1 : 0).ThenByDescending(f => f.Created).Select(f =>
        {
            var (text, brush) = f.Status switch
            {
                BackupAudit.Ok => ("Sağlam", Fmt.Success),
                BackupAudit.Missing => ("Eksik", Fmt.Danger),
                BackupAudit.Changed => ("Bozuk", Fmt.Danger),
                _ => ("Kontrol edilmedi", Fmt.Neutral),
            };
            return new HealthRow(text, brush, f.JobName, f.Target, f.Path, Fmt.Size(f.Size), f.LastCheck ?? "", f.Detail);
        }).ToList();
        TotalText.Text = files.Count.ToString();
        OkText.Text = files.Count(f => f.Status == BackupAudit.Ok).ToString();
        BadText.Text = files.Count(f => f.Status is BackupAudit.Changed or BackupAudit.Missing).ToString();
        LastText.Text = files.Select(f => f.LastCheck).Where(x => x != null).DefaultIfEmpty("—").Max()![..Math.Min(16, files.Select(f => f.LastCheck).Where(x => x != null).DefaultIfEmpty("—").Max()!.Length)];
        Empty.Visibility = files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Poll()
    {
        var state = LiveFiles.ReadAll().FirstOrDefault(s => s.Kind == "audit");
        var view = LivePanel.Children.OfType<ActivityView>().FirstOrDefault();
        if (state != null)
        {
            if (view == null) LivePanel.Children.Add(new ActivityView(state));
            else view.Update(state);
            _wasRunning = true;
            return;
        }
        if (view != null) LivePanel.Children.Remove(view);
        if (!_wasRunning) return;
        _wasRunning = false;
        Reload();
        if (Db.LastRun("audit", 0) is { } last)
        {
            var severity = last.Status switch { "success" => WUI.InfoBarSeverity.Success, "corrupt" or "failed" => WUI.InfoBarSeverity.Error, _ => WUI.InfoBarSeverity.Informational };
            Notify(last.Detail, severity);
        }
    }

    private void Notify(string text, WUI.InfoBarSeverity severity)
    {
        Notice.Message = text;
        Notice.Severity = severity;
        Notice.IsOpen = true;
    }

    private void Start(bool deep)
    {
        if (_wasRunning) { Notify("Denetim zaten çalışıyor.", WUI.InfoBarSeverity.Informational); return; }
        _wasRunning = true;
        Launcher.StartAudit(deep);
        Notify(deep ? "Tam kontrol başladı; son yedekler indirilip açılacak." : "Hızlı kontrol başladı.", WUI.InfoBarSeverity.Informational);
    }

    private void Quick_Click(object sender, RoutedEventArgs e) => Start(false);
    private void Deep_Click(object sender, RoutedEventArgs e) => Start(true);

    private void Forget_Click(object sender, RoutedEventArgs e) => Ui.Guard(async () =>
    {
        if (!await Ui.Confirm("Eksik görünen yedekler listeden kaldırılsın mı? Hedefteki dosyalara dokunulmaz.")) return;
        Db.ForgetMissingBackups();
        Reload();
    });

    private void SaveSchedule_Click(object sender, RoutedEventArgs e) => Ui.Guard(async () =>
    {
        await TaskPlanner.SaveAuditAsync(_schedule.Value, DeepBox.IsChecked == true);
        Notify("Otomatik denetim kaydedildi.", WUI.InfoBarSeverity.Success);
    });
}
