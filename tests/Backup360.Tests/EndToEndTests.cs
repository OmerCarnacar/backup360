using System.Diagnostics;
using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text.Json;
using Backup360.Core;
using Xunit;
using ZstdSharp;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Backup360.Tests;

public sealed class Sandbox : IDisposable
{
    public string Root { get; } = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "b360test-" + Guid.NewGuid().ToString("N")[..8])).FullName;
    public string Cloud1 => Path.Combine(Root, "bulut1");
    public string Cloud2 => Path.Combine(Root, "bulut2");

    public Sandbox()
    {
        Environment.SetEnvironmentVariable("BACKUP360_DATA", Path.Combine(Root, "data"));
        Directory.CreateDirectory(Cloud1);
        Directory.CreateDirectory(Cloud2);
        // Gerçek Drive yerine aynı rclone komutlarını kullanan klasör hedefleri
        File.WriteAllText(AppPaths.RcloneConfig, $"[gdrive]\ntype = alias\nremote = {Cloud1}\n\n[nas]\ntype = alias\nremote = {Cloud2}\n");
        Db.Init();
    }

    public string Tree(string name)
    {
        var d = Path.Combine(Root, name);
        Directory.CreateDirectory(Path.Combine(d, "Alt Klasör ğüşıöç"));
        File.WriteAllText(Path.Combine(d, "rapor şüğ.txt"), string.Concat(Enumerable.Repeat("Backup360 sıkıştırma testi\n", 4000)));
        File.WriteAllBytes(Path.Combine(d, "Alt Klasör ğüşıöç", "veri.bin"), RandomNumberGenerator.GetBytes(50_000));
        return d;
    }

    public static Dictionary<string, string> Hashes(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToDictionary(
            f => Path.GetRelativePath(dir, f).ToLowerInvariant(),
            f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("BACKUP360_DATA", null);
        try { Directory.Delete(Root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public class ArchiveTests
{
    private static async Task<string> Extract(string file, string dest, string? password = null)
    {
        Directory.CreateDirectory(dest);
        if (file.EndsWith(".tar.zst"))
        {
            using var f = File.OpenRead(file);
            using var z = new DecompressionStream(f);
            await TarFile.ExtractToDirectoryAsync(z, dest, true);
            return dest;
        }
        var sz = Tools.SevenZip()!;
        var args = new List<string> { "x", "-y", "-o" + dest };
        if (password != null) args.Add("-p" + password);
        args.Add(file);
        var (code, text) = await Proc.RunAsync(sz, args, null, default);
        Assert.True(code == 0, text);
        foreach (var inner in Directory.GetFiles(dest, "*.tar"))
        {
            var (c2, t2) = await Proc.RunAsync(sz, new[] { "x", "-y", "-o" + dest, inner }, null, default);
            Assert.True(c2 == 0, t2);
            File.Delete(inner);
        }
        return dest;
    }

    public static IEnumerable<object[]> AllFormats() => Formats.All.Select(f => new object[] { f.Key });

    [Theory, MemberData(nameof(AllFormats))]
    public async Task Every_format_roundtrips_at_highest_level(string key)
    {
        using var s = new Sandbox();
        var src = s.Tree("Kaynak");
        var format = Formats.Get(key);
        Assert.True(Formats.IsAvailable(format), key + " kullanılamıyor");
        var percents = new List<int>();
        var result = await Archiver.CreateAsync(format, new ArchiveRequest(new[] { src }, Array.Empty<string>(), Path.Combine(s.Root, "cikti"), 9, null), null, default, p => percents.Add(p));
        Assert.Equal(Path.Combine(s.Root, "cikti." + key), result.Path);
        Assert.False(result.HadWarnings);
        await Archiver.VerifyAsync(format, result.Path, null, default);
        var dest = await Extract(result.Path, Path.Combine(s.Root, "acik"));
        Assert.Equal(Sandbox.Hashes(src), Sandbox.Hashes(Path.Combine(dest, "Kaynak")));
    }

    [Theory, InlineData("7z"), InlineData("zip"), InlineData("rar")]
    public async Task Password_is_required_to_read(string key)
    {
        using var s = new Sandbox();
        var src = s.Tree("Kaynak");
        var format = Formats.Get(key);
        var result = await Archiver.CreateAsync(format, new ArchiveRequest(new[] { src }, Array.Empty<string>(), Path.Combine(s.Root, "x"), 5, "Gizli Parola!1"), null, default);
        await Archiver.VerifyAsync(format, result.Path, "Gizli Parola!1", default);
        await Assert.ThrowsAsync<IOException>(() => Archiver.VerifyAsync(format, result.Path, "yanlis", default));
    }

    [Fact]
    public async Task Excludes_and_multiple_sources()
    {
        using var s = new Sandbox();
        var a = s.Tree("A");
        var b = s.Tree("B");
        File.WriteAllText(Path.Combine(a, "gereksiz.tmp"), "x");
        var format = Formats.Get("tar.gz");
        var r = await Archiver.CreateAsync(format, new ArchiveRequest(new[] { a, b }, new[] { "*.tmp" }, Path.Combine(s.Root, "c"), 9, null), null, default);
        var d = await Extract(r.Path, Path.Combine(s.Root, "acik"));
        Assert.True(Directory.Exists(Path.Combine(d, "A")) && Directory.Exists(Path.Combine(d, "B")));
        Assert.False(File.Exists(Path.Combine(d, "A", "gereksiz.tmp")));
    }

    [Fact]
    public async Task Corrupt_archive_is_detected()
    {
        using var s = new Sandbox();
        foreach (var key in new[] { "7z", "tar.zst", "tar.gz" })
        {
            var r = await Archiver.CreateAsync(Formats.Get(key), new ArchiveRequest(new[] { s.Tree("K" + key) }, Array.Empty<string>(), Path.Combine(s.Root, "b" + key), 5, null), null, default);
            var bytes = File.ReadAllBytes(r.Path);
            for (var i = bytes.Length / 2; i < bytes.Length / 2 + 64; i++) bytes[i] ^= 0xFF;
            File.WriteAllBytes(r.Path, bytes);
            await Assert.ThrowsAnyAsync<Exception>(() => Archiver.VerifyAsync(Formats.Get(key), r.Path, null, default));
        }
    }

    [Fact]
    public async Task Cancel_stops_and_removes_partial_file()
    {
        using var s = new Sandbox();
        var src = Directory.CreateDirectory(Path.Combine(s.Root, "buyuk")).FullName;
        File.WriteAllBytes(Path.Combine(src, "b.bin"), RandomNumberGenerator.GetBytes(120_000_000));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Archiver.CreateAsync(Formats.Get("tar.xz"), new ArchiveRequest(new[] { src }, Array.Empty<string>(), Path.Combine(s.Root, "c"), 9, null), null, cts.Token));
        Assert.False(File.Exists(Path.Combine(s.Root, "c.tar.xz")));
    }
}

public class BackupTests
{
    private static Job Job(string name, string mode, params string[] sources) =>
        new() { Name = name, Mode = mode, Sources = sources.ToList(), Formats = new() { "7z" }, Targets = new() { "gdrive" } };

    private static string[] Files(string dir) =>
        Directory.Exists(dir) ? Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/')).OrderBy(x => x).ToArray() : Array.Empty<string>();

    [Fact]
    public async Task Archive_mode_uploads_all_formats_to_all_targets_and_cleans_temp()
    {
        using var s = new Sandbox();
        var job = Job("Günlük Yedek", "archive", s.Tree("Kaynak"));
        job.Formats = new() { "7z", "zip", "tar.zst" };
        job.Targets = new() { "gdrive", "nas" };
        Db.Save(job);
        var r = await BackupRunner.RunAsync(job.Id, null, default);
        Assert.True(r.Status == "success", r.Detail);
        foreach (var cloud in new[] { s.Cloud1, s.Cloud2 })
            Assert.Equal(new[] { "7z", "tar.zst", "zip" }, Files(cloud).Select(f => f[(f.IndexOf('.') + 1)..]).OrderBy(x => x).ToArray());
        Assert.StartsWith("Backup360/Günlük Yedek/Günlük Yedek_", Files(s.Cloud1)[0]);
        Assert.Equal("success", Db.LastRun("job", job.Id)!.Status);
        Assert.Empty(LiveFiles.ReadAll());
    }

    [Fact]
    public async Task Retention_keeps_newest_only_and_leaves_foreign_files()
    {
        using var s = new Sandbox();
        var job = Job("R", "archive", s.Tree("Kaynak"));
        job.Keep = 2;
        Db.Save(job);
        var folder = Directory.CreateDirectory(Path.Combine(s.Cloud1, "Backup360", "R")).FullName;
        File.WriteAllText(Path.Combine(folder, "R_20200101-000000.7z"), "eski1");
        File.WriteAllText(Path.Combine(folder, "R_20200102-000000.7z"), "eski2");
        File.WriteAllText(Path.Combine(folder, "benim-dosyam.7z"), "dokunma");
        var r = await BackupRunner.RunAsync(job.Id, null, default);
        Assert.Equal("success", r.Status);
        var names = Files(folder);
        Assert.Contains("benim-dosyam.7z", names);
        Assert.Contains("R_20200102-000000.7z", names);
        Assert.DoesNotContain("R_20200101-000000.7z", names);
        Assert.Equal(3, names.Length);
    }

    [Fact]
    public async Task Dated_mode_sends_each_subfolder_and_only_changes_next_time()
    {
        using var s = new Sandbox();
        var root = Directory.CreateDirectory(Path.Combine(s.Root, "Ana")).FullName;
        foreach (var n in new[] { "Muhasebe", "Satis" })
        {
            Directory.CreateDirectory(Path.Combine(root, n));
            File.WriteAllText(Path.Combine(root, n, "a.txt"), n);
        }
        Directory.CreateDirectory(Path.Combine(root, "Bos"));
        File.WriteAllText(Path.Combine(root, "kokteki.txt"), "kok");
        var job = Job("Tarihli", "dated", root);
        Db.Save(job);

        Assert.Equal("success", (await BackupRunner.RunAsync(job.Id, null, default)).Status);
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        var first = Files(s.Cloud1);
        Assert.Equal(3, first.Length);
        Assert.All(first, f => Assert.StartsWith($"Backup360/Tarihli/{today}/", f));
        Assert.Contains(first, f => f.Contains("/_KokDosyalar_"));

        await Task.Delay(1100);
        var again = await BackupRunner.RunAsync(job.Id, null, default);
        Assert.Equal("Değişiklik yok", again.Detail);
        Assert.Equal(3, Files(s.Cloud1).Length);

        File.AppendAllText(Path.Combine(root, "Satis", "a.txt"), "degisti");
        Assert.Equal("success", (await BackupRunner.RunAsync(job.Id, null, default)).Status);
        var third = Files(s.Cloud1);
        Assert.Equal(4, third.Length);
        Assert.Equal(2, third.Count(f => f.Contains("/Satis_")));
    }

    [Fact]
    public async Task Dated_mode_by_folder_date_uses_last_change_day()
    {
        using var s = new Sandbox();
        var root = Directory.CreateDirectory(Path.Combine(s.Root, "Ana")).FullName;
        var sub = Directory.CreateDirectory(Path.Combine(root, "Eski")).FullName;
        var file = Path.Combine(sub, "a.txt");
        File.WriteAllText(file, "x");
        File.SetLastWriteTime(file, new DateTime(2024, 3, 15, 12, 0, 0));
        var job = Job("Klasor", "dated", root);
        job.DateBy = "folder";
        Db.Save(job);
        Assert.Equal("success", (await BackupRunner.RunAsync(job.Id, null, default)).Status);
        Assert.StartsWith("Backup360/Klasor/2024-03-15/Eski_", Files(s.Cloud1).Single());
    }

    [Fact]
    public async Task Mirror_mode_is_one_to_one_and_preserves_removed_files()
    {
        using var s = new Sandbox();
        var src = s.Tree("Belgeler");
        var extra = Path.Combine(src, "silinecek.txt");
        File.WriteAllText(extra, "bu silinecek");
        var job = Job("Birebir", "mirror", src);
        job.MirrorDelete = true;
        Db.Save(job);
        var r = await BackupRunner.RunAsync(job.Id, null, default);
        Assert.True(r.Status == "success", r.Detail);
        var dest = Path.Combine(s.Cloud1, "Backup360", "Birebir");
        Assert.Equal(Sandbox.Hashes(src), Sandbox.Hashes(Path.Combine(dest, "Belgeler")));

        File.Delete(extra);
        await Task.Delay(1100);
        Assert.Equal("success", (await BackupRunner.RunAsync(job.Id, null, default)).Status);
        Assert.Equal(Sandbox.Hashes(src), Sandbox.Hashes(Path.Combine(dest, "Belgeler")));
        Assert.Single(Directory.GetFiles(Path.Combine(dest, "_silinenler"), "silinecek.txt", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Failed_target_is_reported_and_missing_source_warns()
    {
        using var s = new Sandbox();
        var job = Job("Eksik", "archive", s.Tree("Var"), Path.Combine(s.Root, "yok"));
        Db.Save(job);
        Assert.Equal("warning", (await BackupRunner.RunAsync(job.Id, null, default)).Status);

        var bad = Job("Kopuk", "archive", s.Tree("Var2"));
        bad.Targets = new() { "bilinmeyen" };
        Db.Save(bad);
        var r = await BackupRunner.RunAsync(bad.Id, null, default);
        Assert.Equal("failed", r.Status);
        Assert.Contains("bilinmeyen", r.Detail);
    }

    [Fact]
    public async Task Encrypted_job_stores_protected_password_and_rejects_unsupported_format()
    {
        using var s = new Sandbox();
        var job = Job("Sifreli", "archive", s.Tree("K"));
        job.PasswordEnc = Secrets.Protect("Parola!123");
        Db.Save(job);
        Assert.DoesNotContain("Parola!123", File.ReadAllText(AppPaths.Database));
        Assert.Equal("Parola!123", Secrets.Unprotect(job.PasswordEnc));
        Assert.Equal("success", (await BackupRunner.RunAsync(job.Id, null, default)).Status);
        var file = Directory.GetFiles(s.Cloud1, "*.7z", SearchOption.AllDirectories).Single();
        await Archiver.VerifyAsync(Formats.Get("7z"), file, "Parola!123", default);

        job.Formats = new() { "tar.gz" };
        Assert.Throws<ArgumentException>(() => Validation.Check(job));
    }

    [Fact]
    public void Same_named_sources_are_rejected()
    {
        var job = Job("D", "archive", @"C:\a\Belge", @"D:\b\Belge");
        Assert.Throws<ArgumentException>(() => Validation.Check(job));
    }
}

public class MoveTests
{
    private static MoveRule Rule(Sandbox s, bool onlyNew = true, string? target = null)
    {
        var r = new MoveRule
        {
            Name = "Tasi", Source = Directory.CreateDirectory(Path.Combine(s.Root, "gelen")).FullName,
            Target = target ?? Path.Combine(s.Root, "arsiv"), StableSeconds = 0, OnlyNew = onlyNew,
        };
        Db.Save(r);
        return r;
    }

    [Fact]
    public async Task Only_new_files_move_and_structure_is_kept()
    {
        using var s = new Sandbox();
        var r = Rule(s);
        File.WriteAllText(Path.Combine(r.Source, "eski.txt"), "onceden vardi");
        MoveEngine.CaptureBaseline(r);

        Directory.CreateDirectory(Path.Combine(r.Source, "alt"));
        File.WriteAllText(Path.Combine(r.Source, "alt", "yeni ğ.txt"), "yeni");
        var o = await MoveEngine.RunAsync(r.Id, null, default);

        Assert.Equal(1, o.Moved);
        Assert.True(File.Exists(Path.Combine(r.Source, "eski.txt")));
        Assert.False(File.Exists(Path.Combine(r.Source, "alt", "yeni ğ.txt")));
        Assert.Equal("yeni", File.ReadAllText(Path.Combine(r.Target, "alt", "yeni ğ.txt")));
        Assert.Equal("success", Db.LastRun("rule", r.Id)!.Status);
    }

    [Fact]
    public async Task Baseline_file_that_changes_counts_as_new()
    {
        using var s = new Sandbox();
        var r = Rule(s);
        var f = Path.Combine(r.Source, "a.txt");
        File.WriteAllText(f, "1");
        MoveEngine.CaptureBaseline(r);
        Assert.Equal(0, (await MoveEngine.RunAsync(r.Id, null, default)).Moved);
        File.WriteAllText(f, "degisti");
        Assert.Equal(1, (await MoveEngine.RunAsync(r.Id, null, default)).Moved);
    }

    [Fact]
    public async Task Conflict_rename_keeps_both()
    {
        using var s = new Sandbox();
        var r = Rule(s, onlyNew: false);
        Directory.CreateDirectory(r.Target);
        File.WriteAllText(Path.Combine(r.Target, "a.txt"), "hedef");
        File.WriteAllText(Path.Combine(r.Source, "a.txt"), "kaynak");
        Assert.Equal(1, (await MoveEngine.RunAsync(r.Id, null, default)).Moved);
        Assert.Equal("hedef", File.ReadAllText(Path.Combine(r.Target, "a.txt")));
        Assert.Equal("kaynak", File.ReadAllText(Path.Combine(r.Target, "a (1).txt")));
    }

    [Fact]
    public async Task Conflict_skip_leaves_source()
    {
        using var s = new Sandbox();
        var r = Rule(s, onlyNew: false);
        r.Conflict = "skip";
        Db.Save(r);
        Directory.CreateDirectory(r.Target);
        File.WriteAllText(Path.Combine(r.Target, "a.txt"), "hedef");
        File.WriteAllText(Path.Combine(r.Source, "a.txt"), "kaynak");
        Assert.Equal(0, (await MoveEngine.RunAsync(r.Id, null, default)).Moved);
        Assert.Equal("kaynak", File.ReadAllText(Path.Combine(r.Source, "a.txt")));
    }

    [Fact]
    public async Task Open_recent_and_temp_files_wait()
    {
        using var s = new Sandbox();
        var r = Rule(s, onlyNew: false);
        var locked = Path.Combine(r.Source, "kilitli.txt");
        File.WriteAllText(Path.Combine(r.Source, "indirme.crdownload"), "x");
        using (new FileStream(locked, FileMode.Create, FileAccess.Write, FileShare.None))
            Assert.Equal(0, (await MoveEngine.RunAsync(r.Id, null, default)).Moved);
        Assert.True(File.Exists(Path.Combine(r.Source, "indirme.crdownload")));

        r.StableSeconds = 600;
        Db.Save(r);
        Assert.Equal(0, (await MoveEngine.RunAsync(r.Id, null, default)).Moved);
        r.StableSeconds = 0;
        Db.Save(r);
        Assert.Equal(1, (await MoveEngine.RunAsync(r.Id, null, default)).Moved);
    }

    [Fact]
    public async Task Moves_across_drives_with_verification()
    {
        using var s = new Sandbox();
        var other = Path.Combine(@"D:\GLOBAL\Bakcup_360\.test-tmp", Guid.NewGuid().ToString("N")[..8]);
        Assert.NotEqual(Path.GetPathRoot(s.Root), Path.GetPathRoot(other));
        try
        {
            var r = Rule(s, onlyNew: false, target: other);
            var bytes = RandomNumberGenerator.GetBytes(2_000_000);
            File.WriteAllBytes(Path.Combine(r.Source, "b.bin"), bytes);
            Assert.Equal(1, (await MoveEngine.RunAsync(r.Id, null, default)).Moved);
            Assert.False(File.Exists(Path.Combine(r.Source, "b.bin")));
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(other, "b.bin")));
            Assert.Empty(Directory.GetFiles(other, "*.b360tmp"));
        }
        finally { Directory.Delete(@"D:\GLOBAL\Bakcup_360\.test-tmp", true); }
    }

    [Fact]
    public void Target_inside_source_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => Validation.Check(new MoveRule { Name = "x", Source = @"C:\a", Target = @"C:\a\b" }));
        Assert.Throws<ArgumentException>(() => Validation.Check(new MoveRule { Name = "x", Source = @"C:\a", Target = @"c:\A\" }));
        Validation.Check(new MoveRule { Name = "x", Source = @"C:\a", Target = @"C:\ab" });
    }
}

public class AuthTests
{
    [Fact]
    public void Login_lockout_and_password_change()
    {
        using var s = new Sandbox();
        Assert.False(Auth.HasUsers());
        Assert.Throws<ArgumentException>(() => Auth.AddUser("admin", "kisa"));
        Auth.AddUser("admin", "GucluParola1");
        Assert.Throws<ArgumentException>(() => Auth.AddUser("ADMIN", "GucluParola2"));
        Assert.Equal(LoginResult.Ok, Auth.Login("Admin", "GucluParola1"));
        Assert.Equal(LoginResult.Invalid, Auth.Login("yok", "GucluParola1"));
        for (var i = 0; i < 4; i++) Assert.Equal(LoginResult.Invalid, Auth.Login("admin", "yanlis"));
        Assert.Equal(LoginResult.Locked, Auth.Login("admin", "yanlis"));
        Assert.Equal(LoginResult.Locked, Auth.Login("admin", "GucluParola1"));
        Auth.ChangePassword("admin", "YeniParola99");
        Assert.Equal(LoginResult.Ok, Auth.Login("admin", "YeniParola99"));
        Assert.Throws<InvalidOperationException>(() => Auth.DeleteUser("admin"));
        Auth.ResetLogins();
        Assert.False(Auth.HasUsers());
    }

    [Fact]
    public void Database_is_a_single_portable_file()
    {
        using var s = new Sandbox();
        Auth.AddUser("admin", "GucluParola1");
        Db.Save(new Job { Name = "Kalici", Sources = new() { @"C:\x" }, Targets = new() { "gdrive" } });
        var copy = Path.Combine(s.Root, "kopya");
        Directory.CreateDirectory(copy);
        Db.ExportTo(Path.Combine(copy, "backup360.db"));
        Assert.False(File.Exists(AppPaths.Database + "-wal"));
        Environment.SetEnvironmentVariable("BACKUP360_DATA", copy);
        Assert.Equal("Kalici", Db.Jobs().Single().Name);
        Assert.Equal(LoginResult.Ok, Auth.Login("admin", "GucluParola1"));
    }
}

public class SchedulerTests
{
    [Theory]
    [InlineData("minutes", 5, "02:00", "Monday")]
    [InlineData("hourly", 2, "02:00", "Monday")]
    [InlineData("daily", 1, "03:30", "Monday")]
    [InlineData("weekly", 1, "04:15", "Monday,Friday")]
    public async Task Task_is_registered_with_next_run_and_removed(string type, int interval, string time, string days)
    {
        using var s = new Sandbox();
        var name = "Backup360-Test-" + Guid.NewGuid().ToString("N")[..6];
        try
        {
            await TaskPlanner.ApplyAsync(name, "--run-job 999999", new Schedule { Type = type, Interval = interval, Time = time, Days = days });
            Assert.True(await TaskPlanner.ExistsAsync(name));
            var times = await TaskPlanner.ReadTimesAsync();
            Assert.True(times.TryGetValue(name, out var t) && t.Next > DateTime.Now, $"Sonraki çalışma okunamadı: {t.Next}");
        }
        finally { await TaskPlanner.RemoveAsync(name); }
        Assert.False(await TaskPlanner.ExistsAsync(name));
    }
}
