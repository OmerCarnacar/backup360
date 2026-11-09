using System.Windows;
using System.Windows.Controls;
using Backup360.Core;
using Microsoft.Win32;

namespace Backup360.UI;

public partial class JobEditorWindow
{
    private static readonly string[] Engines = { "mssql", "mysql", "sqlite" };
    private static readonly string[] DbTypes = { "full", "diff", "log" };
    private readonly Job _job;
    private readonly ScheduleEditor _schedule;
    private readonly List<FormatDef> _available = Core.Formats.All.Where(Core.Formats.IsAvailable).ToList();
    private bool _ready;

    public JobEditorWindow(Job job, List<Target> targets, string section = "folder")
    {
        _job = job;
        InitializeComponent();
        Ui.FitToScreen(this);
        var title = job.Id == 0 ? "Yeni yedekleme işi" : "İşi düzenle";
        Title = Bar.Title = title;

        NameBox.Text = job.Name;
        foreach (var g in Db.Jobs().Select(x => x.Group).Append("Genel").Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x)) GroupBox.Items.Add(g);
        GroupBox.Text = job.Group;

        (job.Mode switch { "dated" => DatedMode, "database" => DbMode, _ => FolderMode }).IsChecked = true;
        Compress.IsChecked = job.Mode is "archive" or "database" || (job.Mode == "dated" && job.DatedOutput == "archive");
        MirrorDelete.IsChecked = job.MirrorDelete;
        DateBy.SelectedIndex = job.DateBy == "folder" ? 1 : 0;
        DaysFilter.Value = job.DaysFilter;
        OnlyChanged.IsChecked = job.OnlyChanged;

        foreach (var s in job.Sources) Sources.Items.Add(s);
        Excludes.Text = string.Join(";", job.Excludes);

        foreach (var f in _available)
            Formats.Children.Add(new CheckBox { Content = f.Label, Tag = f.Key, IsChecked = job.Formats.Contains(f.Key), Margin = new Thickness(0, 2, 8, 2) });
        Level.Value = Math.Clamp(job.Level, 1, 9);
        UsePassword.IsChecked = job.PasswordEnc != null;
        Password.PlaceholderText = job.PasswordEnc != null ? "Değiştirmek için yazın" : "Parola";

        for (var i = 0; i < targets.Count; i++)
            Targets.Children.Add(new CheckBox { Content = new TextBlock { Text = $"{targets[i].Name}  ({targets[i].TypeLabel})" }, Tag = targets[i].Name, IsChecked = job.Id == 0 ? i == 0 : job.Targets.Contains(targets[i].Name), Margin = new Thickness(0, 2, 0, 2) });
        NoTargets.Visibility = targets.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Folder.Text = job.RemoteFolder;
        Keep.Value = job.Keep;
        KeepDays.Value = job.KeepDays;
        _schedule = new ScheduleEditor(job.Schedule);
        ScheduleHost.Content = _schedule;

        DbEngine.SelectedIndex = Math.Max(0, Array.IndexOf(Engines, job.DbEngine));
        DbServer.Text = job.DbServer;
        DbServer.PlaceholderText = @"SUNUCU\ORNEK";
        DbPort.Value = job.DbPort;
        DbAuth.SelectedIndex = job.DbAuth == "sql" ? 1 : 0;
        DbUser.Text = job.DbUser;
        DbPassword.PlaceholderText = job.DbPasswordEnc != null ? "Değiştirmek için yazın" : "Parola";
        DbAll.IsChecked = job.DbAll;
        foreach (var n in job.DbNames) AddDb(n, n, true);
        DbType.SelectedIndex = Math.Max(0, Array.IndexOf(DbTypes, job.DbType));
        DbCopyOnly.IsChecked = job.DbCopyOnly;
        DbDir.Text = job.DbBackupDir;
        DbAg.SelectedIndex = job.DbAgPolicy == "current" ? 1 : 0;
        DbSubnet.IsChecked = job.DbMultiSubnet;

        if (section == "sql")
        {
            DbMode.IsChecked = true;
            ModeArea.Visibility = Visibility.Collapsed;
            SourcesTab.Visibility = Visibility.Collapsed;
            Tabs.SelectedItem = DatabaseTab;
        }
        else
        {
            DbMode.Visibility = Visibility.Collapsed;
            DatabaseTab.Visibility = Visibility.Collapsed;
            MirrorDelete.Visibility = Visibility.Collapsed;   // Silmeli birebir kopya artık Eşitleme bölümünde
        }

        _ready = true;
        Sync();
    }

    private string Engine => Engines[Math.Max(0, DbEngine.SelectedIndex)];

    private void AddDb(string name, string label, bool check) =>
        DbList.Children.Add(new CheckBox { Content = new TextBlock { Text = label }, Tag = name, IsChecked = check, Margin = new Thickness(0, 1, 0, 1) });

    private void AnyChanged(object sender, RoutedEventArgs e) => Sync();
    private void AnySelection(object sender, SelectionChangedEventArgs e) => Sync();

    /// <summary>Motor değişince standart portu önerir; elle yazılmış farklı porta dokunmaz.</summary>
    private void Engine_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_ready && DbPort.Value is 0 or 1433 or 3306 or null)
            DbPort.Value = Engine switch { "mssql" => 1433, "mysql" => 3306, _ => 0 };
        Sync();
    }

    private void Scan_Click(object sender, RoutedEventArgs e)
    {
        var scan = new ScanWindow { Owner = this };
        if (scan.ShowDialog() != true || scan.Selected is not { } found) return;
        DbServer.Text = found.Server;
        if (found.Port > 0) DbPort.Value = found.Port;
    }

    private void Level_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var v = (int)e.NewValue;
        LevelText.Text = "Sıkıştırma seviyesi: " + (v == 9 ? "9 · En yüksek (Ultra)" : v >= 7 ? $"{v} · Yüksek" : v >= 4 ? $"{v} · Dengeli" : $"{v} · Hızlı");
    }

    private void Sync()
    {
        if (!_ready) return;
        var db = DbMode.IsChecked == true;
        if (db) Compress.IsChecked = true;   // Veritabanı yedekleri her zaman arşivlenir.
        Compress.IsEnabled = !db;
        MirrorDelete.IsEnabled = FolderMode.IsChecked == true && Compress.IsChecked != true;
        DatedOptions.IsEnabled = DatedMode.IsChecked == true;
        CompressPanel.IsEnabled = Compress.IsChecked == true;
        Password.IsEnabled = UsePassword.IsChecked == true;
        SourcesTab.IsEnabled = !db;
        DbPanel.IsEnabled = db;

        var sqlite = Engine == "sqlite";
        var mssql = Engine == "mssql";
        DbServer.IsEnabled = DbPort.IsEnabled = !sqlite;
        DbAuth.IsEnabled = ScanButton.IsEnabled = mssql;
        var login = Engine == "mysql" || (mssql && DbAuth.SelectedIndex == 1);
        DbUser.IsEnabled = DbPassword.IsEnabled = login;
        DbAll.IsEnabled = !sqlite;
        DbList.IsEnabled = sqlite || DbAll.IsChecked != true;
        SqlOptions.Visibility = mssql ? Visibility.Visible : Visibility.Collapsed;
        DbCopyOnly.IsEnabled = DbType.SelectedIndex == 0;
    }

    private void AddSource(string path)
    {
        if (!Sources.Items.Cast<string>().Contains(path, StringComparer.OrdinalIgnoreCase)) Sources.Items.Add(path);
    }

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFileDialog { Multiselect = true, Title = "Yedeklenecek dosyalar" };
        if (d.ShowDialog(this) == true) foreach (var f in d.FileNames) AddSource(f);
    }

    private void AddFolders_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFolderDialog { Multiselect = true, Title = "Yedeklenecek klasörler" };
        if (d.ShowDialog(this) == true) foreach (var f in d.FolderNames) AddSource(f);
    }

    private void AddNetwork_Click(object sender, RoutedEventArgs e)
    {
        var p = Ui.Prompt(this, "Ağ yolu", @"Örn: \\SUNUCU\Paylasim\Klasor");
        if (!string.IsNullOrEmpty(p)) AddSource(p);
    }

    private void RemoveSource_Click(object sender, RoutedEventArgs e)
    {
        foreach (var s in Sources.SelectedItems.Cast<object>().ToList()) Sources.Items.Remove(s);
    }

    private void ApplyDb(Job j)
    {
        j.DbEngine = Engine;
        j.DbServer = DbServer.Text.Trim();
        j.DbPort = (int)(DbPort.Value ?? 0);
        j.DbAuth = DbAuth.SelectedIndex == 1 || Engine == "mysql" ? "sql" : "windows";
        j.DbUser = DbUser.Text.Trim();
        j.DbPasswordEnc = DbPassword.Password.Length > 0 ? Secrets.Protect(DbPassword.Password) : _job.DbPasswordEnc;
        j.DbMultiSubnet = DbSubnet.IsChecked == true;
    }

    private async void LoadDatabases_Click(object sender, RoutedEventArgs e)
    {
        var probe = new Job();
        ApplyDb(probe);
        DbMessage.Text = "Bağlanıyor...";
        try
        {
            var chosen = DbList.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (Engine == "mssql")
            {
                var all = await MsSql.ListAsync(probe, CancellationToken.None);
                DbList.Children.Clear();
                foreach (var d in all) AddDb(d.Name, d.Label, chosen.Contains(d.Name));
                DbMessage.Text = $"{all.Count} veritabanı, {all.Count(d => d.InAg)} tanesi Always On'da.";
            }
            else if (Engine == "mysql")
            {
                var all = await MySqlEngine.ListAsync(probe, CancellationToken.None);
                DbList.Children.Clear();
                foreach (var n in all) AddDb(n, n, chosen.Contains(n));
                DbMessage.Text = $"{all.Count} veritabanı.";
            }
            else DbMessage.Text = "SQLite için dosya ekleyin.";
        }
        catch (Exception x) when (x is Microsoft.Data.SqlClient.SqlException or IOException or InvalidOperationException)
        {
            DbMessage.Text = "";
            await Ui.Info(x.Message, "Bağlantı kurulamadı");
        }
    }

    private void AddSqlite_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFileDialog { Multiselect = true, Title = "SQLite veritabanları", Filter = "SQLite|*.db;*.sqlite;*.sqlite3;*.db3|Tüm dosyalar|*.*" };
        if (d.ShowDialog(this) == true) foreach (var f in d.FileNames) AddDb(f, f, true);
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _job.Name = NameBox.Text.Trim();
            _job.Group = string.IsNullOrWhiteSpace(GroupBox.Text) ? "Genel" : GroupBox.Text.Trim();
            (_job.Mode, _job.DatedOutput) = JobModes.Map(DatedMode.IsChecked == true, DbMode.IsChecked == true, Compress.IsChecked == true);
            _job.MirrorDelete = MirrorDelete.IsChecked == true;
            _job.DateBy = DateBy.SelectedIndex == 1 ? "folder" : "run";
            _job.DaysFilter = (int)(DaysFilter.Value ?? 0);
            _job.OnlyChanged = OnlyChanged.IsChecked == true;
            _job.Sources = Sources.Items.Cast<string>().ToList();
            _job.Excludes = Excludes.Text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            _job.Formats = Formats.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToList();
            _job.Level = (int)Level.Value;
            if (UsePassword.IsChecked != true) _job.PasswordEnc = null;
            else if (Password.Password.Length > 0) _job.PasswordEnc = Secrets.Protect(Password.Password);
            else if (_job.PasswordEnc == null) throw new ArgumentException("Parolayı yazın veya parola seçeneğini kapatın.");
            _job.Targets = Targets.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToList();
            _job.RemoteFolder = Folder.Text.Trim();
            _job.Keep = (int)(Keep.Value ?? 0);
            _job.KeepDays = (int)(KeepDays.Value ?? 0);
            _job.Schedule = _schedule.Value;
            ApplyDb(_job);
            _job.DbAll = DbAll.IsChecked == true && Engine != "sqlite";
            _job.DbNames = DbList.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToList();
            _job.DbType = DbTypes[Math.Max(0, DbType.SelectedIndex)];
            _job.DbCopyOnly = DbCopyOnly.IsChecked == true;
            _job.DbBackupDir = DbDir.Text.Trim();
            _job.DbAgPolicy = DbAg.SelectedIndex == 1 ? "current" : "preferred";
            Core.Validation.Check(_job);
            DialogResult = true;
        }
        catch (Exception x) when (x is ArgumentException or InvalidOperationException)
        {
            await Ui.Info(x.Message, "Eksik bilgi");
        }
    }
}
