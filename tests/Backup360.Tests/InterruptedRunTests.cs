using Backup360.Core;
using Xunit;

namespace Backup360.Tests;

public class InterruptedRunTests
{
    [Fact]
    public async Task Run_left_as_running_is_closed_when_the_job_starts_again()
    {
        using var s = new Sandbox();
        var job = new Job { Name = "Kesik", Mode = "archive", Sources = new() { s.Tree("K") }, Formats = new() { "zip" }, Targets = new() { "gdrive" } };
        Db.Save(job);
        Db.StartRun("job", job.Id, job.Name);
        Assert.Equal("running", Db.LastRun("job", job.Id)!.Status);

        Assert.Equal("success", (await BackupRunner.RunAsync(job.Id, null, default)).Status);
        var runs = Db.RecentRuns().Where(r => r.RefId == job.Id).OrderBy(r => r.Id).ToList();
        Assert.Equal(new[] { "failed", "success" }, runs.Select(r => r.Status));
        Assert.Contains("yarıda kesildi", runs[0].Detail);
        Assert.NotNull(runs[0].Ended);
    }

    [Fact]
    public void Startup_closes_only_old_runs_without_a_live_process()
    {
        using var s = new Sandbox();
        var old = "2026-01-01 10:00:00";
        Db.Exec("INSERT INTO runs(kind,ref_id,name,started,status) VALUES('job',1,'olu',$s,'running'),('job',2,'canli',$s,'running'),('restore',3,'geri',$s,'running')", ("$s", old));
        var fresh = Db.StartRun("job", 4, "yeni basladi");

        Assert.Equal(1, Db.CloseOrphanRuns(new HashSet<string> { "job-2" }));
        Assert.Equal("failed", Db.LastRun("job", 1)!.Status);
        Assert.Equal("running", Db.LastRun("job", 2)!.Status);
        Assert.Equal("running", Db.LastRun("restore", 3)!.Status);
        Assert.Equal("running", Db.RecentRuns().Single(r => r.Id == fresh).Status);
    }

    [Fact]
    public void Leftovers_of_interrupted_runs_are_swept_but_active_work_is_kept()
    {
        using var s = new Sandbox();
        var root = Directory.CreateDirectory(Path.Combine(s.Root, "_calisma")).FullName;
        var stale = Directory.CreateDirectory(Path.Combine(root, "eski")).FullName;
        File.WriteAllText(Path.Combine(stale, "yarim.7z"), "yarim");
        File.SetLastWriteTimeUtc(Path.Combine(stale, "yarim.7z"), DateTime.UtcNow.AddHours(-10));
        Directory.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-10));

        var active = Directory.CreateDirectory(Path.Combine(root, "suren")).FullName;
        File.WriteAllText(Path.Combine(active, "yaziliyor.7z"), "suruyor");
        Directory.SetLastWriteTimeUtc(active, DateTime.UtcNow.AddHours(-10));

        Assert.Equal(1, WorkFolder.SweepStale(root, TimeSpan.FromHours(6)));
        Assert.False(Directory.Exists(stale));
        Assert.True(File.Exists(Path.Combine(active, "yaziliyor.7z")));
        Assert.Equal(0, WorkFolder.SweepStale(Path.Combine(s.Root, "olmayan"), TimeSpan.FromHours(6)));
    }

    [Fact]
    public async Task Archive_reaches_the_local_store_only_after_it_is_verified()
    {
        using var s = new Sandbox();
        var temp = Path.Combine(s.Root, "Temp");
        var job = new Job { Name = "Depo", Mode = "archive", Sources = new() { s.Tree("D") }, Formats = new() { "7z" }, Targets = new() { "yok-hedef" } };
        Db.Save(job);
        TargetStore.Save(new TargetDefinition { Name = "yok-hedef", Type = "alias", Auth = "none", Path = Path.Combine(s.Root, "bulut3") });
        WorkFolder.Save(temp, 1);
        try
        {
            var lines = new List<string>();
            var r = await BackupRunner.RunAsync(job.Id, l => { lock (lines) lines.Add(l); }, default);
            Assert.True(r.Status == "success", r.Detail);
            var stored = Directory.GetFiles(Path.Combine(temp, "Depo"), "*", SearchOption.AllDirectories);
            Assert.Single(stored);
            Assert.EndsWith(".7z", stored[0]);
            Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(temp, "_calisma")));
            Assert.Equal(BackupAudit.Md5Of(stored[0]), BackupAudit.Md5Of(Directory.GetFiles(Path.Combine(s.Root, "bulut3"), "*.7z", SearchOption.AllDirectories).Single()));
        }
        finally { WorkFolder.Save("", 1); }
    }
}
