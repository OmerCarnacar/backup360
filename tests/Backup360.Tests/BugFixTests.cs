using Backup360.Core;
using Xunit;

namespace Backup360.Tests;

/// <summary>Gerçek kullanımın kayıtlarında bulunan kusurlar.</summary>
public class BugFixTests
{
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
