using Backup360.Core;
using Xunit;

namespace Backup360.Tests;

public class SyncTests
{
    private static Job Sync(string name, string kind, IEnumerable<string> sources, params string[] targets) =>
        new() { Name = name, Mode = "sync", SyncKind = kind, Sources = sources.ToList(), Targets = targets.ToList(), RemoteFolder = "", MirrorDelete = true };

    [Fact]
    public async Task Folder_sync_copies_only_differences_and_shows_them_first()
    {
        using var s = new Sandbox();
        var src = Directory.CreateDirectory(Path.Combine(s.Root, "Belgeler")).FullName;
        File.WriteAllText(Path.Combine(src, "ayni.txt"), "ayni");
        File.WriteAllText(Path.Combine(src, "degisen.txt"), "ilk");
        File.WriteAllText(Path.Combine(src, "silinecek.txt"), "sil");
        var job = Sync("NAS Esitleme", "folder", new[] { src }, "nas");
        Db.Save(job);

        var before = await SyncPlanner.DiffAsync(SyncPlanner.Pairs(job).Single(), job.Excludes, default);
        Assert.Equal(3, before.Count(SyncPlanner.New));

        var lines = new List<string>();
        var r = await BackupRunner.RunAsync(job.Id, l => { lock (lines) lines.Add(l); }, default);
        Assert.True(r.Status == "success", r.Detail);
        var dest = Path.Combine(s.Cloud2, "Belgeler");
        Assert.Equal(Sandbox.Hashes(src), Sandbox.Hashes(dest));
        lock (lines) Assert.Contains(lines, l => l.Contains("Yeni") && l.Contains("degisen.txt"));

        var untouched = Path.Combine(dest, "ayni.txt");
        var stamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(untouched, File.GetLastWriteTimeUtc(Path.Combine(src, "ayni.txt")));
        File.WriteAllText(Path.Combine(src, "degisen.txt"), "ikinci surum uzun");
        File.Delete(Path.Combine(src, "silinecek.txt"));
        File.WriteAllText(Path.Combine(src, "yeni.txt"), "yeni");

        var diff = await SyncPlanner.DiffAsync(SyncPlanner.Pairs(job).Single(), job.Excludes, default);
        Assert.Equal(1, diff.Count(SyncPlanner.New));
        Assert.Equal(1, diff.Count(SyncPlanner.Changed));
        Assert.Equal(1, diff.Count(SyncPlanner.Extra));
        Assert.Equal(1, diff.Count(SyncPlanner.Same));
        Assert.True(stamp < DateTime.UtcNow);

        lines.Clear();
        await Task.Delay(1100);
        Assert.Equal("success", (await BackupRunner.RunAsync(job.Id, l => { lock (lines) lines.Add(l); }, default)).Status);
        Assert.Equal(Sandbox.Hashes(src), Sandbox.Hashes(dest));
        Assert.Single(Directory.GetFiles(Path.Combine(s.Cloud2, "_silinenler"), "silinecek.txt", SearchOption.AllDirectories));
        lock (lines)
        {
            Assert.DoesNotContain(lines, l => l.Contains("ayni.txt"));
            Assert.Contains(lines, l => l.Contains("yeni.txt"));
        }
    }

    [Fact]
    public async Task Without_delete_option_extra_files_stay()
    {
        using var s = new Sandbox();
        var src = Directory.CreateDirectory(Path.Combine(s.Root, "A")).FullName;
        File.WriteAllText(Path.Combine(src, "a.txt"), "a");
        var dest = Directory.CreateDirectory(Path.Combine(s.Cloud2, "A")).FullName;
        File.WriteAllText(Path.Combine(dest, "hedefte.txt"), "kalsin");
        var job = Sync("Kopya", "folder", new[] { src }, "nas");
        job.MirrorDelete = false;
        Db.Save(job);
        Assert.Equal("success", (await BackupRunner.RunAsync(job.Id, null, default)).Status);
        Assert.True(File.Exists(Path.Combine(dest, "hedefte.txt")));
        Assert.True(File.Exists(Path.Combine(dest, "a.txt")));
    }

    [Fact]
    public async Task Sql_sync_copies_backups_of_a_job_to_another_target()
    {
        using var s = new Sandbox();
        var backup = new Job { Name = "ERP Yedek", Mode = "database", DbEngine = "sqlite", DbNames = new() { "x" }, Formats = new() { "7z" }, Targets = new() { "gdrive" } };
        Db.Save(backup);
        var folder = Directory.CreateDirectory(Path.Combine(s.Cloud1, "Backup360", "ERP Yedek", "2026-11-09")).FullName;
        File.WriteAllText(Path.Combine(folder, "ERP_FULL_20261109-020000.7z"), "yedek");

        var job = new Job { Name = "SQL NAS", Mode = "sync", SyncKind = "sql", SyncFromJob = backup.Id, Targets = new() { "nas" }, RemoteFolder = "Arsiv" };
        Db.Save(job);
        var r = await BackupRunner.RunAsync(job.Id, null, default);
        Assert.True(r.Status == "success", r.Detail);
        Assert.Equal("yedek", File.ReadAllText(Path.Combine(s.Cloud2, "Arsiv", "ERP Yedek", "2026-11-09", "ERP_FULL_20261109-020000.7z")));
        Assert.True(File.Exists(Path.Combine(folder, "ERP_FULL_20261109-020000.7z")));
    }

    [Fact]
    public void Sync_validation()
    {
        using var s = new Sandbox();
        Assert.Throws<ArgumentException>(() => Validation.Check(new Job { Name = "x", Mode = "sync", Targets = new() { "nas" } }));
        Assert.Throws<ArgumentException>(() => Validation.Check(new Job { Name = "x", Mode = "sync", Sources = new() { "C:\\a" } }));
        var backup = new Job { Name = "Ayni", Mode = "database", DbEngine = "sqlite", DbNames = new() { "x" }, Targets = new() { "nas" }, RemoteFolder = "Backup360" };
        Db.Save(backup);
        var job = new Job { Name = "x", Mode = "sync", SyncFromJob = backup.Id, Targets = new() { "nas" }, RemoteFolder = "Backup360" };
        Assert.Throws<ArgumentException>(() => SyncPlanner.Pairs(job));
    }
}
