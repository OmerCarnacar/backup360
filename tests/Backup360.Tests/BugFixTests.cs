using Backup360.Core;
using Xunit;

namespace Backup360.Tests;

/// <summary>Gerçek kullanımın kayıtlarında bulunan kusurlar.</summary>
public class BugFixTests
{
    [Fact]
    public void File_deleted_while_compressing_is_not_a_missing_backup()
    {
        using var s = new Sandbox();
        var kept = Path.Combine(s.Root, "duruyor.js");
        File.WriteAllText(kept, "x");
        Assert.False(Archiver.IsGone(kept + " : Dosya kullanılıyor."));
        Assert.True(Archiver.IsGone(Path.Combine(s.Root, "assets", "Dashboard-8MglQFfA.js") + " : Sistem belirtilen dosyayı bulamıyor."));
        // Sürücü hiç yoksa dosya "silinmiş" değil, erişilemezdir.
        var missingDrive = Enumerable.Range('D', 23).Select(c => (char)c + @":\").First(d => !Directory.Exists(d));
        Assert.False(Archiver.IsGone(missingDrive + @"Veri\a.txt : Sistem belirtilen yolu bulamıyor."));
        Assert.Null(Problems.Classify("  not: 288 dosya sıkıştırma sırasında kaynaktan silindi, atlandı (ör. D:\\a\\b.js)"));
    }

    [Fact]
    public void Windows_ending_the_session_is_recognised_and_negative_exit_codes_are_errors()
    {
        Assert.True(Archiver.IsSessionEnding(1073807364));               // 0x40010004: oturum kapanırken sonlandırıldı
        Assert.True(Archiver.IsSessionEnding(unchecked((int)0xC0000142))); // kapanış sırasında yeni süreç başlatılamadı
        Assert.False(Archiver.IsSessionEnding(0));
        Assert.False(Archiver.IsSessionEnding(1));
        Assert.False(Archiver.IsSessionEnding(2));
        Assert.False(Archiver.IsSessionEnding(8));
        Assert.IsAssignableFrom<OperationCanceledException>(new SessionEndingException());
    }

    [Fact]
    public void Scheduled_run_cut_short_by_shutdown_is_resumed_but_not_forever()
    {
        using var s = new Sandbox();
        var job = new Job { Name = "Gunluk", Mode = "archive", Sources = new() { s.Tree("V") }, Formats = new() { "zip" }, Targets = new() { "gdrive" }, Schedule = new Schedule { Type = "daily", Time = "16:00" } };
        Db.Save(job);
        Db.SetSetting(TaskPlanner.SinceKey(TaskPlanner.JobTask(job.Id)), "2026-11-10T09:00:00.0000000");
        var morning = new DateTime(2026, 11, 18, 8, 10, 0);

        void Add(string started, string status, string detail)
        {
            var id = Db.StartRun("job", job.Id, job.Name);
            if (status != "running") Db.FinishRun(id, status, detail, 0);
            Db.Exec("UPDATE runs SET started=$s WHERE id=$i", ("$s", started), ("$i", id));
        }

        // Dün 16:00'da başladı, tamamlandı: yapılacak iş yok.
        Add("2026-11-17 16:00:02", "success", "");
        Assert.Empty(TaskPlanner.Overdue(morning));
        Db.Exec("DELETE FROM runs");

        // Dün 16:00'da başladı, bilgisayar kapanınca yarıda kaldı: sabah sürdürülür.
        Add("2026-11-17 16:00:02", "failed", Db.Interrupted);
        Assert.Single(TaskPlanner.Overdue(morning));
        Db.Exec("DELETE FROM runs");

        // Kayıt "çalışıyor" kalmış ama çalışan süreç yok: o da yarıda kalmıştır.
        Add("2026-11-17 16:00:02", "running", "");
        Assert.Single(TaskPlanner.Overdue(morning));
        using (RunGate.TryEnter($"job-{job.Id}")) Assert.Empty(TaskPlanner.Overdue(morning));
        Db.Exec("DELETE FROM runs");

        // Gerçek bir hatayla bitti: kendiliğinden yeniden denenmez.
        Add("2026-11-17 16:00:02", "failed", "Hedef bağlı değil");
        Assert.Empty(TaskPlanner.Overdue(morning));

        // Üç kez yarıda kaldıysa daha fazla denenmez.
        Db.Exec("DELETE FROM runs");
        Add("2026-11-17 16:00:02", "failed", Db.Interrupted);
        Add("2026-11-18 07:55:00", "failed", Db.Interrupted);
        Assert.Single(TaskPlanner.Overdue(morning));
        Add("2026-11-18 08:05:00", "failed", Db.Interrupted);
        Assert.Empty(TaskPlanner.Overdue(morning));
    }

    [Fact]
    public void Two_jobs_writing_the_log_at_once_do_not_overwrite_each_other()
    {
        using var s = new Sandbox();
        using (var a = new RunLog("A", null))
        using (var b = new RunLog("B", null))
        {
            Parallel.Invoke(
                () => { for (var i = 0; i < 400; i++) a.Write($"a-satiri-{i:000}-sonu"); },
                () => { for (var i = 0; i < 400; i++) b.Write($"b-satiri-{i:000}-sonu"); });
        }
        var lines = File.ReadAllLines(Directory.GetFiles(AppPaths.LogDir, "backup360-*.log").Single());
        Assert.Equal(802, lines.Length);
        Assert.Equal(400, lines.Count(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d  a-satiri-\d{3}-sonu$")));
        Assert.Equal(400, lines.Count(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d  b-satiri-\d{3}-sonu$")));
    }

    [Fact]
    public void Missed_scheduled_backup_is_caught_up_but_never_for_a_time_before_the_schedule_was_set()
    {
        Assert.Equal(new DateTime(2026, 11, 15, 16, 0, 0), TaskPlanner.LastDue(new Schedule { Type = "daily", Time = "16:00" }, new DateTime(2026, 11, 16, 8, 5, 0)));
        Assert.Equal(new DateTime(2026, 11, 16, 16, 0, 0), TaskPlanner.LastDue(new Schedule { Type = "daily", Time = "16:00" }, new DateTime(2026, 11, 16, 16, 0, 0)));
        Assert.Equal(new DateTime(2026, 11, 13, 9, 30, 0), TaskPlanner.LastDue(new Schedule { Type = "weekly", Days = "Monday,Friday", Time = "09:30" }, new DateTime(2026, 11, 16, 8, 0, 0)));
        Assert.Null(TaskPlanner.LastDue(new Schedule { Type = "hourly", Interval = 2 }, new DateTime(2026, 11, 16, 8, 0, 0)));
        Assert.Null(TaskPlanner.LastDue(new Schedule(), new DateTime(2026, 11, 16, 8, 0, 0)));

        using var s = new Sandbox();
        var job = new Job { Name = "Gunluk", Mode = "archive", Sources = new() { s.Tree("V") }, Formats = new() { "zip" }, Targets = new() { "gdrive" }, Schedule = new Schedule { Type = "daily", Time = "16:00" } };
        Db.Save(job);
        var monday = new DateTime(2026, 11, 16, 8, 5, 0);
        var since = TaskPlanner.SinceKey(TaskPlanner.JobTask(job.Id));
        // Güncellemeden önce kaydedilmiş zamanlama: ilk bakışta iş başlatılmaz, takip o andan başlar.
        Assert.Empty(TaskPlanner.Overdue(monday));
        Assert.Empty(TaskPlanner.Overdue(monday.AddMinutes(15)));
        // Zamanlama o saatten önce kaydedildiyse hiç çalışmamış iş de telafi edilir.
        Db.SetSetting(since, "2026-11-10T09:00:00.0000000");
        Assert.Single(TaskPlanner.Overdue(monday));
        // Zamanlama o saatten sonra kaydedildiyse edilmez.
        Db.SetSetting(since, "2026-11-15T17:00:00.0000000");
        Assert.Empty(TaskPlanner.Overdue(monday));

        var run = Db.StartRun("job", job.Id, job.Name);
        Db.FinishRun(run, "success", "", 0);
        Db.Exec("UPDATE runs SET started='2026-11-13 16:00:02'");
        Db.SetSetting(since, "2026-11-10T09:00:00.0000000");
        Assert.Equal(job.Id, Assert.Single(TaskPlanner.Overdue(monday)).Id);
        // Saati yeni geçmişse Windows'un kendi başlatmasına süre tanınır.
        Db.Exec("UPDATE runs SET started='2026-11-15 16:00:01'");
        Assert.Empty(TaskPlanner.Overdue(monday));
        Assert.Empty(TaskPlanner.Overdue(new DateTime(2026, 11, 16, 16, 10, 0)));
        Assert.Single(TaskPlanner.Overdue(new DateTime(2026, 11, 16, 16, 30, 0)));

        // Haftalık denetim Pazar 03:00'te bilgisayar kapalıyken kaçtıysa o da telafi edilir.
        Db.SetSetting("audit_schedule", System.Text.Json.JsonSerializer.Serialize(new Schedule { Type = "weekly", Days = "Sunday", Time = "03:00" }));
        Assert.False(TaskPlanner.AuditOverdue(monday));
        Db.SetSetting(TaskPlanner.SinceKey(TaskPlanner.AuditTask), "2026-11-10T09:00:00.0000000");
        Assert.True(TaskPlanner.AuditOverdue(monday));
        Db.FinishRun(Db.StartRun("audit", 0, "Hızlı yedek denetimi"), "success", "", 0);
        Db.Exec("UPDATE runs SET started='2026-11-15 03:00:04' WHERE kind='audit'");
        Assert.False(TaskPlanner.AuditOverdue(monday));
    }

    [Fact]
    public void Own_lock_and_live_files_are_not_reported_as_missing_from_the_backup()
    {
        using var s = new Sandbox();
        var data = AppPaths.DataDir;
        Assert.True(Archiver.IsOwnTransient(Path.Combine(data, "locks", "job-1.lock") + " : Dosya başka bir işlem tarafından kullanıldığından bu işlem dosyaya erişemiyor."));
        Assert.True(Archiver.IsOwnTransient(Path.Combine(data, "live", "job-1.json") + " : Erişim engellendi."));
        Assert.False(Archiver.IsOwnTransient(Path.Combine(data, "backup360.db") + " : Dosya kullanılıyor."));
        Assert.False(Archiver.IsOwnTransient(@"D:\Proje\locks\benim.lock : Dosya kullanılıyor."));
    }

    [Fact]
    public async Task Backing_up_the_apps_own_folder_does_not_end_with_a_warning()
    {
        using var s = new Sandbox();
        // Kaynak, uygulamanın veri klasörünü de içeriyor; iş çalışırken kendi kilidi açık duruyor.
        File.WriteAllText(Path.Combine(s.Root, "not.txt"), "kullanıcı dosyası");
        var job = new Job { Name = "Kendisi", Mode = "archive", Sources = new() { AppPaths.DataDir }, Formats = new() { "zip" }, Targets = new() { "gdrive" } };
        Db.Save(job);
        var lines = new List<string>();
        var r = await BackupRunner.RunAsync(job.Id, l => { lock (lines) lines.Add(l); }, default);
        Assert.True(r.Status == "success", r.Detail + " | " + string.Join(" | ", lines.Where(l => l.Contains("uyarı"))));
        Assert.Equal((0, 0), Problems.OpenCounts());
    }

    [Fact]
    public void Problems_that_do_not_recur_are_closed_when_the_job_finishes_again()
    {
        using var s = new Sandbox();
        Problems.Capture("job", 1, "İş", @"  uyarı: yedeklenemedi: D:\a\gecici.zip : Sistem belirtilen dosyayı bulamıyor.");
        Problems.Capture("job", 1, "İş", @"  uyarı: yedeklenemedi: D:\a\hep-acik.sock : Sistem dosyaya erişemiyor.");
        Problems.Capture("job", 2, "Başka", "[X.zip] HATA: başka işin sorunu");
        Db.Exec("UPDATE problems SET time='2026-11-10 16:29:49'");

        // Sonraki çalışma: yalnızca biri yeniden görülüyor ve iş yine uyarıyla bitiyor.
        var started = new DateTime(2026, 11, 11, 16, 0, 2);
        Problems.Capture("job", 1, "İş", @"  uyarı: yedeklenemedi: D:\a\hep-acik.sock : Sistem dosyaya erişemiyor.", started);
        Problems.Capture("job", 1, "İş", "Bitti: warning", started);

        var open = Problems.List(0, true);
        Assert.Equal(new[] { "Başka", "İş" }, open.Select(p => p.Name).OrderBy(n => n));
        Assert.EndsWith("hep-acik.sock", open.Single(p => p.Name == "İş").Item);
        Assert.Equal(4, Problems.List(0, false).Count);
    }

    [Fact]
    public async Task Changes_inside_excluded_folders_do_not_trigger_a_new_archive()
    {
        using var s = new Sandbox();
        var project = Path.Combine(s.Root, "Kod", "Proje");
        Directory.CreateDirectory(Path.Combine(project, "node_modules", "paket"));
        Directory.CreateDirectory(Path.Combine(project, "src"));
        File.WriteAllText(Path.Combine(project, "src", "app.ts"), "kaynak");
        File.WriteAllText(Path.Combine(project, "node_modules", "paket", "index.js"), "v1");
        var job = new Job { Name = "Kod", Mode = "dated", UseDateFolder = false, DatedOutput = "archive", OnlyChanged = true, Sources = new() { Path.Combine(s.Root, "Kod") }, Formats = new() { "zip" }, Targets = new() { "gdrive" }, Excludes = new() { "node_modules" }, Keep = 0 };
        Db.Save(job);
        int Zips() => Directory.GetFiles(s.Cloud1, "*.zip", SearchOption.AllDirectories).Length;

        Assert.Equal("success", (await BackupRunner.RunAsync(job.Id, null, default)).Status);
        Assert.Equal(1, Zips());

        await Task.Delay(1100);
        File.WriteAllText(Path.Combine(project, "node_modules", "paket", "index.js"), "v2, daha uzun içerik");
        File.WriteAllText(Path.Combine(project, "node_modules", "yeni.js"), "yeni paket dosyası");
        var unchanged = await BackupRunner.RunAsync(job.Id, null, default);
        Assert.Equal(("success", "Değişiklik yok"), (unchanged.Status, unchanged.Detail));
        Assert.Equal(1, Zips());

        File.AppendAllText(Path.Combine(project, "src", "app.ts"), " değişti");
        Assert.Equal("success", (await BackupRunner.RunAsync(job.Id, null, default)).Status);
        Assert.Equal(2, Zips());
    }

    [Fact]
    public async Task Fingerprint_is_unchanged_for_jobs_without_excludes()
    {
        using var s = new Sandbox();
        var project = Path.Combine(s.Root, "Kod", "Proje");
        Directory.CreateDirectory(Path.Combine(project, "alt"));
        File.WriteAllText(Path.Combine(project, "alt", "a.txt"), "12345");
        File.WriteAllText(Path.Combine(project, "b.txt"), "123");
        var job = new Job { Name = "Iz", Mode = "dated", UseDateFolder = false, DatedOutput = "archive", OnlyChanged = true, Sources = new() { Path.Combine(s.Root, "Kod") }, Formats = new() { "zip" }, Targets = new() { "gdrive" } };
        Db.Save(job);
        Assert.Equal("success", (await BackupRunner.RunAsync(job.Id, null, default)).Status);
        var last = new[] { Path.Combine(project, "alt", "a.txt"), Path.Combine(project, "b.txt") }.Max(File.GetLastWriteTime);
        Assert.Equal($"2|8|{last.Ticks}", Db.GetFingerprint(job.Id, "Proje"));
    }
}
