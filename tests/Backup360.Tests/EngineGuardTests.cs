using System.Security.Cryptography;
using System.Text;
using Backup360.Core;
using Xunit;

namespace Backup360.Tests;

public class PostgresFirebirdTests
{
    private static string Env(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        Assert.False(string.IsNullOrEmpty(v), name + " ayarlanmadı");
        return v!;
    }

    private static Job Pg() => new()
    {
        Name = "Pg", Mode = "database", DbEngine = "postgres", DbServer = "127.0.0.1", DbPort = int.Parse(Env("BACKUP360_PG_TEST_PORT")), DbUser = "postgres",
        DbPasswordEnc = Secrets.Protect("PgGizli1!"), Formats = new() { "7z" }, Targets = new() { "gdrive" }, DbAll = true, DbRestoreTest = true,
    };

    private static Job Fb(string db) => new()
    {
        Name = "Fb", Mode = "database", DbEngine = "firebird", DbServer = "localhost", DbPort = int.Parse(Env("BACKUP360_FB_TEST_PORT")), DbUser = "SYSDBA",
        DbPasswordEnc = Secrets.Protect("FbGizli1"), DbNames = new() { db }, DbAll = false, Formats = new() { "7z" }, Targets = new() { "gdrive" }, DbRestoreTest = true,
    };

    [Fact]
    public async Task Postgres_backup_restore_test_and_cleanup()
    {
        Env("BACKUP360_PG_BIN");
        using var s = new Sandbox();
        var job = Pg();
        Assert.Contains("b360test", await PostgresEngine.ListAsync(job, default));
        Db.Save(job);
        var lines = new List<string>();
        var r = await BackupRunner.RunAsync(job.Id, l => { lock (lines) lines.Add(l); }, default);
        Assert.True(r.Status == "success", r.Detail + "\n" + string.Join("\n", lines));
        Assert.Single(Directory.GetFiles(s.Cloud1, "b360test_*.7z", SearchOption.AllDirectories));
        lock (lines) Assert.Contains(lines, l => l.Contains("geri yükleme") && l.Contains("temiz"));
        var all = await PostgresEngine.ListAsync(job, default);
        Assert.DoesNotContain(all, n => n.StartsWith("backup360_verify_"));
    }

    [Fact]
    public async Task Postgres_damaged_dump_and_wrong_password()
    {
        Env("BACKUP360_PG_BIN");
        using var s = new Sandbox();
        var job = Pg();
        var path = Path.Combine(s.Root, "x.dump");
        await PostgresEngine.DumpAsync(job, "b360test", path, default);
        await PostgresEngine.VerifyAsync(job, path, default);
        File.WriteAllBytes(path, RandomNumberGenerator.GetBytes(4096));
        await Assert.ThrowsAsync<BackupCorruptException>(() => PostgresEngine.VerifyAsync(job, path, default));

        job.DbPasswordEnc = Secrets.Protect("yanlis");
        await Assert.ThrowsAsync<IOException>(() => PostgresEngine.ListAsync(job, default));
    }

    [Fact]
    public async Task Firebird_backup_with_restore_test_and_corrupt_database()
    {
        Env("BACKUP360_FB_BIN");
        using var s = new Sandbox();
        var job = Fb(Env("BACKUP360_FB_TEST_DB"));
        Db.Save(job);
        var lines = new List<string>();
        var r = await BackupRunner.RunAsync(job.Id, l => { lock (lines) lines.Add(l); }, default);
        Assert.True(r.Status == "success", r.Detail + "\n" + string.Join("\n", lines));
        Assert.Single(Directory.GetFiles(s.Cloud1, "test_*.7z", SearchOption.AllDirectories));

        var bad = Path.Combine(s.Root, "bozuk.fdb");
        var bytes = RandomNumberGenerator.GetBytes(200_000);
        File.WriteAllBytes(bad, bytes);
        var broken = Fb(bad);
        broken.Name = "FbBozuk";
        Db.Save(broken);
        var result = await BackupRunner.RunAsync(broken.Id, null, default);
        Assert.True(result.Status == "corrupt", result.Status + ": " + result.Detail);
    }

    [Fact]
    public void New_engines_are_validated()
    {
        Assert.Throws<ArgumentException>(() => Validation.Check(new Job { Name = "x", Mode = "database", DbEngine = "postgres", DbServer = "h", Targets = new() { "a" }, Formats = new() { "7z" } }));
        Assert.Throws<ArgumentException>(() => Validation.Check(new Job { Name = "x", Mode = "database", DbEngine = "firebird", DbServer = "h", DbUser = "u", DbAll = false, Targets = new() { "a" }, Formats = new() { "7z" } }));
    }
}

public class RansomGuardTests
{
    private static string Folder(Sandbox s, string name) => Directory.CreateDirectory(Path.Combine(s.Root, name)).FullName;

    private static void Random(string dir, string ext, int count)
    {
        for (var i = 0; i < count; i++) File.WriteAllBytes(Path.Combine(dir, $"f{i}{ext}"), RandomNumberGenerator.GetBytes(70_000));
    }

    private static void Healthy(string dir, int count)
    {
        for (var i = 0; i < count; i++)
        {
            File.WriteAllText(Path.Combine(dir, $"n{i}.txt"), string.Concat(Enumerable.Repeat("Normal metin icerigi\n", 2000)));
            File.WriteAllBytes(Path.Combine(dir, $"p{i}.pdf"), Encoding.ASCII.GetBytes("%PDF-1.7\n").Concat(RandomNumberGenerator.GetBytes(70_000)).ToArray());
        }
    }

    [Fact]
    public void Normal_files_and_compressed_formats_do_not_alarm()
    {
        using var s = new Sandbox();
        var dir = Folder(s, "A");
        Healthy(dir, 10);
        var r = RansomGuard.Scan(new[] { dir }, DateTime.Now.AddDays(-1), default);
        Assert.False(r.Alarm, r.Summary);
    }

    [Fact]
    public void Encrypted_office_files_alarm()
    {
        using var s = new Sandbox();
        var dir = Folder(s, "B");
        Random(dir, ".docx", 8);
        Healthy(dir, 2);
        var r = RansomGuard.Scan(new[] { dir }, DateTime.Now.AddDays(-1), default);
        Assert.True(r.Alarm, r.Summary);
    }

    [Fact]
    public void Ransom_note_or_many_bad_extensions_alarm_but_one_does_not()
    {
        using var s = new Sandbox();
        var a = Folder(s, "C");
        File.WriteAllText(Path.Combine(a, "HOW_TO_DECRYPT.txt"), "bitcoin");
        Assert.True(RansomGuard.Scan(new[] { a }, DateTime.Now.AddDays(-1), default).Alarm);

        var b = Folder(s, "D");
        File.WriteAllText(Path.Combine(b, "tek.locked"), "x");
        Assert.False(RansomGuard.Scan(new[] { b }, DateTime.Now.AddDays(-1), default).Alarm);

        for (var i = 0; i < 6; i++) File.WriteAllText(Path.Combine(b, $"a{i}.lockbit"), "x");
        Assert.True(RansomGuard.Scan(new[] { b }, DateTime.Now.AddDays(-1), default).Alarm);
    }

    [Fact]
    public async Task Sync_stops_and_good_copies_stay_when_files_are_encrypted()
    {
        using var s = new Sandbox();
        var src = Folder(s, "Belgeler");
        File.WriteAllText(Path.Combine(src, "ana.txt"), "saglam");
        var job = new Job { Name = "Es", Mode = "sync", Sources = new() { src }, Targets = new() { "nas" }, RemoteFolder = "", MirrorDelete = true };
        Db.Save(job);
        Assert.Equal("success", (await BackupRunner.RunAsync(job.Id, null, default)).Status);

        File.Delete(Path.Combine(src, "ana.txt"));
        Random(src, ".xlsx", 8);
        var r = await BackupRunner.RunAsync(job.Id, null, default);
        Assert.Equal("ransomware", r.Status);
        Assert.Equal("saglam", File.ReadAllText(Path.Combine(s.Cloud2, "Belgeler", "ana.txt")));
        Assert.Empty(Directory.GetFiles(s.Cloud2, "*.xlsx", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Alarm_sends_mail_once_for_same_event()
    {
        using var s = new Sandbox();
        using var smtp = new ServiceTests.FakeSmtp();
        Notifier.Save(new SmtpSettings { Enabled = true, Host = "127.0.0.1", Port = smtp.Port, Security = "none", From = "y@o.com", To = "bt@o.com" });
        var src = Folder(s, "Kaynak");
        Db.Save(new Job { Name = "J", Mode = "mirror", Sources = new() { src }, Targets = new() { "nas" } });
        Random(src, ".docx", 8);

        Assert.True((await RansomGuard.MonitorAsync(default)).Alarm);
        lock (smtp.Lines) Assert.Contains(smtp.Lines, l => l.StartsWith("Subject:") && l.Contains("=?") || l.Contains("FIDYE"));
        Assert.False((await RansomGuard.MonitorAsync(default)).Alarm);
    }

    [Fact]
    public void Partial_encryption_in_text_files_is_found()
    {
        using var s = new Sandbox();
        var dir = Folder(s, "K");
        for (var i = 0; i < 8; i++)
        {
            var clean = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("satir satir normal icerik\n", 30000)));
            RandomNumberGenerator.Fill(clean.AsSpan(clean.Length / 2, 200_000));
            File.WriteAllBytes(Path.Combine(dir, $"k{i}.sql"), clean);
        }
        var r = RansomGuard.Scan(new[] { dir }, DateTime.Now.AddDays(-1), default);
        Assert.True(r.Alarm, r.Summary);
    }

    [Fact]
    public async Task Mass_change_on_target_stops_sync()
    {
        using var s = new Sandbox();
        var src = Folder(s, "Veri");
        for (var i = 0; i < 30; i++) File.WriteAllText(Path.Combine(src, $"d{i}.dat"), "ilk icerik");
        var job = new Job { Name = "Toplu", Mode = "sync", Sources = new() { src }, Targets = new() { "nas" }, RemoteFolder = "", MirrorDelete = true };
        Db.Save(job);
        Assert.Equal("success", (await BackupRunner.RunAsync(job.Id, null, default)).Status);

        for (var i = 0; i < 20; i++) File.WriteAllText(Path.Combine(src, $"d{i}.dat"), "degisti ve boyu da farkli");
        var r = await BackupRunner.RunAsync(job.Id, null, default);
        Assert.Equal("ransomware", r.Status);
        Assert.Equal("ilk icerik", File.ReadAllText(Path.Combine(s.Cloud2, "Veri", "d0.dat")));

        File.WriteAllText(Path.Combine(src, "d0.dat"), "ilk icerik");
        for (var i = 1; i < 20; i++) File.WriteAllText(Path.Combine(src, $"d{i}.dat"), "ilk icerik");
        File.WriteAllText(Path.Combine(src, "d25.dat"), "tek degisiklik yeni boy");
        Assert.Equal("success", (await BackupRunner.RunAsync(job.Id, null, default)).Status);
        Assert.Equal("tek degisiklik yeni boy", File.ReadAllText(Path.Combine(s.Cloud2, "Veri", "d25.dat")));
    }

    [Fact]
    public void Guard_can_be_disabled()
    {
        using var s = new Sandbox();
        Assert.True(RansomGuard.Enabled);
        Db.SetSetting("guard_enabled", "0");
        Assert.False(RansomGuard.Enabled);
    }
}
