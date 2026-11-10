using Backup360.Core;
using Xunit;

namespace Backup360.Tests;

/// <summary>Buluta göndermeden yalnızca yerel klasöre yedekleme ve ikisinin birlikte kullanımı.</summary>
public class LocalOnlyTests
{
    private static string[] Rel(string dir) =>
        Directory.Exists(dir) ? Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/')).OrderBy(f => f, StringComparer.Ordinal).ToArray() : Array.Empty<string>();

    [Fact]
    public async Task Archive_job_backs_up_to_a_local_folder_without_any_cloud_target()
    {
        using var s = new Sandbox();
        TargetStore.Delete("gdrive");
        TargetStore.Delete("nas");
        var local = Path.Combine(s.Root, "Yerel Yedek");
        var src = s.Tree("Belgeler");
        var job = new Job { Name = "Yalniz Yerel", Mode = "archive", Sources = new() { src }, Formats = new() { "7z" }, Targets = new() { local }, RemoteFolder = "Yedek", Keep = 1 };
        Validation.Check(job);
        Db.Save(job);

        var r = await BackupRunner.RunAsync(job.Id, null, default);
        Assert.True(r.Status == "success", r.Detail);
        Assert.Matches(@"^Yedek/Yalniz Yerel/Yalniz Yerel_\d{8}-\d{6}\.7z$", Assert.Single(Rel(local)));
        Assert.Empty(Rel(s.Cloud1));

        // Saklama kuralı, sağlık denetimi ve geri yükleme yerel klasörde de çalışır.
        await Task.Delay(1100);
        Assert.Equal("success", (await BackupRunner.RunAsync(job.Id, null, default)).Status);
        Assert.Single(Rel(local));
        var audit = await BackupAudit.RunAsync(true, null, default);
        Assert.True(audit.Status == "success", audit.Detail);

        var point = (await RestoreCatalog.ListAsync(job, default)).Single();
        var dest = await RestoreEngine.RestoreFilesAsync(point, Directory.CreateDirectory(Path.Combine(s.Root, "don")).FullName, _ => { }, null, default);
        Assert.Equal(Sandbox.Hashes(src), Sandbox.Hashes(Path.Combine(dest, "Belgeler")));
    }

    [Fact]
    public async Task Each_subfolder_and_uncompressed_jobs_work_locally_too()
    {
        using var s = new Sandbox();
        var local = Path.Combine(s.Root, "Yerel");
        var root = Path.Combine(s.Root, "Projeler");
        foreach (var name in new[] { "P1", "P2" })
        {
            Directory.CreateDirectory(Path.Combine(root, name));
            File.WriteAllText(Path.Combine(root, name, "a.txt"), name);
        }
        var each = new Job { Name = "Her", Mode = "dated", UseDateFolder = false, DatedOutput = "archive", Sources = new() { root }, Formats = new() { "zip" }, Targets = new() { local }, RemoteFolder = "Arsiv", Keep = 0 };
        Db.Save(each);
        Assert.Equal("success", (await BackupRunner.RunAsync(each.Id, null, default)).Status);
        Assert.All(Rel(local), f => Assert.Matches(@"^Arsiv/Her/\d{2}-\d{2}-\d{4}_\d{2}_\d{2}/P\d_\d{8}-\d{6}\.zip$", f));
        Assert.Equal(2, Rel(local).Length);

        var mirror = new Job { Name = "Kopya", Mode = "mirror", Sources = new() { root }, Targets = new() { local }, RemoteFolder = "Duz" };
        Db.Save(mirror);
        Assert.Equal("success", (await BackupRunner.RunAsync(mirror.Id, null, default)).Status);
        Assert.Equal(new[] { "Projeler/P1/a.txt", "Projeler/P2/a.txt" }, Rel(Path.Combine(local, "Duz", "Kopya")));
    }

    [Fact]
    public async Task Local_folder_and_cloud_can_be_used_together()
    {
        using var s = new Sandbox();
        var local = Path.Combine(s.Root, "Yerel");
        var job = new Job { Name = "Ikisi", Mode = "archive", Sources = new() { s.Tree("Veri") }, Formats = new() { "zip" }, Targets = new() { local, "gdrive" }, RemoteFolder = "Y" };
        Db.Save(job);
        var r = await BackupRunner.RunAsync(job.Id, null, default);
        Assert.True(r.Status == "success", r.Detail);
        Assert.Equal(Rel(local), Rel(s.Cloud1));
        Assert.Single(Rel(local));
        Assert.Equal(2, (await RestoreCatalog.ListAsync(job, default)).Count);
    }

    /// <summary>Gerçek kullanımda bulundu: geçici klasör ile yerel yedek klasörü aynı seçilince dosya kendi üzerine yazılmaya çalışılıyordu.</summary>
    [Fact]
    public async Task Temp_folder_and_local_backup_folder_can_be_the_same()
    {
        using var s = new Sandbox();
        var shared = Path.Combine(s.Root, "OrtakKlasor");
        var root = Path.Combine(s.Root, "Projeler");
        foreach (var name in new[] { "P1", "P2", "P3" })
        {
            Directory.CreateDirectory(Path.Combine(root, name));
            File.WriteAllText(Path.Combine(root, name, "a.txt"), name);
        }
        var job = new Job { Name = "Proje_yedekle", Mode = "dated", UseDateFolder = false, DatedOutput = "archive", Sources = new() { root }, Formats = new() { "7z" }, Targets = new() { shared }, RemoteFolder = "PROJE_YEDEKLE", Keep = 7, KeepDays = 0 };
        Db.Save(job);
        WorkFolder.Save(shared, 1);
        try
        {
            var r = await BackupRunner.RunAsync(job.Id, null, default);
            Assert.True(r.Status == "success", r.Detail);
            var files = Rel(shared);
            Assert.Equal(3, files.Length);
            Assert.All(files, f => Assert.Matches(@"^(?i)proje_yedekle/\d{2}-\d{2}-\d{4}_\d{2}_\d{2}/P\d_\d{8}-\d{6}\.7z$", f));

            // Geçici klasörün "1 gün" kuralı işin kendi saklama kuralını ezmez.
            var old = Path.Combine(shared, "PROJE_YEDEKLE", "01-11-2026_10_00");
            Directory.CreateDirectory(old);
            File.Copy(Path.Combine(shared, files[0]), Path.Combine(old, "P1_20261101-100000.7z"));
            File.AppendAllText(Path.Combine(root, "P1", "a.txt"), "degisti");
            await Task.Delay(1100);
            Assert.Equal("success", (await BackupRunner.RunAsync(job.Id, null, default)).Status);
            Assert.True(File.Exists(Path.Combine(old, "P1_20261101-100000.7z")));
            Assert.Equal(5, Rel(shared).Length);
        }
        finally { WorkFolder.Save("", 1); }
    }

    [Fact]
    public void Local_folder_rules()
    {
        using var s = new Sandbox();
        var src = Directory.CreateDirectory(Path.Combine(s.Root, "Kaynak")).FullName;
        Assert.True(Cloud.IsLocalPath(@"E:\Yedekler"));
        Assert.True(Cloud.IsLocalPath(@"\\nas\paylasim\yedek"));
        Assert.False(Cloud.IsLocalPath("Google"));
        Assert.False(Cloud.IsLocalPath("C:"));
        Assert.True(Cloud.TargetExists(@"E:\Yedekler"));
        Assert.False(Cloud.TargetExists("tanimsiz"));

        Job With(params string[] targets) => new() { Name = "K", Mode = "archive", Sources = new() { src }, Formats = new() { "7z" }, Targets = targets.ToList() };
        Assert.Throws<ArgumentException>(() => Validation.Check(With()));
        Assert.Throws<ArgumentException>(() => Validation.Check(With(Path.Combine(src, "yedek"))));
        Assert.Throws<ArgumentException>(() => Validation.Check(With(src)));
        Validation.Check(With(Path.Combine(s.Root, "Disarida")));
    }

    [Fact]
    public async Task Missing_cloud_target_still_fails_clearly()
    {
        using var s = new Sandbox();
        var job = new Job { Name = "Eksik", Mode = "archive", Sources = new() { s.Tree("V") }, Formats = new() { "zip" }, Targets = new() { Path.Combine(s.Root, "Yerel"), "olmayan-bulut" } };
        Db.Save(job);
        var r = await BackupRunner.RunAsync(job.Id, null, default);
        Assert.Equal("failed", r.Status);
        Assert.Contains("olmayan-bulut", r.Detail);
    }
}
