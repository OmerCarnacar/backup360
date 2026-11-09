using Backup360.Core;

namespace Backup360.UI;

public sealed class ScheduleEditor : FlowLayoutPanel
{
    private static readonly (string Key, string Label)[] Types =
        { ("none", "Elle (zamanlama yok)"), ("minutes", "Her N dakikada"), ("hourly", "Her N saatte"), ("daily", "Her gün"), ("weekly", "Haftanın günleri") };
    private static readonly (string Key, string Label)[] Days =
        { ("Monday", "Pzt"), ("Tuesday", "Sal"), ("Wednesday", "Çar"), ("Thursday", "Per"), ("Friday", "Cum"), ("Saturday", "Cmt"), ("Sunday", "Paz") };

    private readonly ComboBox _type = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    private readonly NumericUpDown _interval = new() { Minimum = 1, Maximum = 1440, Width = 70, Value = 1 };
    private readonly DateTimePicker _time = new() { Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true, Width = 80 };
    private readonly FlowLayoutPanel _days = new() { AutoSize = true, WrapContents = false };

    public ScheduleEditor(Schedule s)
    {
        AutoSize = true;
        WrapContents = true;
        foreach (var t in Types) _type.Items.Add(t.Label);
        _type.SelectedIndex = Math.Max(0, Array.FindIndex(Types, t => t.Key == s.Type));
        _interval.Value = Math.Clamp(s.Interval, 1, 1440);
        _time.Value = DateTime.Today + (TimeSpan.TryParse(s.Time, out var ts) ? ts : TimeSpan.FromHours(2));
        var chosen = s.Days.Split(',', StringSplitOptions.RemoveEmptyEntries);
        foreach (var d in Days) _days.Controls.Add(new CheckBox { Text = d.Label, Tag = d.Key, AutoSize = true, Checked = chosen.Contains(d.Key) });
        Controls.AddRange(new Control[] { _type, _interval, _time, _days });
        _type.SelectedIndexChanged += (_, _) => Sync();
        Sync();
    }

    private string Selected => Types[_type.SelectedIndex].Key;

    private void Sync()
    {
        _interval.Visible = Selected is "minutes" or "hourly";
        _time.Visible = Selected is "daily" or "weekly";
        _days.Visible = Selected == "weekly";
    }

    public Schedule Value
    {
        get
        {
            var days = _days.Controls.OfType<CheckBox>().Where(c => c.Checked).Select(c => (string)c.Tag!).ToList();
            if (Selected == "weekly" && days.Count == 0) throw new ArgumentException("En az bir gün seçin.");
            return new Schedule { Type = Selected, Interval = (int)_interval.Value, Time = _time.Value.ToString("HH:mm"), Days = string.Join(",", days) };
        }
    }
}

public abstract class EditorForm : Form
{
    protected EditorForm(string title, int width, int height)
    {
        Text = title;
        Icon = Theme.AppIcon;
        ClientSize = new Size(width, height);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Surface;
        Font = Theme.Body;
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(12, 10, 4, 0), BackColor = Theme.Bg };
        var save = Ui.Button("Kaydet", true, (_, _) => { try { Commit(); DialogResult = DialogResult.OK; Close(); } catch (Exception e) when (e is ArgumentException or IOException or InvalidOperationException) { Ui.Error(e); } });
        bar.Controls.Add(save);
        bar.Controls.Add(Ui.Button("Vazgeç", false, (_, _) => Close()));
        Controls.Add(bar);
    }

    protected abstract void Commit();

    protected static TableLayoutPanel Form2(TabPage? page = null)
    {
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(14), AutoScroll = true, BackColor = Theme.Surface };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        page?.Controls.Add(t);
        return t;
    }
}

public sealed class JobEditor : EditorForm
{
    private readonly Job _job;
    private readonly TextBox _name = new() { Width = 420 };
    private readonly ComboBox _group = new() { Width = 220 };
    private readonly ComboBox _datedOutput = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
    private readonly NumericUpDown _daysFilter = new() { Minimum = 0, Maximum = 36500, Width = 80 };
    private readonly NumericUpDown _keepDays = new() { Minimum = 0, Maximum = 36500, Width = 80 };
    private readonly ListBox _sources = new() { Width = 660, Height = 210, SelectionMode = SelectionMode.MultiExtended, HorizontalScrollbar = true };
    private readonly TextBox _excludes = new() { Width = 660 };
    private readonly RadioButton _archive = new() { Text = "Tek arşiv: seçilen her şeyi tarihli tek dosyada sıkıştır ve gönder", AutoSize = true };
    private readonly RadioButton _dated = new() { Text = "Tarih bazlı: seçilen klasörün içindeki her klasörü ayrı sıkıştır, tarih klasörüne gönder", AutoSize = true };
    private readonly RadioButton _mirror = new() { Text = "Klasör eşitleme: sıkıştırmadan, birebir aynı yapıyla buluta gönder", AutoSize = true };
    private readonly RadioButton _database = new() { Text = "Veritabanı yedeği: SQL Server (Always On dahil), MySQL/MariaDB, SQLite", AutoSize = true };
    private readonly ComboBox _dbEngine = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly TextBox _dbServer = new() { Width = 330 };
    private readonly NumericUpDown _dbPort = new() { Minimum = 0, Maximum = 65535, Width = 90 };
    private readonly ComboBox _dbAuth = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 230 };
    private readonly TextBox _dbUser = new() { Width = 150 };
    private readonly TextBox _dbPassword = new() { Width = 150, UseSystemPasswordChar = true };
    private readonly CheckBox _dbAll = new() { Text = "Tüm kullanıcı veritabanları (yeni eklenenler otomatik dahil)", AutoSize = true };
    private readonly CheckedListBox _dbList = new() { Width = 660, Height = 92, CheckOnClick = true, HorizontalScrollbar = true };
    private readonly ComboBox _dbType = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 230 };
    private readonly CheckBox _dbCopyOnly = new() { Text = "Tam yedek COPY_ONLY olsun (mevcut yedek planınızı bozmaz)", AutoSize = true };
    private readonly TextBox _dbDir = new() { Width = 500 };
    private readonly ComboBox _dbAg = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 500 };
    private readonly CheckBox _dbSubnet = new() { Text = "Always On dinleyicisine bağlan (MultiSubnetFailover)", AutoSize = true };
    private readonly Label _dbMessage = Ui.Label("", Theme.Small, Theme.Muted);
    private readonly FlowLayoutPanel _sourceButtons = new() { AutoSize = true };
    private readonly Control[] _sqlOnly, _serverOnly;
    private static readonly string[] Engines = { "mssql", "mysql", "sqlite" };
    private static readonly string[] DbTypes = { "full", "diff", "log" };
    private readonly ComboBox _dateBy = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
    private readonly CheckBox _onlyChanged = new() { Text = "Yalnızca son gönderimden sonra değişen klasörleri gönder", AutoSize = true };
    private readonly CheckBox _mirrorDelete = new() { Text = "Kaynakta silinenleri hedeften de kaldır (kaldırılanlar _silinenler klasöründe saklanır)", AutoSize = true };
    private readonly CheckedListBox _formats = new() { Width = 420, Height = 190, CheckOnClick = true };
    private readonly TrackBar _level = new() { Minimum = 1, Maximum = 9, Width = 300, TickFrequency = 1 };
    private readonly Label _levelText = Ui.Label("");
    private readonly CheckBox _usePassword = new() { Text = "Arşivi parola ile şifrele (7z, zip, rar)", AutoSize = true };
    private readonly TextBox _password = new() { Width = 300, UseSystemPasswordChar = true };
    private readonly CheckedListBox _targets = new() { Width = 420, Height = 110, CheckOnClick = true };
    private readonly TextBox _folder = new() { Width = 420 };
    private readonly NumericUpDown _keep = new() { Minimum = 0, Maximum = 9999, Width = 80 };
    private readonly ScheduleEditor _schedule;
    private readonly List<FormatDef> _available = Formats.All.Where(Formats.IsAvailable).ToList();
    private readonly TabPage _compress = new("Sıkıştırma");

    public JobEditor(Job job, List<Target> targets) : base(job.Id == 0 ? "Yeni yedekleme işi" : "İşi düzenle", 760, 700)
    {
        _job = job;
        _schedule = new ScheduleEditor(job.Schedule);
        var tabs = new TabControl { Dock = DockStyle.Fill };

        var src = new TabPage("Kaynaklar");
        var a = Form2(src);
        a.Controls.Add(Ui.Label("İş adı"));
        _name.Text = job.Name;
        var nameRow = new FlowLayoutPanel { AutoSize = true };
        _group.Items.AddRange(Db.Jobs().Select(x => x.Group).Append("Genel").Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).Cast<object>().ToArray());
        _group.Text = job.Group;
        nameRow.Controls.AddRange(new Control[] { _name, Ui.Label("Grup"), _group });
        a.Controls.Add(nameRow);
        a.Controls.Add(Ui.Label("Dosyalar, klasörler, USB ve ağ alanları"));
        _sources.Items.AddRange(job.Sources.Cast<object>().ToArray());
        a.Controls.Add(_sources);
        var row = _sourceButtons;
        row.Controls.Add(Ui.Button("Dosya ekle", false, (_, _) => AddFiles()));
        row.Controls.Add(Ui.Button("Klasör ekle", false, (_, _) => AddFolders()));
        row.Controls.Add(Ui.Button("Ağ yolu ekle", false, (_, _) => { var p = Ui.Prompt("Ağ yolu", @"Örn: \\SUNUCU\Paylasim\Klasor"); if (!string.IsNullOrEmpty(p)) AddSource(p); }));
        row.Controls.Add(Ui.Button("Kaldır", false, (_, _) => { foreach (var s in _sources.SelectedItems.Cast<object>().ToList()) _sources.Items.Remove(s); }));
        a.Controls.Add(row);
        a.Controls.Add(Ui.Label("Hariç tutulacaklar (noktalı virgülle, örn: *.tmp;node_modules)"));
        _excludes.Text = string.Join(";", job.Excludes);
        a.Controls.Add(_excludes);
        tabs.TabPages.Add(src);

        var mode = new TabPage("Gönderim ayarı");
        var b = Form2(mode);
        b.Controls.Add(Ui.Label("Nasıl gönderilsin?", Theme.Bold));
        (job.Mode switch { "dated" => _dated, "mirror" => _mirror, "database" => _database, _ => _archive }).Checked = true;
        b.Controls.Add(_archive);
        b.Controls.Add(_dated);
        _dateBy.Items.AddRange(new object[] { "Gönderim tarihi", "Klasörün son değişiklik tarihi" });
        _dateBy.SelectedIndex = job.DateBy == "folder" ? 1 : 0;
        b.Controls.Add(Ui.Label("Tarih klasörü (yıl-ay-gün)"));
        b.Controls.Add(_dateBy);
        var datedRow = new FlowLayoutPanel { AutoSize = true };
        _datedOutput.Items.AddRange(new object[] { "Sıkıştırılmış arşiv olarak", "Klasör olarak (sıkıştırmasız)" });
        _datedOutput.SelectedIndex = job.DatedOutput == "folder" ? 1 : 0;
        _datedOutput.SelectedIndexChanged += (_, _) => SyncMode();
        _daysFilter.Value = Math.Clamp(job.DaysFilter, 0, 36500);
        datedRow.Controls.AddRange(new Control[] { Ui.Label("Çıktı"), _datedOutput, Ui.Label("Son kaç günde değişenler (0 = hepsi)"), _daysFilter });
        b.Controls.Add(datedRow);
        _onlyChanged.Checked = job.OnlyChanged;
        b.Controls.Add(_onlyChanged);
        b.Controls.Add(_mirror);
        _mirrorDelete.Checked = job.MirrorDelete;
        b.Controls.Add(_mirrorDelete);
        b.Controls.Add(_database);
        foreach (var r in new[] { _archive, _dated, _mirror, _database }) r.CheckedChanged += (_, _) => SyncMode();
        tabs.TabPages.Add(mode);

        var c = Form2(_compress);
        c.Controls.Add(Ui.Label("Formatlar (birden çok seçilebilir, her biri ayrı dosya olur)"));
        foreach (var f in _available) _formats.Items.Add(f.Label, job.Formats.Contains(f.Key));
        c.Controls.Add(_formats);
        var fr = new FlowLayoutPanel { AutoSize = true };
        fr.Controls.Add(Ui.Button("Tümünü seç", false, (_, _) => { for (var i = 0; i < _formats.Items.Count; i++) _formats.SetItemChecked(i, true); }));
        fr.Controls.Add(Ui.Button("Temizle", false, (_, _) => { for (var i = 0; i < _formats.Items.Count; i++) _formats.SetItemChecked(i, false); }));
        c.Controls.Add(fr);
        c.Controls.Add(Ui.Label("Sıkıştırma seviyesi"));
        _level.Value = Math.Clamp(job.Level, 1, 9);
        _level.ValueChanged += (_, _) => UpdateLevel();
        var lv = new FlowLayoutPanel { AutoSize = true };
        lv.Controls.Add(_level);
        lv.Controls.Add(_levelText);
        c.Controls.Add(lv);
        _usePassword.Checked = job.PasswordEnc != null;
        _usePassword.CheckedChanged += (_, _) => _password.Enabled = _usePassword.Checked;
        _password.Enabled = _usePassword.Checked;
        _password.PlaceholderText = job.PasswordEnc != null ? "Değiştirmek için yazın" : "Parola";
        c.Controls.Add(_usePassword);
        c.Controls.Add(_password);
        tabs.TabPages.Add(_compress);

        var dest = new TabPage("Hedef ve zamanlama");
        var d = Form2(dest);
        d.Controls.Add(Ui.Label("Hedefler"));
        foreach (var t in targets) _targets.Items.Add(t.Name, job.Id == 0 ? t == targets[0] : job.Targets.Contains(t.Name));
        d.Controls.Add(_targets);
        if (targets.Count == 0) d.Controls.Add(Ui.Label("Henüz hedef yok. Önce Hedefler sayfasından Google Drive bağlayın.", Theme.Small, Theme.Danger));
        d.Controls.Add(Ui.Label("Hedefteki ana klasör"));
        _folder.Text = job.RemoteFolder;
        d.Controls.Add(_folder);
        d.Controls.Add(Ui.Label("Saklanacak yedek sayısı (her öğe ve format için, 0 = hepsi)"));
        _keep.Value = Math.Clamp(job.Keep, 0, 9999);
        d.Controls.Add(_keep);
        d.Controls.Add(Ui.Label("Şu kadar günden eski yedekleri sil (0 = silme; her öğenin en yeni yedeği her zaman kalır)"));
        _keepDays.Value = Math.Clamp(job.KeepDays, 0, 36500);
        d.Controls.Add(_keepDays);
        d.Controls.Add(Ui.Label("Zamanlama"));
        d.Controls.Add(_schedule);
        tabs.TabPages.Add(dest);

        var dbPage = new TabPage("Veritabanı");
        var e = Form2(dbPage);
        _dbEngine.Items.AddRange(new object[] { "SQL Server", "MySQL / MariaDB", "SQLite" });
        _dbEngine.SelectedIndex = Math.Max(0, Array.IndexOf(Engines, job.DbEngine));
        e.Controls.Add(Ui.Label("Veritabanı motoru"));
        e.Controls.Add(_dbEngine);
        var server = new FlowLayoutPanel { AutoSize = true };
        _dbServer.Text = job.DbServer;
        _dbServer.PlaceholderText = @"SUNUCU\ORNEK veya Always On dinleyicisi";
        _dbPort.Value = Math.Clamp(job.DbPort, 0, 65535);
        server.Controls.AddRange(new Control[] { Ui.Label("Sunucu"), _dbServer, Ui.Label("Port (0 = varsayılan)"), _dbPort });
        e.Controls.Add(server);
        var auth = new FlowLayoutPanel { AutoSize = true };
        _dbAuth.Items.AddRange(new object[] { "Windows kimlik doğrulama", "Kullanıcı adı ve parola" });
        _dbAuth.SelectedIndex = job.DbAuth == "sql" ? 1 : 0;
        _dbUser.Text = job.DbUser;
        _dbPassword.PlaceholderText = job.DbPasswordEnc != null ? "Değiştirmek için yazın" : "Parola";
        auth.Controls.AddRange(new Control[] { _dbAuth, Ui.Label("Kullanıcı"), _dbUser, Ui.Label("Parola"), _dbPassword });
        e.Controls.Add(auth);
        _dbAll.Checked = job.DbAll;
        e.Controls.Add(_dbAll);
        foreach (var n in job.DbNames) _dbList.Items.Add(n, true);
        e.Controls.Add(_dbList);
        var dbButtons = new FlowLayoutPanel { AutoSize = true };
        dbButtons.Controls.Add(Ui.Button("Bağlan ve listele", false, async (_, _) => await LoadDatabasesAsync()));
        dbButtons.Controls.Add(Ui.Button("SQLite dosyası ekle", false, (_, _) => AddSqliteFiles()));
        dbButtons.Controls.Add(_dbMessage);
        e.Controls.Add(dbButtons);
        var type = new FlowLayoutPanel { AutoSize = true };
        _dbType.Items.AddRange(new object[] { "Tam (Full)", "Fark (Differential)", "Günlük (Log)" });
        _dbType.SelectedIndex = Math.Max(0, Array.IndexOf(DbTypes, job.DbType));
        _dbCopyOnly.Checked = job.DbCopyOnly;
        type.Controls.AddRange(new Control[] { Ui.Label("Yedek türü"), _dbType, _dbCopyOnly });
        e.Controls.Add(type);
        e.Controls.Add(Ui.Label("Yedek klasörü (SQL Server'ın yazabildiği; uzak sunucuda ortak UNC yolu. Boşsa sunucu varsayılanı)"));
        _dbDir.Text = job.DbBackupDir;
        e.Controls.Add(_dbDir);
        e.Controls.Add(Ui.Label("Always On yedek replikası"));
        _dbAg.Items.AddRange(new object[] { "SQL Server'daki yedek tercihine uy (önerilen)", "Bağlandığım replikadan her zaman al" });
        _dbAg.SelectedIndex = job.DbAgPolicy == "current" ? 1 : 0;
        e.Controls.Add(_dbAg);
        _dbSubnet.Checked = job.DbMultiSubnet;
        e.Controls.Add(_dbSubnet);
        tabs.TabPages.Add(dbPage);
        _serverOnly = new Control[] { _dbServer, _dbPort, _dbUser, _dbPassword };
        _sqlOnly = new Control[] { _dbAuth, _dbType, _dbCopyOnly, _dbDir, _dbAg, _dbSubnet };
        _dbEngine.SelectedIndexChanged += (_, _) => SyncMode();
        _dbAuth.SelectedIndexChanged += (_, _) => SyncMode();
        _dbAll.CheckedChanged += (_, _) => SyncMode();
        _dbType.SelectedIndexChanged += (_, _) => SyncMode();

        Controls.Add(tabs);
        tabs.BringToFront();
        UpdateLevel();
        SyncMode();
    }

    private string Engine => Engines[_dbEngine.SelectedIndex];

    private void ApplyDatabaseFields(Job j)
    {
        j.DbEngine = Engine;
        j.DbServer = _dbServer.Text.Trim();
        j.DbPort = (int)_dbPort.Value;
        j.DbAuth = _dbAuth.SelectedIndex == 1 || Engine == "mysql" ? "sql" : "windows";
        j.DbUser = _dbUser.Text.Trim();
        j.DbPasswordEnc = _dbPassword.Text.Length > 0 ? Secrets.Protect(_dbPassword.Text) : _job.DbPasswordEnc;
        j.DbMultiSubnet = _dbSubnet.Checked;
    }

    private async Task LoadDatabasesAsync()
    {
        var probe = new Job();
        ApplyDatabaseFields(probe);
        _dbMessage.Text = "Bağlanıyor...";
        try
        {
            var chosen = _dbList.CheckedItems.Cast<object>().Select(EntryName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _dbList.Items.Clear();
            if (Engine == "mssql")
            {
                var all = await MsSql.ListAsync(probe, CancellationToken.None);
                foreach (var d in all) _dbList.Items.Add(new DbEntry(d.Name, d.Label), chosen.Contains(d.Name));
                _dbMessage.Text = $"{all.Count} veritabanı, {all.Count(d => d.InAg)} tanesi Always On'da.";
            }
            else if (Engine == "mysql")
            {
                var all = await MySqlEngine.ListAsync(probe, CancellationToken.None);
                foreach (var n in all) _dbList.Items.Add(new DbEntry(n, n), chosen.Contains(n));
                _dbMessage.Text = $"{all.Count} veritabanı.";
            }
        }
        catch (Exception x) when (x is Microsoft.Data.SqlClient.SqlException or IOException or InvalidOperationException)
        {
            _dbMessage.Text = "";
            Ui.Error(x);
        }
    }

    private sealed record DbEntry(string Name, string Label) { public override string ToString() => Label; }
    private static string EntryName(object o) => o is DbEntry e ? e.Name : (string)o;

    private void AddSqliteFiles()
    {
        using var d = new OpenFileDialog { Multiselect = true, Title = "SQLite veritabanları", Filter = "SQLite|*.db;*.sqlite;*.sqlite3;*.db3|Tüm dosyalar|*.*" };
        if (d.ShowDialog(this) == DialogResult.OK)
            foreach (var f in d.FileNames) _dbList.Items.Add(f, true);
    }

    private void AddSource(string path)
    {
        if (!_sources.Items.Cast<string>().Contains(path, StringComparer.OrdinalIgnoreCase)) _sources.Items.Add(path);
    }

    private void AddFiles()
    {
        using var d = new OpenFileDialog { Multiselect = true, Title = "Yedeklenecek dosyalar" };
        if (d.ShowDialog(this) == DialogResult.OK) foreach (var f in d.FileNames) AddSource(f);
    }

    private void AddFolders()
    {
        using var d = new FolderBrowserDialog { Multiselect = true, Description = "Yedeklenecek klasörler" };
        if (d.ShowDialog(this) == DialogResult.OK) foreach (var f in d.SelectedPaths) AddSource(f);
    }

    private void UpdateLevel() =>
        _levelText.Text = _level.Value == 9 ? "9 · En yüksek (Ultra)" : _level.Value >= 7 ? $"{_level.Value} · Yüksek" : _level.Value >= 4 ? $"{_level.Value} · Dengeli" : $"{_level.Value} · Hızlı";

    private void SyncMode()
    {
        _dateBy.Enabled = _onlyChanged.Enabled = _dated.Checked;
        _mirrorDelete.Enabled = _mirror.Checked;
        _datedOutput.Enabled = _daysFilter.Enabled = _dated.Checked;
        var uncompressed = _mirror.Checked || (_dated.Checked && _datedOutput.SelectedIndex == 1);
        foreach (Control c in _compress.Controls) c.Enabled = !uncompressed;
        _sources.Enabled = _sourceButtons.Enabled = _excludes.Enabled = !_database.Checked;

        var isDb = _database.Checked;
        var sqlite = Engine == "sqlite";
        var mssql = Engine == "mssql";
        _dbEngine.Enabled = _dbMessage.Enabled = isDb;
        foreach (var c in _serverOnly) c.Enabled = isDb && !sqlite;
        foreach (var c in _sqlOnly) c.Enabled = isDb && mssql;
        var needsLogin = Engine == "mysql" || (mssql && _dbAuth.SelectedIndex == 1);
        _dbUser.Enabled = _dbPassword.Enabled = isDb && needsLogin;
        _dbAll.Enabled = isDb && !sqlite;
        _dbList.Enabled = isDb && (sqlite || !_dbAll.Checked);
        _dbCopyOnly.Enabled = isDb && mssql && DbTypes[_dbType.SelectedIndex] == "full";
    }

    protected override void Commit()
    {
        _job.Name = _name.Text.Trim();
        _job.Sources = _sources.Items.Cast<string>().ToList();
        _job.Excludes = _excludes.Text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        _job.Group = string.IsNullOrWhiteSpace(_group.Text) ? "Genel" : _group.Text.Trim();
        _job.DatedOutput = _datedOutput.SelectedIndex == 1 ? "folder" : "archive";
        _job.DaysFilter = (int)_daysFilter.Value;
        _job.KeepDays = (int)_keepDays.Value;
        _job.Mode = _database.Checked ? "database" : _dated.Checked ? "dated" : _mirror.Checked ? "mirror" : "archive";
        ApplyDatabaseFields(_job);
        _job.DbAll = _dbAll.Checked && Engine != "sqlite";
        _job.DbNames = _dbList.CheckedItems.Cast<object>().Select(EntryName).ToList();
        _job.DbType = DbTypes[_dbType.SelectedIndex];
        _job.DbCopyOnly = _dbCopyOnly.Checked;
        _job.DbBackupDir = _dbDir.Text.Trim();
        _job.DbAgPolicy = _dbAg.SelectedIndex == 1 ? "current" : "preferred";
        _job.DateBy = _dateBy.SelectedIndex == 1 ? "folder" : "run";
        _job.OnlyChanged = _onlyChanged.Checked;
        _job.MirrorDelete = _mirrorDelete.Checked;
        _job.Formats = _formats.CheckedIndices.Cast<int>().Select(i => _available[i].Key).ToList();
        _job.Level = _level.Value;
        if (!_usePassword.Checked) _job.PasswordEnc = null;
        else if (_password.Text.Length > 0) _job.PasswordEnc = Secrets.Protect(_password.Text);
        else if (_job.PasswordEnc == null) throw new ArgumentException("Parolayı yazın veya parola seçeneğini kapatın.");
        _job.Targets = _targets.CheckedItems.Cast<string>().ToList();
        _job.RemoteFolder = _folder.Text.Trim();
        _job.Keep = (int)_keep.Value;
        _job.Schedule = _schedule.Value;
        Validation.Check(_job);
    }
}

public sealed class RuleEditor : EditorForm
{
    private readonly MoveRule _rule;
    private readonly TextBox _name = new() { Width = 420 };
    private readonly ComboBox _ruleGroup = new() { Width = 150 };
    private readonly TextBox _source = new() { Width = 480 };
    private readonly TextBox _target = new() { Width = 480 };
    private readonly TextBox _pattern = new() { Width = 300 };
    private readonly CheckBox _recursive = new() { Text = "Alt klasörleri de tara (klasör yapısı hedefte korunur)", AutoSize = true };
    private readonly CheckBox _onlyNew = new() { Text = "Yalnızca kural oluşturulduktan sonra gelen dosyaları taşı", AutoSize = true };
    private readonly ComboBox _conflict = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
    private readonly NumericUpDown _stable = new() { Minimum = 0, Maximum = 3600, Width = 80 };
    private readonly ScheduleEditor _schedule;
    private static readonly string[] Conflicts = { "rename", "skip", "overwrite" };

    /// <summary>Kaynak değiştiyse veya kural yeniyse mevcut içerik baz alınmalıdır.</summary>
    public bool NeedsBaseline { get; private set; }

    public RuleEditor(MoveRule rule) : base(rule.Id == 0 ? "Yeni taşıma kuralı" : "Kuralı düzenle", 640, 660)
    {
        _rule = rule;
        _schedule = new ScheduleEditor(rule.Schedule);
        var t = Form2();
        t.Controls.Add(Ui.Label("Kural adı ve grup"));
        _name.Text = rule.Name;
        _ruleGroup.Items.AddRange(Db.Rules().Select(x => x.Group).Append("Genel").Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).Cast<object>().ToArray());
        _ruleGroup.Text = rule.Group;
        var nameRow = new FlowLayoutPanel { AutoSize = true };
        nameRow.Controls.AddRange(new Control[] { _name, _ruleGroup });
        t.Controls.Add(nameRow);
        t.Controls.Add(Ui.Label("Kaynak klasör (yeni dosyaların geldiği yer)"));
        t.Controls.Add(Picker(_source, rule.Source));
        t.Controls.Add(Ui.Label("Hedef klasör (dosyaların taşınacağı yer)"));
        t.Controls.Add(Picker(_target, rule.Target));
        t.Controls.Add(Ui.Label("Dosya filtresi (noktalı virgülle, örn: *.pdf;*.xlsx)"));
        _pattern.Text = rule.Pattern;
        t.Controls.Add(_pattern);
        _recursive.Checked = rule.Recursive;
        t.Controls.Add(_recursive);
        _onlyNew.Checked = rule.OnlyNew;
        t.Controls.Add(_onlyNew);
        t.Controls.Add(Ui.Label("Hedefte aynı adlı dosya varsa"));
        _conflict.Items.AddRange(new object[] { "Yeni adla kaydet: dosya (1).pdf", "Atla, kaynakta kalsın", "Üzerine yaz" });
        _conflict.SelectedIndex = Math.Max(0, Array.IndexOf(Conflicts, rule.Conflict));
        t.Controls.Add(_conflict);
        t.Controls.Add(Ui.Label("Dosya kaç saniye değişmezse hazır sayılsın"));
        _stable.Value = Math.Clamp(rule.StableSeconds, 0, 3600);
        t.Controls.Add(_stable);
        t.Controls.Add(Ui.Label("Kontrol sıklığı"));
        t.Controls.Add(_schedule);
        Controls.Add(t);
        t.BringToFront();
    }

    private Control Picker(TextBox box, string value)
    {
        box.Text = value;
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        row.Controls.Add(box);
        row.Controls.Add(Ui.Button("Seç", false, (_, _) =>
        {
            using var d = new FolderBrowserDialog { InitialDirectory = box.Text };
            if (d.ShowDialog(this) == DialogResult.OK) box.Text = d.SelectedPath;
        }));
        return row;
    }

    protected override void Commit()
    {
        NeedsBaseline = _rule.Id == 0
            || !string.Equals(_rule.Source, _source.Text.Trim(), StringComparison.OrdinalIgnoreCase)
            || _rule.OnlyNew != _onlyNew.Checked
            || _rule.Recursive != _recursive.Checked;
        _rule.Name = _name.Text.Trim();
        _rule.Group = string.IsNullOrWhiteSpace(_ruleGroup.Text) ? "Genel" : _ruleGroup.Text.Trim();
        _rule.Source = _source.Text.Trim();
        _rule.Target = _target.Text.Trim();
        _rule.Pattern = string.IsNullOrWhiteSpace(_pattern.Text) ? "*" : _pattern.Text.Trim();
        _rule.Recursive = _recursive.Checked;
        _rule.OnlyNew = _onlyNew.Checked;
        _rule.Conflict = Conflicts[_conflict.SelectedIndex];
        _rule.StableSeconds = (int)_stable.Value;
        _rule.Schedule = _schedule.Value;
        Validation.Check(_rule);
    }
}
