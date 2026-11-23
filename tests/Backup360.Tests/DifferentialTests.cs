using System.Globalization;
using Backup360.Core;
using Xunit;

namespace Backup360.Tests;

/// <summary>Tam + fark yedeği: alma, saklama ve geri yükleme.</summary>
public class DifferentialTests
{
    private static string[] Rel(string dir) =>
        Directory.Exists(dir) ? Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/')).OrderBy(f => f, StringComparer.Ordinal).ToArray() : Array.Empty<string>();

    private static Job DailyJob(Sandbox s, string root, string local) =>
        new()
        {
            Name = "Gunluk", Mode = "dated", UseDateFolder = false, DatedOutput = "archive", OnlyChanged = true,
            Sources = new() { root }, Formats = new() { "zip" }, Targets = new() { local }, RemoteFolder = "Yedek",
            Keep = 0, Differential = true, FullEveryDays = 7,
        };

    [Fact]
    public async Task Full_then_differential_backup_restores_the_latest_state_exactly()
    {
        using var s = new Sandbox();
        var local = Path.Combine(s.Root, "Yerel");
        var root = Path.Combine(s.Root, "Projeler");
        var p1 = Directory.CreateDirectory(Path.Combine(root, "P1", "src")).FullName;
        File.WriteAllText(Path.Combine(p1, "a.txt"), "ilk");
        File.WriteAllText(Path.Combine(p1, "silinecek.txt"), "gidecek");
        File.WriteAllBytes(Path.Combine(p1, "buyuk.bin"), new byte[300_000]);
        Directory.CreateDirectory(Path.Combine(root, "P1", "eski-klasor"));
        File.WriteAllText(Path.Combine(root, "P1", "eski-klasor", "x.txt"), "x");
        Directory.CreateDirectory(Path.Combine(root, "P2"));
        File.WriteAllText(Path.Combine(root, "P2", "b.txt"), "dokunulmadı");
        var job = DailyJob(s, root, local);
        Db.Save(job);

        Assert.Equal("success", (await BackupRunner.RunAsync(job.Id, null, default)).Status);
        var fulls = Rel(local);
        Assert.Equal(2, fulls.Length);
        Assert.All(fulls, f => Assert.DoesNotContain("_FARK-", f));

        // P1 değişir: bir dosya düzenlenir, biri eklenir, biri ve bir klasör silinir, boş bir klasör eklenir.
        await Task.Delay(1100);
        File.WriteAllText(Path.Combine(p1, "a.txt"), "değişti, daha uzun");
        File.WriteAllText(Path.Combine(p1, "yeni.txt"), "yeni dosya");
        File.Delete(Path.Combine(p1, "silinecek.txt"));
        Directory.Delete(Path.Combine(root, "P1", "eski-klasor"), true);
        Directory.CreateDirectory(Path.Combine(root, "P1", "bos"));
        var lines = new List<string>();
        Assert.Equal("success", (await BackupRunner.RunAsync(job.Id, l => { lock (lines) lines.Add(l); }, default)).Status);
        var diff = Assert.Single(Rel(local).Except(fulls));
        Assert.Matches(@"/P1_FARK-\d{8}-\d{6}_\d{8}-\d{6}\.zip$", diff);
        Assert.Contains(lines, l => l.Contains("fark yedeği") && l.Contains("2 yeni/değişen") && l.Contains("3 silinen"));
        // Fark yalnızca değişenleri taşır: 300 KB'lık değişmeyen dosya içinde yok.
        Assert.True(new FileInfo(Path.Combine(local, diff)).Length < 100_000);

        var points = await RestoreCatalog.ListAsync(job, default);
        var point = points.Single(p => p.IsDiff);
        Assert.Equal("P1", point.Item);
        var dest = await RestoreEngine.RestoreFilesAsync(point, Directory.CreateDirectory(Path.Combine(s.Root, "don")).FullName, _ => { }, null, default);
        Assert.Equal(Sandbox.Hashes(Path.Combine(root, "P1")), Sandbox.Hashes(Path.Combine(dest, "P1")));
        Assert.True(Directory.Exists(Path.Combine(dest, "P1", "bos")));
        Assert.False(Directory.Exists(Path.Combine(dest, "P1", "eski-klasor")));
        Assert.False(File.Exists(Path.Combine(dest, Differential.MarkerFile)));

        // Tam yedeğin kendisi de tek başına geri yüklenir ve ilk hâli verir.
        var full = points.Single(p => !p.IsDiff && p.Item == "P1");
        var first = await RestoreEngine.RestoreFilesAsync(full, Directory.CreateDirectory(Path.Combine(s.Root, "ilk")).FullName, _ => { }, null, default);
        Assert.Equal("ilk", File.ReadAllText(Path.Combine(first, "P1", "src", "a.txt")));
        Assert.True(File.Exists(Path.Combine(first, "P1", "src", "silinecek.txt")));
    }

    [Fact]
    public async Task New_full_backup_is_taken_when_due_or_when_the_base_is_gone()
    {
        using var s = new Sandbox();
        var local = Path.Combine(s.Root, "Yerel");
        var root = Path.Combine(s.Root, "Projeler");
        Directory.CreateDirectory(Path.Combine(root, "P1"));
        var file = Path.Combine(root, "P1", "a.txt");
        File.WriteAllText(file, "1");
        var job = DailyJob(s, root, local);
        Db.Save(job);
        async Task<string> NextAsync(string content)
        {
            var before = Rel(local);
            await Task.Delay(1100);
            File.WriteAllText(file, content);
            Assert.Equal("success", (await BackupRunner.RunAsync(job.Id, null, default)).Status);
            return Path.GetFileName(Assert.Single(Rel(local).Except(before)));
        }

        Assert.DoesNotContain("_FARK-", await NextAsync("1"));
        Assert.Contains("_FARK-", await NextAsync("22"));

        // Tam yedeğin üzerinden 7 gün geçti: yeni tam yedek.
        var snapshot = Differential.Load(job.Id, "P1")!;
        Differential.Save(job.Id, "P1", snapshot with { Created = DateTime.Now.AddDays(-7) });
        var full = await NextAsync("333");
        Assert.DoesNotContain("_FARK-", full);
        Assert.Contains("_FARK-", await NextAsync("4444"));

        // Dayanılacak tam yedek silinmişse fark alınmaz, yeniden tam yedek alınır.
        File.Delete(Directory.GetFiles(local, full, SearchOption.AllDirectories).Single());
        Assert.DoesNotContain("_FARK-", await NextAsync("55555"));
    }

    [Fact]
    public async Task Retention_never_deletes_a_full_backup_while_a_differential_depends_on_it()
    {
        using var s = new Sandbox();
        var root = Path.Combine(s.Cloud1, "gunluk");
        string Stamp(int daysAgo, int hour = 16) => DateTime.Today.AddDays(-daysAgo).AddHours(hour).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        void Put(string name)
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, name), name);
        }

        var oldFull = Stamp(10);
        var newFull = Stamp(3);
        Put($"A_{oldFull}.zip");
        Put($"A_FARK-{oldFull}_{Stamp(9)}.zip");
        Put($"A_FARK-{oldFull}_{Stamp(2)}.zip");   // eski tam yedeğe dayanan, saklanacak fark
        Put($"A_{newFull}.zip");
        Put($"A_FARK-{newFull}_{Stamp(1)}.zip");

        // 5 günden eskiler silinir: eski fark gider, eski tam yedek ona dayanan yeni fark yüzünden kalır.
        Assert.Equal(1, await Cloud.ApplyRetentionAsync("gdrive", "gunluk", "zip", 0, 5, null, default));
        Assert.False(File.Exists(Path.Combine(root, $"A_FARK-{oldFull}_{Stamp(9)}.zip")));
        Assert.True(File.Exists(Path.Combine(root, $"A_{oldFull}.zip")));

        // Ona dayanan son fark da silinince eski tam yedek de gider.
        File.Delete(Path.Combine(root, $"A_FARK-{oldFull}_{Stamp(2)}.zip"));
        Assert.Equal(1, await Cloud.ApplyRetentionAsync("gdrive", "gunluk", "zip", 0, 5, null, default));
        Assert.False(File.Exists(Path.Combine(root, $"A_{oldFull}.zip")));

        // Aynı gün sabah tam, akşam fark: "aynı günde tek yedek" tam yedeği silmez.
        var morning = Stamp(0, 8);
        Put($"B_{morning}.zip");
        Put($"B_FARK-{morning}_{Stamp(0, 16)}.zip");
        await Cloud.ApplyRetentionAsync("gdrive", "gunluk", "zip", 0, 0, null, default, onePerDay: true);
        Assert.True(File.Exists(Path.Combine(root, $"B_{morning}.zip")));
        Assert.True(File.Exists(Path.Combine(root, $"B_FARK-{morning}_{Stamp(0, 16)}.zip")));
    }

    [Fact]
    public void Differential_names_and_restore_paths_are_safe()
    {
        Assert.Equal(("Proje_X", "20261118-160000"), Differential.Split("Proje_X_FARK-20261118-160000"));
        Assert.Equal(("Proje_X", (string?)null), Differential.Split("Proje_X"));

        using var s = new Sandbox();
        var diff = Directory.CreateDirectory(Path.Combine(s.Root, "fark")).FullName;
        var dest = Directory.CreateDirectory(Path.Combine(s.Root, "hedef")).FullName;
        var outside = Path.Combine(s.Root, "disarida.txt");
        File.WriteAllText(outside, "dokunulmamalı");
        File.WriteAllLines(Path.Combine(diff, Differential.MarkerFile), new[] { "tam=20261118-160000", "silinen=../disarida.txt", @"silinen=..\disarida.txt" });
        Differential.Apply(diff, dest);
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public async Task No_empty_differential_is_made_when_nothing_really_changed()
    {
        using var s = new Sandbox();
        var local = Path.Combine(s.Root, "Yerel");
        var root = Path.Combine(s.Root, "Projeler");
        Directory.CreateDirectory(Path.Combine(root, "P1"));
        File.WriteAllText(Path.Combine(root, "P1", "a.txt"), "1");
        var job = DailyJob(s, root, local);
        Db.Save(job);
        Assert.Equal("success", (await BackupRunner.RunAsync(job.Id, null, default)).Status);
        var before = Rel(local);

        // Parmak izi değişsin ama dosya içerikleri ve zamanları aynı kalsın.
        Db.SetFingerprint(job.Id, "P1", "eski");
        var lines = new List<string>();
        Assert.Equal("success", (await BackupRunner.RunAsync(job.Id, l => { lock (lines) lines.Add(l); }, default)).Status);
        Assert.Equal(before, Rel(local));
        Assert.Contains(lines, l => l.Contains("fark yedeği gerekmedi"));
        Assert.NotEqual("eski", Db.GetFingerprint(job.Id, "P1"));
    }

    [Fact]
    public void Option_is_off_by_default_and_only_for_compressed_backups()
    {
        Assert.False(new Job().Differential);
        Assert.False(Differential.Applies(new Job { Mode = "dated", DatedOutput = "folder", Differential = true }));
        Assert.False(Differential.Applies(new Job { Mode = "database", Differential = true }));
        Assert.False(Differential.Applies(new Job { Mode = "mirror", Differential = true }));
        Assert.True(Differential.Applies(new Job { Mode = "archive", Differential = true }));
        Assert.True(Differential.Applies(new Job { Mode = "dated", DatedOutput = "archive", Differential = true }));
    }
}
