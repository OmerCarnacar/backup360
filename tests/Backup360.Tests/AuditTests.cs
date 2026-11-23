using Backup360.Core;
using Xunit;

namespace Backup360.Tests;

public class AuditTests
{
    private static async Task<(Job Job, string File)> Backup(Sandbox s, string name = "Denetim")
    {
        var job = new Job { Name = name, Mode = "archive", Sources = new() { s.Tree("K" + name) }, Formats = new() { "7z" }, Targets = new() { "gdrive" } };
        Db.Save(job);
        Assert.Equal("success", (await BackupRunner.RunAsync(job.Id, null, default)).Status);
        return (job, Directory.GetFiles(s.Cloud1, "*.7z", SearchOption.AllDirectories).Single());
    }

    [Fact]
    public async Task Healthy_backups_pass_quick_and_deep()
    {
        using var s = new Sandbox();
        await Backup(s);
        Assert.Single(Db.BackupFiles());
        var quick = await BackupAudit.RunAsync(false, null, default);
        Assert.True(quick.Status == "success", quick.Detail);
        Assert.Contains("1 sağlam", quick.Detail);
        var deep = await BackupAudit.RunAsync(true, null, default);
        Assert.True(deep.Status == "success", deep.Detail);
        Assert.Equal(BackupAudit.Ok, Db.BackupFiles().Single().Status);
    }

    [Fact]
    public async Task Same_size_tampering_is_detected_by_fingerprint()
    {
        using var s = new Sandbox();
        var (_, file) = await Backup(s);
        var bytes = File.ReadAllBytes(file);
        bytes[bytes.Length / 2] ^= 0xFF;
        File.WriteAllBytes(file, bytes);
        var r = await BackupAudit.RunAsync(false, null, default);
        Assert.Equal("corrupt", r.Status);
        Assert.Equal(BackupAudit.Changed, Db.BackupFiles().Single().Status);
    }

    [Fact]
    public async Task Deleted_backup_is_reported_missing_and_can_be_forgotten()
    {
        using var s = new Sandbox();
        var (_, file) = await Backup(s);
        File.Delete(file);
        // Eksik yedek uyarıdır; "yedek bozuk" alarmı yalnızca bozulmuş dosya içindir.
        Assert.Equal("warning", (await BackupAudit.RunAsync(false, null, default)).Status);
        Assert.Equal(BackupAudit.Missing, Db.BackupFiles().Single().Status);
        Db.ForgetMissingBackups();
        Assert.Empty(Db.BackupFiles());
    }

    [Fact]
    public void Old_missing_backup_with_a_newer_copy_is_retired_but_never_a_base_or_a_recent_one()
    {
        var job = new Job { Id = 7, KeepDays = 3 };
        var now = new DateTime(2026, 11, 23, 8, 0, 0);
        BackupFile F(string path) => new("T", "B", path, 7, "İş", 1, "", "", null, "ok", "");

        // 10 Kasım'ın yedeği yok, 18 Kasım'ın yeni tam yedeği duruyor: beklenen yokluk.
        Assert.True(BackupAudit.IsRetired(F("10-11-2026_11_27/A_20261110-112715.zip"), job, new[] { "18-11-2026_16_00/A_20261118-160000.zip" }, now));
        // Daha yeni yedeği yok: gerçek kayıp.
        Assert.False(BackupAudit.IsRetired(F("10-11-2026_11_27/A_20261110-112715.zip"), job, new[] { "18-11-2026_16_00/B_20261118-160000.zip" }, now));
        // Saklama süresi içinde (dün): gerçek kayıp.
        Assert.False(BackupAudit.IsRetired(F("22-11-2026_16_00/A_20261122-160000.zip"), job, new[] { "23-11-2026_07_59/A_20261123-075900.zip" }, now));
        // Hedefte duran bir fark bu tam yedeğe dayanıyor: gerçek kayıp.
        Assert.False(BackupAudit.IsRetired(F("10-11-2026_11_27/A_20261110-112715.zip"), job,
            new[] { "18-11-2026_16_00/A_20261118-160000.zip", "19-11-2026_16_00/A_FARK-20261110-112715_20261119-160000.zip" }, now));
        // Daha yeni olan yalnızca tam yedeği kayıp bir fark: kullanılamaz, sayılmaz.
        Assert.False(BackupAudit.IsRetired(F("10-11-2026_11_27/A_20261110-112715.zip"), job, new[] { "19-11-2026_16_00/A_FARK-20261115-160000_20261119-160000.zip" }, now));
        // Saklama süresi tanımlı değilse hiçbir eksik kendiliğinden kaldırılmaz.
        Assert.False(BackupAudit.IsRetired(F("10-11-2026_11_27/A_20261110-112715.zip"), new Job { Id = 7 }, new[] { "18-11-2026_16_00/A_20261118-160000.zip" }, now));
    }

    [Fact]
    public async Task Retention_removes_records_so_it_is_not_reported_missing()
    {
        using var s = new Sandbox();
        var (job, _) = await Backup(s, "Sakla");
        job.Keep = 1;
        Db.Save(job);
        await Task.Delay(1100);
        Assert.Equal("success", (await BackupRunner.RunAsync(job.Id, null, default)).Status);
        Assert.Single(Db.BackupFiles());
        Assert.Equal("success", (await BackupAudit.RunAsync(false, null, default)).Status);
    }

    [Fact]
    public async Task Unreachable_target_is_not_treated_as_lost()
    {
        using var s = new Sandbox();
        await Backup(s);
        TargetStore.Delete("gdrive");
        TargetStore.Delete("nas");
        var r = await BackupAudit.RunAsync(false, null, default);
        Assert.Equal("failed", r.Status);
        Assert.NotEqual(BackupAudit.Missing, Db.BackupFiles().Single().Status);
    }

    [Fact]
    public async Task Older_backups_are_adopted_and_deep_check_finds_damage()
    {
        using var s = new Sandbox();
        var job = new Job { Name = "Eski", Mode = "archive", Sources = new() { @"C:\yok" }, Formats = new() { "7z" }, Targets = new() { "gdrive" } };
        Db.Save(job);
        var folder = Directory.CreateDirectory(Path.Combine(s.Cloud1, "Backup360", "Eski")).FullName;
        File.WriteAllText(Path.Combine(folder, "Eski_20200101-000000.7z"), "bu gercek bir arsiv degil");
        Assert.Equal("success", (await BackupAudit.RunAsync(false, null, default)).Status);
        Assert.Single(Db.BackupFiles());
        var deep = await BackupAudit.RunAsync(true, null, default);
        Assert.Equal("corrupt", deep.Status);
        Assert.Equal(BackupAudit.Changed, Db.BackupFiles().Single().Status);
    }
}
