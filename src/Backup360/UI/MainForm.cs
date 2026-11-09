using System.Diagnostics;
using Backup360.Core;

namespace Backup360.UI;

public sealed class MainForm : Form
{
    private readonly Panel _content = new() { Dock = DockStyle.Fill, BackColor = Theme.Bg };
    private readonly FlowLayoutPanel _menu = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Theme.Sidebar, Padding = new Padding(0, 6, 0, 0) };
    private readonly Dictionary<string, Control> _pages = new();
    private readonly Dictionary<string, NavButton> _buttons = new();

    public MainForm(string user)
    {
        Text = "Backup360";
        Icon = Theme.AppIcon;
        ClientSize = new Size(1180, 740);
        MinimumSize = new Size(1040, 660);
        StartPosition = FormStartPosition.CenterScreen;
        Font = Theme.Body;
        BackColor = Theme.Bg;

        var side = new Panel { Dock = DockStyle.Left, Width = 212, BackColor = Theme.Sidebar };
        var head = new Panel { Dock = DockStyle.Top, Height = 88, BackColor = Theme.Sidebar };
        head.Controls.Add(new PictureBox { Image = Theme.Logo, SizeMode = PictureBoxSizeMode.Zoom, Bounds = new Rectangle(18, 22, 44, 44) });
        head.Controls.Add(new Label { Text = "Backup360", Font = Theme.Bold, ForeColor = Color.White, AutoSize = true, Location = new Point(70, 25) });
        head.Controls.Add(new Label { Text = user, Font = Theme.Small, ForeColor = Color.FromArgb(148, 163, 184), AutoSize = true, Location = new Point(70, 47) });
        var foot = new Label { Dock = DockStyle.Bottom, Height = 34, Text = $"v{AppInfo.Version}", TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.FromArgb(100, 116, 139), Font = Theme.Small };
        side.Controls.Add(_menu);
        side.Controls.Add(foot);
        side.Controls.Add(head);

        Add("", "Pano", new DashboardPage());
        Add("", "Yedekleme İşleri", new JobsPage());
        Add("", "Klasör Taşıma", new RulesPage());
        Add("", "Hedefler", new TargetsPage());
        Add("", "Geçmiş", new HistoryPage());
        Add("", "Ayarlar", new SettingsPage(user));
        Add("", "Hakkında", new AboutPage());

        Controls.Add(_content);
        Controls.Add(side);
        Show("Pano");
        Shown += async (_, _) =>
        {
            try { await TaskPlanner.ResyncIfMovedAsync(); }
            catch (InvalidOperationException e) { Ui.Error(e); }
        };
    }

    private void Add(string glyph, string title, Control page)
    {
        page.Dock = DockStyle.Fill;
        page.Visible = false;
        _pages[title] = page;
        _content.Controls.Add(page);
        var b = new NavButton(glyph, title);
        b.Click += (_, _) => Show(title);
        _buttons[title] = b;
        _menu.Controls.Add(b);
    }

    public void Show(string title)
    {
        foreach (var (name, page) in _pages) page.Visible = name == title;
        foreach (var (name, button) in _buttons) button.Active = name == title;
        (_pages[title] as IRefreshable)?.Reload();
    }
}

public interface IRefreshable { void Reload(); }

/// <summary>Başlık, araç çubuğu ve kart içinde liste düzeni.</summary>
public abstract class ListPage : Panel, IRefreshable
{
    protected readonly ListView List;
    protected readonly FlowLayoutPanel Tools = new() { Dock = DockStyle.Top, AutoSize = true, BackColor = Theme.Bg, Padding = new Padding(0, 10, 0, 6) };

    protected ListPage(string title, string hint, params (string, int)[] columns)
    {
        BackColor = Theme.Bg;
        Padding = new Padding(28, 22, 28, 22);
        List = Ui.Grid(columns);
        var card = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Border, Padding = new Padding(1) };
        var host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Padding = new Padding(10) };
        host.Controls.Add(List);
        card.Controls.Add(host);
        Controls.Add(card);
        Controls.Add(Tools);
        Controls.Add(new Label { Text = hint, Dock = DockStyle.Top, Height = 24, ForeColor = Theme.Muted, Font = Theme.Body });
        Controls.Add(new Label { Text = title, Dock = DockStyle.Top, Height = 40, Font = Theme.Heading, ForeColor = Theme.Text });
        List.DoubleClick += (_, _) => OnEdit();
    }

    protected long? SelectedId => List.SelectedItems.Count == 1 ? List.SelectedItems[0].Tag as long? : null;
    protected virtual void OnEdit() { }
    public abstract void Reload();

    protected ListViewGroup GroupFor(string name)
    {
        var g = List.Groups.Cast<ListViewGroup>().FirstOrDefault(x => string.Equals(x.Header, name, StringComparison.OrdinalIgnoreCase));
        if (g != null) return g;
        g = new ListViewGroup(name, name);
        List.Groups.Add(g);
        return g;
    }

    protected void ClearList()
    {
        List.Items.Clear();
        List.Groups.Clear();
    }

    protected static async void Guard(Func<Task> action)
    {
        try { await action(); }
        catch (Exception e) when (e is IOException or InvalidOperationException or ArgumentException or UnauthorizedAccessException) { Ui.Error(e); }
    }
}

public sealed class JobsPage : ListPage
{
    private static readonly Dictionary<string, string> ModeNames = new() { ["archive"] = "Tek arşiv", ["dated"] = "Tarih bazlı", ["mirror"] = "Eşitleme" };
    private static readonly Dictionary<string, string> EngineNames = new() { ["mssql"] = "SQL Server", ["mysql"] = "MySQL", ["sqlite"] = "SQLite" };

    public JobsPage() : base("Yedekleme İşleri", "Dosya, klasör ve veritabanlarınızı gruplar halinde yönetin.",
        ("İş", 210), ("Tür", 105), ("Format", 120), ("Hedef", 120), ("Zamanlama", 125), ("Son durum", 170))
    {
        Tools.Controls.Add(Ui.Button("Yeni iş", true, (_, _) => Guard(() => EditAsync(new Job()))));
        Tools.Controls.Add(Ui.Button("Düzenle", false, (_, _) => OnEdit()));
        Tools.Controls.Add(Ui.Button("Şimdi çalıştır", false, (_, _) => { if (SelectedId is { } id) Guard(() => { Launcher.Start("job", id); return Task.CompletedTask; }); }));
        Tools.Controls.Add(Ui.Button("Grubu çalıştır", false, (_, _) => RunGroup()));
        Tools.Controls.Add(Ui.Button("Süre hesapla", false, (_, _) => Estimate()));
        Tools.Controls.Add(Ui.Button("Sil", false, (_, _) => Delete()));
    }

    public override void Reload()
    {
        ClearList();
        foreach (var j in Db.Jobs().OrderBy(x => x.Group).ThenBy(x => x.Name))
        {
            var last = Db.LastRun("job", j.Id);
            var (text, color) = Theme.Status(last?.Status);
            var uncompressed = j.Mode == "mirror" || (j.Mode == "dated" && j.DatedOutput == "folder");
            var item = new ListViewItem(new[]
            {
                j.Name, j.Mode == "database" ? EngineNames[j.DbEngine] : ModeNames.GetValueOrDefault(j.Mode, j.Mode), uncompressed ? "Klasör" : string.Join(", ", j.Formats),
                string.Join(", ", j.Targets), j.Schedule.Describe(), last == null ? "—" : $"{text} · {last.Started[5..16]}",
            }) { Tag = j.Id, UseItemStyleForSubItems = false, Group = GroupFor(j.Group) };
            item.SubItems[5].ForeColor = color;
            List.Items.Add(item);
        }
    }

    private async Task EditAsync(Job job)
    {
        var targets = await Cloud.ListTargetsAsync();
        using var editor = new JobEditor(job, targets);
        if (editor.ShowDialog(this) != DialogResult.OK) return;
        Db.Save(job);
        await TaskPlanner.ApplyAsync(job);
        Reload();
    }

    protected override void OnEdit()
    {
        if (SelectedId is { } id && Db.GetJob(id) is { } job) Guard(() => EditAsync(job));
    }

    private void RunGroup()
    {
        if (SelectedId is not { } id || Db.GetJob(id) is not { } job) { MessageBox.Show("Gruptan bir iş seçin.", "Backup360"); return; }
        if (Ui.Confirm($"\"{job.Group}\" grubundaki işler sırayla çalıştırılsın mı?")) Guard(() => { Launcher.StartGroup(job.Group); return Task.CompletedTask; });
    }

    private void Estimate()
    {
        if (SelectedId is not { } id || Db.GetJob(id) is not { } job) return;
        Guard(async () =>
        {
            var text = await Task.Run(() => Estimator.Describe(job));
            MessageBox.Show(text, $"Aktarım süresi: {job.Name}", MessageBoxButtons.OK, MessageBoxIcon.Information);
        });
    }

    private void Delete()
    {
        if (SelectedId is not { } id || !Ui.Confirm("İş ve zamanlaması silinsin mi? Hedefteki yedekler silinmez.")) return;
        Guard(async () =>
        {
            await TaskPlanner.RemoveAsync(TaskPlanner.JobTask(id));
            Db.DeleteJob(id);
            Reload();
        });
    }
}

public sealed class RulesPage : ListPage
{
    public RulesPage() : base("Klasör Taşıma", "Bir klasöre yeni gelen dosyaları bulup otomatik olarak diğer klasöre taşır.",
        ("Kural", 170), ("Kaynak", 245), ("Hedef", 245), ("Sıklık", 110), ("Son durum", 170))
    {
        Tools.Controls.Add(Ui.Button("Yeni kural", true, (_, _) => Guard(() => EditAsync(new MoveRule()))));
        Tools.Controls.Add(Ui.Button("Düzenle", false, (_, _) => OnEdit()));
        Tools.Controls.Add(Ui.Button("Şimdi çalıştır", false, (_, _) => { if (SelectedId is { } id) Guard(() => { Launcher.Start("rule", id); return Task.CompletedTask; }); }));
        Tools.Controls.Add(Ui.Button("Sil", false, (_, _) => Delete()));
    }

    public override void Reload()
    {
        ClearList();
        foreach (var r in Db.Rules().OrderBy(x => x.Group).ThenBy(x => x.Name))
        {
            var last = Db.LastRun("rule", r.Id);
            var (text, color) = Theme.Status(last?.Status);
            var item = new ListViewItem(new[] { r.Name, r.Source, r.Target, r.Schedule.Describe(), last == null ? "—" : $"{text} · {last.Started[5..16]}" })
                { Tag = r.Id, UseItemStyleForSubItems = false, Group = GroupFor(r.Group) };
            item.SubItems[4].ForeColor = color;
            List.Items.Add(item);
        }
    }

    private async Task EditAsync(MoveRule rule)
    {
        using var editor = new RuleEditor(rule);
        if (editor.ShowDialog(this) != DialogResult.OK) return;
        Db.Save(rule);
        if (editor.NeedsBaseline) await Task.Run(() => MoveEngine.CaptureBaseline(rule));
        await TaskPlanner.ApplyAsync(rule);
        Reload();
    }

    protected override void OnEdit()
    {
        if (SelectedId is { } id && Db.GetRule(id) is { } rule) Guard(() => EditAsync(rule));
    }

    private void Delete()
    {
        if (SelectedId is not { } id || !Ui.Confirm("Kural ve zamanlaması silinsin mi? Taşınmış dosyalara dokunulmaz.")) return;
        Guard(async () =>
        {
            await TaskPlanner.RemoveAsync(TaskPlanner.RuleTask(id));
            Db.DeleteRule(id);
            Reload();
        });
    }
}

public sealed class TargetsPage : ListPage
{
    private readonly Dictionary<string, ConnectionResult> _results = new();

    public TargetsPage() : base("Hedefler", "Bağlantıyı test edin; ölçülen hız aktarım süresi hesabında kullanılır.",
        ("Ad", 190), ("Tür", 170), ("Durum", 250), ("Gecikme", 90), ("Yükleme hızı", 120))
    {
        Tools.Controls.Add(Ui.Button("Google Drive bağla", true, (_, _) => Account("drive", "gdrive")));
        Tools.Controls.Add(Ui.Button("OneDrive bağla", false, (_, _) => Account("onedrive", "onedrive")));
        Tools.Controls.Add(Ui.Button("Dropbox bağla", false, (_, _) => Account("dropbox", "dropbox")));
        Tools.Controls.Add(Ui.Button("Yerel / ağ klasörü", false, (_, _) => Folder()));
        Tools.Controls.Add(Ui.Button("Tümünü test et", false, (_, _) => TestAll()));
        Tools.Controls.Add(Ui.Button("Kaldır", false, (_, _) => Remove()));
    }

    public override async void Reload()
    {
        List<Target> targets;
        try { targets = await Cloud.ListTargetsAsync(); }
        catch (InvalidOperationException e) { Ui.Error(e); return; }
        ClearList();
        foreach (var t in targets)
        {
            _results.TryGetValue(t.Name, out var r);
            var speed = Estimator.KnownSpeed(t.Name);
            var item = new ListViewItem(new[] { t.Name, t.TypeLabel, r?.Message ?? "Test edilmedi", r != null ? $"{r.LatencyMs} ms" : "—", speed is { } v ? $"{Theme.Size(v)}/sn" : "—" }) { UseItemStyleForSubItems = false };
            item.SubItems[2].ForeColor = r == null ? Theme.Muted : r.Ok ? Theme.Success : Theme.Danger;
            List.Items.Add(item);
        }
    }

    private void TestAll() => Guard(async () =>
    {
        var targets = await Cloud.ListTargetsAsync();
        foreach (var t in targets)
        {
            _results[t.Name] = new ConnectionResult(false, "Test ediliyor...", 0, 0);
            Reload();
            _results[t.Name] = await Cloud.TestAsync(t.Name, CancellationToken.None);
        }
        Reload();
    });

    private void Account(string type, string suggested)
    {
        var name = Ui.Prompt("Hesap adı", "Bu hesaba bir ad verin (örn: gdrive, sirket-drive):", suggested);
        if (string.IsNullOrEmpty(name)) return;
        Guard(async () =>
        {
            await Cloud.AddAccountAsync(Cloud.CleanTargetName(name), type, CancellationToken.None);
            Reload();
        });
    }

    private void Folder()
    {
        using var d = new FolderBrowserDialog { Description = "Yedeklerin yazılacağı klasör" };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        var name = Ui.Prompt("Hedef adı", "Bu klasöre bir ad verin:", "yerel");
        if (string.IsNullOrEmpty(name)) return;
        Guard(async () =>
        {
            await Cloud.AddFolderAsync(Cloud.CleanTargetName(name), d.SelectedPath, CancellationToken.None);
            Reload();
        });
    }

    private void Remove()
    {
        if (List.SelectedItems.Count != 1) return;
        var name = List.SelectedItems[0].Text;
        if (!Ui.Confirm($"{name} hedefi kaldırılsın mı? Hedefteki dosyalar silinmez.")) return;
        Guard(async () =>
        {
            await Cloud.RemoveTargetAsync(name);
            Reload();
        });
    }
}

public sealed class HistoryPage : ListPage
{
    public HistoryPage() : base("Geçmiş", "Çalışmaların süresi, aktarılan veri ve ortalama hızı.",
        ("Başlangıç", 140), ("Süre", 90), ("Tür", 65), ("Ad", 160), ("Durum", 75), ("Boyut", 80), ("Hız", 90), ("Ayrıntı", 260))
    {
        Tools.Controls.Add(Ui.Button("Yenile", false, (_, _) => Reload()));
        Tools.Controls.Add(Ui.Button("Günlük klasörü", false, (_, _) => Process.Start("explorer.exe", AppPaths.LogDir)));
    }

    public override void Reload()
    {
        ClearList();
        foreach (var r in Db.RecentRuns())
        {
            var (text, color) = Theme.Status(r.Status);
            var seconds = Theme.RunSeconds(r);
            var item = new ListViewItem(new[]
            {
                r.Started, r.Ended == null ? "" : Theme.Duration((long)seconds), r.Kind == "job" ? "Yedek" : "Taşıma", r.Name, text,
                r.Bytes > 0 ? Theme.Size(r.Bytes) : "", r.Bytes > 0 && seconds > 0 ? $"{Theme.Size(r.Bytes / seconds)}/sn" : "", r.Detail,
            }) { UseItemStyleForSubItems = false };
            item.SubItems[4].ForeColor = color;
            List.Items.Add(item);
        }
    }
}

public sealed class SettingsPage : Panel, IRefreshable
{
    private readonly string _user;
    private readonly ListBox _users = new() { Width = 300, Height = 90 };
    private readonly Label _tools = Ui.Label("", Theme.Body, Theme.Muted);
    private readonly CheckBox _mailOn = new() { Text = "E-posta bildirimlerini aç (hata olduğunda her zaman gönderilir)", AutoSize = true };
    private readonly CheckBox _mailWarn = new() { Text = "Uyarılarda da gönder", AutoSize = true };
    private readonly CheckBox _mailOk = new() { Text = "Başarılı işlerde de gönder", AutoSize = true };
    private readonly TextBox _host = new() { Width = 260 };
    private readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535, Width = 80 };
    private readonly ComboBox _security = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
    private readonly TextBox _smtpUser = new() { Width = 260 };
    private readonly TextBox _smtpPass = new() { Width = 200, UseSystemPasswordChar = true, PlaceholderText = "Değiştirmek için yazın" };
    private readonly TextBox _from = new() { Width = 260 };
    private readonly TextBox _to = new() { Width = 400, PlaceholderText = "virgülle birden çok adres" };
    private readonly Label _mailResult = Ui.Label("", Theme.Small, Theme.Muted);
    private static readonly string[] Securities = { "auto", "ssl", "starttls", "none" };

    public SettingsPage(string user)
    {
        _user = user;
        BackColor = Theme.Bg;
        Padding = new Padding(28, 22, 28, 22);
        AutoScroll = true;
        var t = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        t.Controls.Add(Ui.Label("Ayarlar", Theme.Heading));

        t.Controls.Add(Ui.Label("E-posta bildirimleri (SMTP)", Theme.Bold));
        t.Controls.Add(_mailOn);
        var r1 = new FlowLayoutPanel { AutoSize = true };
        _security.Items.AddRange(new object[] { "Otomatik", "SSL/TLS", "STARTTLS", "Yok" });
        r1.Controls.AddRange(new Control[] { Ui.Label("Sunucu"), _host, Ui.Label("Port"), _port, Ui.Label("Güvenlik"), _security });
        t.Controls.Add(r1);
        var r2 = new FlowLayoutPanel { AutoSize = true };
        r2.Controls.AddRange(new Control[] { Ui.Label("Kullanıcı"), _smtpUser, Ui.Label("Parola"), _smtpPass });
        t.Controls.Add(r2);
        var r3 = new FlowLayoutPanel { AutoSize = true };
        r3.Controls.AddRange(new Control[] { Ui.Label("Gönderen"), _from, Ui.Label("Alıcı"), _to });
        t.Controls.Add(r3);
        var r4 = new FlowLayoutPanel { AutoSize = true };
        r4.Controls.AddRange(new Control[] { _mailWarn, _mailOk });
        t.Controls.Add(r4);
        var r5 = new FlowLayoutPanel { AutoSize = true };
        r5.Controls.Add(Ui.Button("Kaydet", true, (_, _) => SaveMail()));
        r5.Controls.Add(Ui.Button("Test e-postası gönder", false, async (_, _) => await TestMailAsync()));
        r5.Controls.Add(_mailResult);
        t.Controls.Add(r5);

        t.Controls.Add(Ui.Label("Kullanıcılar", Theme.Bold));
        t.Controls.Add(_users);
        var r6 = new FlowLayoutPanel { AutoSize = true };
        r6.Controls.Add(Ui.Button("Parolamı değiştir", false, (_, _) => ChangePassword()));
        r6.Controls.Add(Ui.Button("Kullanıcı ekle", false, (_, _) => AddUser()));
        r6.Controls.Add(Ui.Button("Kullanıcıyı sil", false, (_, _) => DeleteUser()));
        t.Controls.Add(r6);

        t.Controls.Add(Ui.Label("Taşınabilir veritabanı (SQLite)", Theme.Bold));
        t.Controls.Add(Ui.Label(AppPaths.Database, Theme.Small, Theme.Muted));
        t.Controls.Add(Ui.Label("Uygulama klasörünü kopyalamanız yeterlidir. Parolalar güvenlik gereği bu Windows hesabına bağlıdır, yeni bilgisayarda yeniden girilir.", Theme.Small, Theme.Muted));
        var r7 = new FlowLayoutPanel { AutoSize = true };
        r7.Controls.Add(Ui.Button("Veritabanını dışa aktar", false, (_, _) => Export()));
        r7.Controls.Add(Ui.Button("Veri klasörünü aç", false, (_, _) => Process.Start("explorer.exe", AppPaths.DataDir)));
        t.Controls.Add(r7);
        t.Controls.Add(Ui.Label("Araçlar", Theme.Bold));
        t.Controls.Add(_tools);
        Controls.Add(t);
    }

    public void Reload()
    {
        _users.Items.Clear();
        _users.Items.AddRange(Auth.Users().Cast<object>().ToArray());
        static string S(string? p) => p != null ? "hazır" : "bulunamadı";
        _tools.Text = $"7-Zip: {S(Tools.SevenZip())}   ·   rclone: {S(Tools.Rclone())}   ·   WinRAR: {S(Tools.Rar())}   ·   mysqldump: {S(MySqlEngine.BinDir())}";

        var s = Notifier.Load();
        _mailOn.Checked = s.Enabled;
        _mailWarn.Checked = s.OnWarning;
        _mailOk.Checked = s.OnSuccess;
        _host.Text = s.Host;
        _port.Value = Math.Clamp(s.Port, 1, 65535);
        _security.SelectedIndex = Math.Max(0, Array.IndexOf(Securities, s.Security));
        _smtpUser.Text = s.User;
        _from.Text = s.From;
        _to.Text = s.To;
    }

    private SmtpSettings ReadMail()
    {
        var current = Notifier.Load();
        return new SmtpSettings
        {
            Enabled = _mailOn.Checked, OnWarning = _mailWarn.Checked, OnSuccess = _mailOk.Checked,
            Host = _host.Text.Trim(), Port = (int)_port.Value, Security = Securities[Math.Max(0, _security.SelectedIndex)],
            User = _smtpUser.Text.Trim(), From = _from.Text.Trim(), To = _to.Text.Trim(),
            PasswordEnc = _smtpPass.Text.Length > 0 ? Secrets.Protect(_smtpPass.Text) : current.PasswordEnc,
        };
    }

    private void SaveMail()
    {
        Notifier.Save(ReadMail());
        _smtpPass.Clear();
        _mailResult.ForeColor = Theme.Success;
        _mailResult.Text = "Kaydedildi.";
    }

    private async Task TestMailAsync()
    {
        _mailResult.ForeColor = Theme.Muted;
        _mailResult.Text = "Gönderiliyor...";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            await Notifier.SendAsync(ReadMail(), "[Backup360] Test e-postası", $"SMTP ayarlarınız çalışıyor.\nBilgisayar: {Environment.MachineName}", cts.Token);
            _mailResult.ForeColor = Theme.Success;
            _mailResult.Text = "Test e-postası gönderildi.";
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _mailResult.ForeColor = Theme.Danger;
            _mailResult.Text = e.Message;
        }
    }

    private static void Try(Action a)
    {
        try { a(); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException) { Ui.Error(e); }
    }

    private void ChangePassword() => Try(() =>
    {
        var p = PasswordPrompt("Yeni parola");
        if (p != null) { Auth.ChangePassword(_user, p); MessageBox.Show("Parola değiştirildi."); }
    });

    private void AddUser() => Try(() =>
    {
        var name = Ui.Prompt("Kullanıcı ekle", "Kullanıcı adı:");
        if (string.IsNullOrEmpty(name)) return;
        var p = PasswordPrompt("Parola");
        if (p != null) { Auth.AddUser(name, p); Reload(); }
    });

    private void DeleteUser() => Try(() =>
    {
        if (_users.SelectedItem is not string name) return;
        if (string.Equals(name, _user, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Açık oturumdaki kullanıcı silinemez.");
        if (Ui.Confirm($"{name} silinsin mi?")) { Auth.DeleteUser(name); Reload(); }
    });

    private static string? PasswordPrompt(string title)
    {
        using var f = new Form { Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false, ClientSize = new Size(360, 130), Font = Theme.Body, BackColor = Theme.Surface };
        var t = new TextBox { Location = new Point(16, 40), Width = 328, UseSystemPasswordChar = true };
        var ok = Ui.Button("Tamam", true);
        ok.Location = new Point(262, 84);
        ok.DialogResult = DialogResult.OK;
        f.Controls.AddRange(new Control[] { new Label { Text = $"En az {Auth.MinPasswordLength} karakter:", Location = new Point(16, 14), AutoSize = true }, t, ok });
        f.AcceptButton = ok;
        return f.ShowDialog() == DialogResult.OK ? t.Text : null;
    }

    private void Export() => Try(() =>
    {
        using var d = new SaveFileDialog { FileName = $"backup360-{DateTime.Now:yyyyMMdd}.db", Filter = "SQLite veritabanı|*.db" };
        if (d.ShowDialog(this) == DialogResult.OK) { Db.ExportTo(d.FileName); MessageBox.Show("Veritabanı dışa aktarıldı."); }
    });
}

public sealed class AboutPage : Panel
{
    public AboutPage()
    {
        BackColor = Theme.Bg;
        Padding = new Padding(28, 40, 28, 20);
        var t = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        t.Controls.Add(new PictureBox { Image = Theme.Logo, SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(104, 104), Margin = new Padding(0, 0, 0, 10) });
        t.Controls.Add(Ui.Label("Backup360", Theme.Big));
        t.Controls.Add(Ui.Label($"Sürüm {AppInfo.Version} · Yapı kodu {AppInfo.BuildCode}", Theme.Body, Theme.Muted));
        t.Controls.Add(Ui.Label("Ücretsiz sürüm, para ile satılamaz", Theme.Body, Theme.Muted));
        t.Controls.Add(Ui.Label(AppInfo.Author, Theme.Bold));
        t.Controls.Add(Ui.MailRow());
        foreach (var (text, url) in new[]
                 {
                     ("LinkedIn", AppInfo.LinkedIn), ("Twitter", AppInfo.Twitter), ("GitHub", AppInfo.GitHub), ("Son sürümü indir (GitHub Releases)", AppInfo.Releases),
                 })
        {
            var link = new LinkLabel { Text = text, AutoSize = true, Font = Theme.Body, LinkColor = Theme.Primary, Margin = new Padding(0, 6, 0, 0) };
            link.LinkClicked += (_, _) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            t.Controls.Add(link);
        }
        Controls.Add(t);
    }
}
