using System.Windows;
using System.Windows.Controls;
using Backup360.Core;
using Microsoft.Win32;

namespace Backup360.UI;

public partial class SyncEditorWindow
{
    private sealed record JobOption(long Id, string Name) { public override string ToString() => Name; }

    private readonly Job _job;
    private readonly ScheduleEditor _schedule;

    public SyncEditorWindow(Job job, List<Target> targets)
    {
        _job = job;
        InitializeComponent();
        Ui.FitToScreen(this);
        var sql = job.SyncKind == "sql";
        Title = Bar.Title = (job.Id == 0 ? "Yeni " : "") + (sql ? "SQL yedek eşitleme" : "Klasör eşitleme");
        NameBox.Text = job.Name;
        foreach (var g in Db.Jobs().Select(x => x.Group).Append("Genel").Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x)) GroupBox.Items.Add(g);
        GroupBox.Text = job.Group;

        SqlSource.Visibility = sql ? Visibility.Visible : Visibility.Collapsed;
        FromJob.Items.Add(new JobOption(0, "(Yedek işi seçilmedi)"));
        foreach (var j in Db.Jobs().Where(x => x.Mode == "database")) FromJob.Items.Add(new JobOption(j.Id, j.Name));
        FromJob.SelectedItem = FromJob.Items.Cast<JobOption>().FirstOrDefault(o => o.Id == job.SyncFromJob) ?? FromJob.Items[0];

        foreach (var s in job.Sources) Sources.Items.Add(s);
        for (var i = 0; i < targets.Count; i++)
            Targets.Children.Add(new CheckBox { Content = new TextBlock { Text = $"{targets[i].Name}  ({targets[i].TypeLabel})" }, Tag = targets[i].Name, IsChecked = job.Targets.Contains(targets[i].Name), Margin = new Thickness(0, 2, 0, 2) });
        NoTargets.Visibility = targets.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Folder.Text = job.RemoteFolder;
        MirrorDelete.IsChecked = job.MirrorDelete;
        Excludes.Text = string.Join(";", job.Excludes);
        _schedule = new ScheduleEditor(job.Schedule);
        ScheduleHost.Content = _schedule;
    }

    private void AddSource(string path)
    {
        if (!Sources.Items.Cast<string>().Contains(path, StringComparer.OrdinalIgnoreCase)) Sources.Items.Add(path);
    }

    private void AddFolders_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFolderDialog { Multiselect = true, Title = "Eşitlenecek klasörler" };
        if (d.ShowDialog(this) == true) foreach (var f in d.FolderNames) AddSource(f);
    }

    private void AddNetwork_Click(object sender, RoutedEventArgs e)
    {
        var p = Ui.Prompt(this, "Ağ yolu", @"Örn: \\NAS\Yedekler");
        if (!string.IsNullOrEmpty(p)) AddSource(p);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        foreach (var s in Sources.SelectedItems.Cast<object>().ToList()) Sources.Items.Remove(s);
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var schedule = _schedule.Value;
            _job.Name = NameBox.Text.Trim();
            _job.Group = string.IsNullOrWhiteSpace(GroupBox.Text) ? "Genel" : GroupBox.Text.Trim();
            _job.Mode = "sync";
            _job.SyncFromJob = _job.SyncKind == "sql" ? ((JobOption)FromJob.SelectedItem).Id : 0;
            _job.Sources = Sources.Items.Cast<string>().ToList();
            _job.Targets = Targets.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToList();
            _job.RemoteFolder = Folder.Text.Trim();
            _job.MirrorDelete = MirrorDelete.IsChecked == true;
            _job.Excludes = Excludes.Text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            _job.Schedule = schedule;
            Core.Validation.Check(_job);
            DialogResult = true;
        }
        catch (Exception x) when (x is ArgumentException or InvalidOperationException)
        {
            await Ui.Info(x.Message, "Eksik bilgi");
        }
    }
}
