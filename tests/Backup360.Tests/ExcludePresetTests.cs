using Backup360.Core;
using Xunit;

namespace Backup360.Tests;

public class ExcludePresetTests
{
    [Fact]
    public void Preset_is_added_and_removed_without_touching_the_users_own_rules()
    {
        var on = ExcludePreset.Apply("*.tmp; Node_Modules ;gizli", true);
        Assert.StartsWith("*.tmp;gizli;node_modules;", on);
        Assert.True(ExcludePreset.IsOn(on));
        Assert.False(ExcludePreset.IsOn("node_modules;.next"));
        Assert.Equal("*.tmp;gizli", ExcludePreset.Apply(on, false));
        Assert.Equal("", ExcludePreset.Apply("", false));
        Assert.DoesNotContain("bin", ExcludePreset.Rebuildable);
        Assert.DoesNotContain(".git", ExcludePreset.Rebuildable);
    }

    [Fact]
    public async Task Each_subfolder_archive_skips_rebuildable_folders_but_keeps_sources_and_git()
    {
        using var s = new Sandbox();
        var project = Path.Combine(s.Root, "Kod", "Proje");
        foreach (var (rel, text) in new[]
                 {
                     ("src/app.ts", "kaynak"), (".git/HEAD", "ref"), ("dist/app.js", "cikti"), ("node_modules/paket/index.js", "paket"),
                     ("web/node_modules/x/y.js", "ic"), (".next/cache/a.bin", "onbellek"), ("api/obj/a.dll", "ara"), ("obj.txt", "dosya"),
                 })
        {
            var file = Path.Combine(project, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, text);
        }
        var job = new Job
        {
            Name = "Kod", Mode = "dated", UseDateFolder = false, DatedOutput = "archive", Sources = new() { Path.Combine(s.Root, "Kod") }, Formats = new() { "zip" }, Targets = new() { "gdrive" },
            Excludes = ExcludePreset.Apply("", true).Split(';').ToList(),
        };
        Db.Save(job);
        var r = await BackupRunner.RunAsync(job.Id, null, default);
        Assert.True(r.Status == "success", r.Detail);

        var point = (await RestoreCatalog.ListAsync(job, default)).Single();
        var dest = await RestoreEngine.RestoreFilesAsync(point, Directory.CreateDirectory(Path.Combine(s.Root, "don")).FullName, _ => { }, null, default);
        var files = Directory.GetFiles(dest, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(Path.Combine(dest, "Proje"), f).Replace('\\', '/')).OrderBy(f => f, StringComparer.Ordinal);
        Assert.Equal(new[] { ".git/HEAD", "dist/app.js", "obj.txt", "src/app.ts" }, files);
    }

    [Fact]
    public async Task Folder_based_sending_skips_the_same_folders()
    {
        using var s = new Sandbox();
        var src = Path.Combine(s.Root, "Site");
        foreach (var rel in new[] { "index.html", "node_modules/a/b.js", "alt/.venv/lib.py", "alt/kod.py" })
        {
            var file = Path.Combine(src, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, rel);
        }
        var job = new Job { Name = "Site", Mode = "mirror", Sources = new() { src }, Targets = new() { "gdrive" }, Excludes = ExcludePreset.Rebuildable.ToList() };
        Db.Save(job);
        Assert.Equal("success", (await BackupRunner.RunAsync(job.Id, null, default)).Status);
        var sent = Directory.GetFiles(s.Cloud1, "*", SearchOption.AllDirectories).Select(Path.GetFileName).OrderBy(f => f);
        Assert.Equal(new[] { "index.html", "kod.py" }, sent);
    }
}
