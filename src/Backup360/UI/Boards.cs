using System.Windows;
using System.Windows.Controls;
using Backup360.Core;
using WUI = Wpf.Ui.Controls;

namespace Backup360.UI;

public abstract class BoardPage : Page
{
    protected readonly BoardControl Board = new();
    private readonly System.Windows.Threading.DispatcherTimer _liveTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly Dictionary<string, LiveCardVm> _liveCards = new();
    private readonly HashSet<string> _seen = new();

    protected virtual string? LiveKind => null;

    protected BoardPage()
    {
        Content = Board;
        _liveTimer.Tick += (_, _) => PollLive();
        Loaded += (_, _) =>
        {
            Ui.Guard(ReloadAsync);
            if (LiveKind != null) _liveTimer.Start();
        };
        Unloaded += (_, _) => _liveTimer.Stop();
    }

    protected LiveCardVm LiveFor(string kind, long id)
    {
        var key = $"{kind}-{id}";
        if (!_liveCards.TryGetValue(key, out var vm))
            _liveCards[key] = vm = new LiveCardVm(new RelayCommand(() => Launcher.Cancel(kind, id)));
        return vm;
    }

    /// <summary>İşi başlatır ve kullanıcıya hemen geri bildirim verir.</summary>
    protected void StartRun(string kind, long id, string name)
    {
        var vm = LiveFor(kind, id);
        if (vm.IsActive)
        {
            Board.Notify($"{name} zaten çalışıyor; ilerlemeyi kartta izleyebilirsiniz.", WUI.InfoBarSeverity.Informational);
            return;
        }
        vm.Begin();
        Ui.Guard(() => { Launcher.Start(kind, id); return Task.CompletedTask; });
        Board.Notify($"{name} başlatıldı. İlerleme kartın üzerinde görünüyor.", WUI.InfoBarSeverity.Informational);
    }

    private void PollLive()
    {
        var states = LiveFiles.ReadAll().Where(s => s.Kind == LiveKind).ToDictionary(s => s.Key);
        var finished = new List<(string Key, bool Seen, DateTime Began)>();
        foreach (var (key, vm) in _liveCards)
        {
            if (states.TryGetValue(key, out var state))
            {
                vm.Update(state);
                _seen.Add(key);
            }
            else if (vm.IsActive && (_seen.Contains(key) || vm.PendingExpired))
            {
                finished.Add((key, _seen.Remove(key), vm.StartedAt));
                vm.Hide();
            }
        }
        if (finished.Count == 0) return;
        foreach (var (key, seen, began) in finished) Announce(key, seen, began);
        Ui.Guard(ReloadAsync);
    }

    private void Announce(string key, bool seen, DateTime began)
    {
        var cut = key.LastIndexOf('-');
        var (kind, id) = (key[..cut], long.Parse(key[(cut + 1)..]));
        var last = Db.LastRun(kind, id);
        var fresh = last != null && (seen || (DateTime.TryParse(last.Started, out var d) && d >= began.AddSeconds(-2)));
        if (!fresh)
        {
            Board.Notify("İşlem bitti; yapılacak yeni bir değişiklik bulunmadı.", WUI.InfoBarSeverity.Informational);
            return;
        }
        var (text, _) = Fmt.Status(last!.Status);
        var severity = last.Status switch { "success" => WUI.InfoBarSeverity.Success, "warning" => WUI.InfoBarSeverity.Warning, "failed" => WUI.InfoBarSeverity.Error, _ => WUI.InfoBarSeverity.Informational };
        Board.Notify($"{last.Name}: {text}. {last.Detail}", severity);
    }

    protected abstract Task ReloadAsync();
    protected Window OwnerWindow => Ui.Owner(this);
    protected static string LastTime(RunRecord? last) => last == null ? "" : $"  ·  {last.Started[5..16]}";
}

public abstract class JobsPageBase : BoardPage
{
    private readonly string _section;

    private static readonly Dictionary<string, string> ModeNames = new() { ["archive"] = "Sıkıştırılmış", ["dated"] = "Tarih bazlı", ["mirror"] = "Klasör bazlı", ["sync"] = "Eşitleme" };
    private static readonly Dictionary<string, string> EngineNames = new() { ["mssql"] = "SQL Server", ["mysql"] = "MySQL", ["sqlite"] = "SQLite" };

    protected override string? LiveKind => "job";

    protected JobsPageBase(string section, string title, string hint)
    {
        _section = section;
        Board.Setup(title, hint, "Henüz iş yok. Yeni iş ile başlayın.");
        Board.AddTop("Yeni iş", WUI.SymbolRegular.Add24, () => Ui.Guard(() => EditAsync(NewJob())), primary: true);
    }

    private Job NewJob() => _section switch
    {
        "sql" => new Job { Mode = "database", DbPort = 1433 },
        "folder-sync" => new Job { Mode = "sync", SyncKind = "folder", RemoteFolder = "", MirrorDelete = true },
        "sql-sync" => new Job { Mode = "sync", SyncKind = "sql", RemoteFolder = "SQL-Yedekler", MirrorDelete = true },
        _ => new Job { Mode = "mirror", DatedOutput = "folder" },
    };

    private bool Belongs(Job j) => _section switch
    {
        "sql" => j.Mode == "database",
        "folder-sync" => j.Mode == "sync" && j.SyncKind == "folder",
        "sql-sync" => j.Mode == "sync" && j.SyncKind == "sql",
        _ => j.Mode is "mirror" or "archive" or "dated",
    };

    private static (WUI.SymbolRegular Icon, System.Windows.Media.Brush Accent) Look(Job j) => j.Mode switch
    {
        "database" => (WUI.SymbolRegular.Database24, Fmt.Violet),
        "sync" => (WUI.SymbolRegular.ArrowSync24, Fmt.Success),
        "dated" => (WUI.SymbolRegular.CalendarLtr24, Fmt.Teal),
        "archive" => (WUI.SymbolRegular.Archive24, Fmt.Indigo),
        _ => (WUI.SymbolRegular.Folder24, Fmt.Success),
    };

    protected override Task ReloadAsync()
    {
        var cards = Db.Jobs().Where(Belongs).OrderBy(j => j.Group).ThenBy(j => j.Name).Select(j =>
        {
            var last = Db.LastRun("job", j.Id);
            var (status, color) = Fmt.Status(last?.Status);
            var (icon, accent) = Look(j);
            var uncompressed = j.Mode is "mirror" or "sync" || (j.Mode == "dated" && j.DatedOutput == "folder");
            var kind = j.Mode == "database" ? EngineNames[j.DbEngine] : ModeNames.GetValueOrDefault(j.Mode, j.Mode);
            var source = j.Mode == "sync" && j.SyncFromJob != 0
                ? "Yedek işi: " + (Db.GetJob(j.SyncFromJob)?.Name ?? "bulunamadı")
                : j.Mode == "database"
                ? (j.DbEngine == "sqlite" ? string.Join(", ", j.DbNames) : j.DbServer)
                : j.Sources.FirstOrDefault() + (j.Sources.Count > 1 ? $"  +{j.Sources.Count - 1}" : "");
            var chips = new List<string> { kind };
            chips.AddRange(uncompressed ? new[] { "Sıkıştırmasız" } : j.Formats.Take(3));
            chips.AddRange(j.Targets.Take(2));
            var actions = new[]
            {
                new CardAction("Şimdi çalıştır", WUI.SymbolRegular.Play24, new RelayCommand(() => StartRun("job", j.Id, j.Name))),
                new CardAction("Düzenle", WUI.SymbolRegular.Edit24, new RelayCommand(() => Ui.Guard(() => EditAsync(j)))),
                j.Mode == "sync"
                    ? new CardAction("Farkları göster", WUI.SymbolRegular.BranchCompare24, new RelayCommand(() => new DiffWindow(j) { Owner = OwnerWindow }.ShowDialog()))
                    : new CardAction("Süre hesapla", WUI.SymbolRegular.Timer24, new RelayCommand(() => Ui.Guard(() => EstimateAsync(j)))),
                new CardAction("Sil", WUI.SymbolRegular.Delete24, new RelayCommand(() => Ui.Guard(() => DeleteAsync(j))), true),
            };
            return (Group: j.Group, Card: new CardVm(icon, accent, j.Name, source ?? "", chips.ToArray(), status, color, j.Schedule.Describe() + LastTime(last), actions) { Live = LiveFor("job", j.Id) });
        }).ToList();

        Board.SetGroups(cards.GroupBy(c => c.Group, c => c.Card, StringComparer.OrdinalIgnoreCase),
            g => new CardAction("Grubu çalıştır", WUI.SymbolRegular.PlayCircle24, new RelayCommand(() => Ui.Guard(() => RunGroupAsync(g)))));
        return Task.CompletedTask;
    }

    private async Task RunGroupAsync(string group)
    {
        if (await Ui.Confirm($"\"{group}\" grubundaki işler sırayla çalıştırılsın mı?")) Launcher.StartGroup(group);
    }

    private async Task EditAsync(Job job)
    {
        var targets = await Cloud.ListTargetsAsync();
        Window editor = job.Mode == "sync" ? new SyncEditorWindow(job, targets) : new JobEditorWindow(job, targets, _section);
        editor.Owner = OwnerWindow;
        if (editor.ShowDialog() != true) return;
        Db.Save(job);
        await TaskPlanner.ApplyAsync(job);
        await ReloadAsync();
    }

    private static async Task EstimateAsync(Job job)
    {
        var text = await Task.Run(() => Estimator.Describe(job));
        await Ui.Info(text, $"Aktarım süresi: {job.Name}");
    }

    private async Task DeleteAsync(Job job)
    {
        if (!await Ui.Confirm($"\"{job.Name}\" ve zamanlaması silinsin mi? Hedefteki yedekler silinmez.")) return;
        await TaskPlanner.RemoveAsync(TaskPlanner.JobTask(job.Id));
        Db.DeleteJob(job.Id);
        await ReloadAsync();
    }
}

public sealed class FolderBackupPage() : JobsPageBase("folder", "Klasör Yedekleme", "Dosya ve klasörleri buluta, klasör olarak veya sıkıştırılmış yedekleyin.");
public sealed class FolderSyncPage() : JobsPageBase("folder-sync", "Klasör Eşitleme", "Klasörleri başka klasöre, NAS'a veya farklı PC'ye yalnızca farkları aktararak eşitleyin.");
public sealed class SqlBackupPage() : JobsPageBase("sql", "SQL Yedekleme", "SQL Server (Always On dahil), MySQL/MariaDB ve SQLite yedekleri.");
public sealed class SqlSyncPage() : JobsPageBase("sql-sync", "SQL Eşitleme", "SQL yedeklerini başka klasöre, NAS'a veya farklı PC'ye yalnızca farkları aktararak eşitleyin.");

public sealed class RulesPage : BoardPage
{
    protected override string? LiveKind => "rule";

    public RulesPage()
    {
        Board.Setup("Klasör Taşıma", "Bir klasöre yeni gelen dosyaları bulup otomatik olarak diğer klasöre taşır.", "Henüz kural yok. Yeni kural ile başlayın.");
        Board.AddTop("Yeni kural", WUI.SymbolRegular.Add24, () => Ui.Guard(() => EditAsync(new MoveRule())), primary: true);
    }

    protected override Task ReloadAsync()
    {
        var cards = Db.Rules().OrderBy(r => r.Group).ThenBy(r => r.Name).Select(r =>
        {
            var last = Db.LastRun("rule", r.Id);
            var (status, color) = Fmt.Status(last?.Status);
            var chips = new[] { r.Pattern, r.OnlyNew ? "Yalnızca yeni" : "Tüm dosyalar", r.Recursive ? "Alt klasörler" : "" }.Where(x => x.Length > 0).ToArray();
            var actions = new[]
            {
                new CardAction("Şimdi çalıştır", WUI.SymbolRegular.Play24, new RelayCommand(() => StartRun("rule", r.Id, r.Name))),
                new CardAction("Düzenle", WUI.SymbolRegular.Edit24, new RelayCommand(() => Ui.Guard(() => EditAsync(r)))),
                new CardAction("Sil", WUI.SymbolRegular.Delete24, new RelayCommand(() => Ui.Guard(() => DeleteAsync(r))), true),
            };
            return (Group: r.Group, Card: new CardVm(WUI.SymbolRegular.FolderArrowRight24, Fmt.Indigo, r.Name, $"{r.Source}  →  {r.Target}", chips, status, color, r.Schedule.Describe() + LastTime(last), actions) { Live = LiveFor("rule", r.Id) });
        }).ToList();
        Board.SetGroups(cards.GroupBy(c => c.Group, c => c.Card, StringComparer.OrdinalIgnoreCase));
        return Task.CompletedTask;
    }

    private async Task EditAsync(MoveRule rule)
    {
        var editor = new RuleEditorWindow(rule) { Owner = OwnerWindow };
        if (editor.ShowDialog() != true) return;
        Db.Save(rule);
        if (editor.NeedsBaseline) await Task.Run(() => MoveEngine.CaptureBaseline(rule));
        await TaskPlanner.ApplyAsync(rule);
        await ReloadAsync();
    }

    private async Task DeleteAsync(MoveRule rule)
    {
        if (!await Ui.Confirm($"\"{rule.Name}\" ve zamanlaması silinsin mi? Taşınmış dosyalara dokunulmaz.")) return;
        await TaskPlanner.RemoveAsync(TaskPlanner.RuleTask(rule.Id));
        Db.DeleteRule(rule.Id);
        await ReloadAsync();
    }
}

public sealed class TargetsPage : BoardPage
{
    private readonly Dictionary<string, ConnectionResult> _results = new();
    private readonly Dictionary<string, LiveCardVm> _tests = new();
    private LiveCardVm TestVm(string name) => _tests.TryGetValue(name, out var vm) ? vm : _tests[name] = new LiveCardVm();

    public TargetsPage()
    {
        Board.Setup("Hedefler", "Tarayıcı, token veya şifre ile hedef tanımlayın. Ölçülen hız aktarım süresi hesabında kullanılır.", "Henüz hedef yok. Yeni hedef tanımla ile başlayın.");
        Board.AddTop("Tümünü test et", WUI.SymbolRegular.PlugConnected24, () => Ui.Guard(TestAllAsync));
        Board.AddTop("Yeni hedef tanımla", WUI.SymbolRegular.Add24, () => Ui.Guard(DefineAsync), primary: true);
    }

    private static WUI.SymbolRegular Icon(string type) => type switch
    {
        "alias" or "local" => WUI.SymbolRegular.Folder24,
        "sftp" or "ftp" or "smb" => WUI.SymbolRegular.Server24,
        _ => WUI.SymbolRegular.Cloud24,
    };

    protected override async Task ReloadAsync()
    {
        var targets = await Cloud.ListTargetsAsync();
        var cards = targets.Select(t =>
        {
            _results.TryGetValue(t.Name, out var r);
            var speed = Estimator.KnownSpeed(t.Name);
            var testing = TestVm(t.Name).IsActive;
            var status = testing ? "Test ediliyor" : r == null ? "Test edilmedi" : r.Ok ? "Bağlı" : "Hata";
            var color = r == null ? Fmt.Neutral : r.Ok ? Fmt.Success : Fmt.Danger;
            var footer = (speed is { } v ? $"{Fmt.Size(v)}/sn" : "Hız ölçülmedi") + (r is { Ok: true } ? $"  ·  {r.LatencyMs} ms" : "");
            var actions = new[]
            {
                new CardAction("Bağlantıyı test et", WUI.SymbolRegular.PlugConnected24, new RelayCommand(() => Ui.Guard(() => TestAsync(t.Name)))),
                new CardAction("Klasör oluştur", WUI.SymbolRegular.FolderAdd24, new RelayCommand(() => Ui.Guard(() => MakeFolderAsync(t.Name)))),
                new CardAction("Kaldır", WUI.SymbolRegular.Delete24, new RelayCommand(() => Ui.Guard(() => RemoveAsync(t.Name))), true),
            };
            return (Group: t.TypeLabel, Card: new CardVm(Icon(t.Type), Fmt.Indigo, t.Name, r is { Ok: false } ? r.Message : t.TypeLabel, new[] { t.Type }, status, color, footer, actions) { Logo = Brand.For(t.Type), Live = TestVm(t.Name) });
        }).ToList();
        Board.SetGroups(cards.GroupBy(c => c.Group, c => c.Card));
    }

    private async Task DefineAsync()
    {
        var editor = new TargetEditorWindow { Owner = OwnerWindow };
        if (editor.ShowDialog() != true) return;
        var connect = new ConnectWindow(editor.Definition) { Owner = OwnerWindow };
        connect.ShowDialog();
        await ReloadAsync();
        if (connect.Error != null) await Ui.Info(connect.Error, "Hedef tanımlanamadı");
    }

    private async Task TestAsync(string name)
    {
        var vm = TestVm(name);
        if (vm.IsActive)
        {
            Board.Notify($"{name} zaten test ediliyor, sonucu bekleyin.", WUI.InfoBarSeverity.Informational);
            return;
        }
        vm.Begin("Teste hazırlanılıyor");
        await ReloadAsync();
        ConnectionResult result;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        try { result = await Cloud.TestAsync(name, cts.Token, vm.Step); }
        catch (OperationCanceledException) { result = new ConnectionResult(false, "Zaman aşımı: hedef 3 dakika içinde yanıt vermedi.", 0, 0); }
        finally { vm.Hide(); }
        _results[name] = result;
        await ReloadAsync();
        Board.Notify(result.Ok ? $"{name}: Bağlantı başarılı. Yükleme {Fmt.Size(result.UploadBytesPerSecond)}/sn, gecikme {result.LatencyMs} ms." : $"{name}: {result.Message}",
            result.Ok ? WUI.InfoBarSeverity.Success : WUI.InfoBarSeverity.Error);
    }

    private async Task TestAllAsync()
    {
        foreach (var t in await Cloud.ListTargetsAsync()) await TestAsync(t.Name);
    }

    private async Task MakeFolderAsync(string target)
    {
        var path = Ui.Prompt(OwnerWindow, "Klasör oluştur", $"{target} hedefinde oluşturulacak klasör (örn: Yedekler/Muhasebe):", "Backup360");
        if (string.IsNullOrEmpty(path)) return;
        await Cloud.CreateFolderAsync(target, path, CancellationToken.None);
        await Ui.Info($"Klasör oluşturuldu: {target}:{path}");
    }

    private async Task RemoveAsync(string name)
    {
        if (!await Ui.Confirm($"{name} hedefi kaldırılsın mı? Hedefteki dosyalar silinmez.")) return;
        await Cloud.RemoveTargetAsync(name);
        _results.Remove(name);
        await ReloadAsync();
    }
}
