using Backup360.Core;
using Xunit;

namespace Backup360.Tests;

public class PipelineTests
{
    private static void Touch(string dir, string name)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name), name);
    }

    [Fact]
    public void Prune_removes_older_days_but_keeps_newest_copy_of_each_item()
    {
        using var s = new Sandbox();
        var store = Path.Combine(s.Root, "depo");
        var (old, now) = (Path.Combine(store, "07-11-2026_16_00"), Path.Combine(store, "09-11-2026_16_00"));
        Touch(old, "A_20261107-160000.7z");
        Touch(old, "B_20261107-160000.7z");
        Touch(now, "A_20261109-160000.7z");
        Touch(Path.Combine(store, "diger"), "A_20261101-160000.7z");

        Assert.Equal(1, WorkFolder.Prune(store, 1, new DateTime(2026, 11, 9), null));
        Assert.False(File.Exists(Path.Combine(old, "A_20261107-160000.7z")));
        Assert.True(File.Exists(Path.Combine(old, "B_20261107-160000.7z")));
        Assert.True(File.Exists(Path.Combine(now, "A_20261109-160000.7z")));
        Assert.True(File.Exists(Path.Combine(store, "diger", "A_20261101-160000.7z")));
    }

    [Fact]
    public void Prune_with_three_days_keeps_two_days_ago_and_removes_empty_run_folders()
    {
        using var s = new Sandbox();
        var store = Path.Combine(s.Root, "depo");
        Touch(Path.Combine(store, "06-11-2026_16_00"), "A_20261106-160000.7z");
        Touch(Path.Combine(store, "07-11-2026_16_00"), "A_20261107-160000.7z");
        Touch(Path.Combine(store, "09-11-2026_16_00"), "A_20261109-160000.7z");
        WorkFolder.Prune(store, 3, new DateTime(2026, 11, 9), null);
        Assert.False(Directory.Exists(Path.Combine(store, "06-11-2026_16_00")));
        Assert.True(Directory.Exists(Path.Combine(store, "07-11-2026_16_00")));
    }

    [Fact]
    public async Task Low_memory_retries_with_next_rung_and_other_errors_do_not()
    {
        var seen = new List<int>();
        var result = await Archiver.RetryOnLowMemoryAsync(4, rung =>
        {
            seen.Add(rung);
            return rung < 2 ? throw new IOException("7-Zip hata verdi (kod 8): ERROR: Can't allocate required memory!") : Task.FromResult(true);
        }, _ => { });
        Assert.True(result);
        Assert.Equal(new[] { 0, 1, 2 }, seen);

        var count = 0;
        await Assert.ThrowsAsync<IOException>(() => Archiver.RetryOnLowMemoryAsync<bool>(4, _ => { count++; throw new IOException("7-Zip hata verdi (kod 2): disk dolu"); }, _ => { }));
        Assert.Equal(1, count);

        count = 0;
        await Assert.ThrowsAsync<IOException>(() => Archiver.RetryOnLowMemoryAsync<bool>(4, _ => { count++; throw new IOException("Can't allocate required memory"); }, _ => { }));
        Assert.Equal(4, count);
    }

    public static IEnumerable<object[]> MemoryCases() =>
        from f in Formats.All.Where(f => Archiver.RungCount(f) > 1)
        from rung in Enumerable.Range(1, Archiver.RungCount(f) - 1)
        select new object[] { f.Key, rung };

    [Theory, MemberData(nameof(MemoryCases))]
    public async Task Every_low_memory_rung_is_accepted_by_real_seven_zip(string key, int rung)
    {
        using var s = new Sandbox();
        var format = Formats.Get(key);
        var source = Path.Combine(s.Root, "veri.txt");
        File.WriteAllText(source, string.Concat(Enumerable.Repeat("backup360 ", 5000)));
        var output = Path.Combine(s.Root, "cikti." + format.SevenZipType);
        var args = new List<string> { "a", "-t" + format.SevenZipType, "-y", "-mx=9" };
        args.AddRange(Archiver.MemoryArgs(format, rung));
        args.Add(output);
        args.Add(source);
        var (code, text) = await Proc.RunAsync(Tools.SevenZip()!, args, null, default);
        Assert.True(code == 0, text);
    }

    private static Job Each(Sandbox s, string name, int folders)
    {
        var root = Path.Combine(s.Root, name);
        for (var i = 1; i <= folders; i++) Touch(Path.Combine(root, $"P{i}"), "a.txt");
        var job = new Job { Name = name, Mode = "dated", UseDateFolder = false, DatedOutput = "archive", Sources = new() { root }, Formats = new() { "zip" }, Targets = new() { "gdrive" }, Keep = 0 };
        Db.Save(job);
        return job;
    }

    [Fact]
    public async Task Subfolders_go_into_one_run_folder_keep_local_copy_and_do_not_wait_for_upload()
    {
        using var s = new Sandbox();
        var temp = Path.Combine(s.Root, "Temp");
        var job = Each(s, "Proje", 5);
        WorkFolder.Save(temp, 1);
        try
        {
            var lines = new List<string>();
            var r = await BackupRunner.RunAsync(job.Id, l => { lock (lines) lines.Add(l); }, default);
            Assert.True(r.Status == "success", r.Detail);

            var remote = Directory.GetFiles(s.Cloud1, "*.zip", SearchOption.AllDirectories);
            Assert.Equal(5, remote.Length);
            var runDir = Assert.Single(remote.Select(f => Path.GetFileName(Path.GetDirectoryName(f))).Distinct());
            Assert.NotNull(WorkFolder.ParseRunFolder(runDir!));

            var local = Directory.GetFiles(Path.Combine(temp, "Proje", runDir!), "*.zip");
            Assert.Equal(5, local.Length);
            Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(temp, "_calisma")));

            var second = lines.FindIndex(l => l.Contains("[P2.zip] sıkıştırılıyor"));
            var firstDone = lines.FindIndex(l => l.Contains("[P1.zip] tamam."));
            Assert.InRange(second, 0, firstDone);
        }
        finally { WorkFolder.Save("", 1); }
    }

    [Fact]
    public async Task Zero_keep_days_sends_and_leaves_no_local_copy()
    {
        using var s = new Sandbox();
        var temp = Path.Combine(s.Root, "Temp");
        var job = Each(s, "Sifir", 2);
        WorkFolder.Save(temp, 0);
        try
        {
            Assert.Equal("success", (await BackupRunner.RunAsync(job.Id, null, default)).Status);
            Assert.Equal(2, Directory.GetFiles(s.Cloud1, "*.zip", SearchOption.AllDirectories).Length);
            Assert.Empty(Directory.GetFiles(temp, "*.zip", SearchOption.AllDirectories));
        }
        finally { WorkFolder.Save("", 1); }
    }

    [Fact]
    public void Temp_inside_a_source_is_rejected_and_falls_back_at_run_time()
    {
        using var s = new Sandbox();
        var job = Each(s, "Kaynak", 1);
        var inside = Path.Combine(s.Root, "Kaynak", "Temp");
        Assert.Throws<ArgumentException>(() => WorkFolder.Save(inside, 1));

        Db.SetSetting("work_dir", inside);
        try
        {
            var warnings = new List<string>();
            var space = WorkFolder.Create(job, warnings.Add);
            Assert.Null(space.Store);
            Assert.Single(warnings);
            Assert.False(WorkFolder.IsInside(space.Work, job.Sources[0]));
            Directory.Delete(space.Work);
        }
        finally { Db.SetSetting("work_dir", ""); }
    }

    [Theory]
    [InlineData("PROJE_YEDEKLE", "Proje_yedekle", false, "PROJE_YEDEKLE")]
    [InlineData("Yedek/PROJE_YEDEKLE", "Proje_yedekle", false, "Yedek/PROJE_YEDEKLE")]
    [InlineData("Backup360", "Proje_yedekle", false, "Backup360/Proje_yedekle")]
    [InlineData("PROJE_YEDEKLE", "Proje_yedekle", true, "PROJE_YEDEKLE/Proje_yedekle")]
    public void Job_named_folder_is_not_nested_twice(string remote, string name, bool useDate, string expected) =>
        Assert.Equal(expected, Cloud.JobFolder(new Job { Name = name, Mode = "dated", UseDateFolder = useDate, RemoteFolder = remote }));

    [Fact]
    public async Task Retention_understands_run_folders()
    {
        using var s = new Sandbox();
        var root = Path.Combine(s.Cloud1, "yedek");
        Touch(Path.Combine(root, "01-01-2020_16_00"), "A_20200101-160000.7z");
        Touch(Path.Combine(root, "09-11-2026_16_00"), "A_20261109-160000.7z");
        await Cloud.ApplyRetentionAsync("gdrive", "yedek", "7z", 1, 0, null, default);
        Assert.False(File.Exists(Path.Combine(root, "01-01-2020_16_00", "A_20200101-160000.7z")));
        Assert.True(File.Exists(Path.Combine(root, "09-11-2026_16_00", "A_20261109-160000.7z")));

        Directory.CreateDirectory(Path.Combine(root, "01-01-2021_16_00", "B"));
        File.WriteAllText(Path.Combine(root, "01-01-2021_16_00", "B", "x.txt"), "1");
        Directory.CreateDirectory(Path.Combine(root, "02-01-2021_16_00", "B"));
        File.WriteAllText(Path.Combine(root, "02-01-2021_16_00", "B", "x.txt"), "2");
        await Cloud.ApplyFolderRetentionAsync("gdrive", "yedek", 1, 0, null, default);
        Assert.False(Directory.Exists(Path.Combine(root, "01-01-2021_16_00", "B")));
        Assert.True(Directory.Exists(Path.Combine(root, "02-01-2021_16_00", "B")));
    }
}
