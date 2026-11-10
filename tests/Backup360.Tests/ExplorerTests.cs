using Backup360.Core;
using Xunit;

namespace Backup360.Tests;

public class ExplorerTests
{
    [Fact]
    public async Task List_navigate_and_download()
    {
        using var s = new Sandbox();
        var folder = Directory.CreateDirectory(Path.Combine(s.Cloud1, "Yedekler", "Alt Ğü")).FullName;
        File.WriteAllText(Path.Combine(s.Cloud1, "Yedekler", "kok.txt"), "kok");
        File.WriteAllText(Path.Combine(folder, "ic.txt"), "ic");

        var root = await CloudExplorer.ListAsync("gdrive", "", default);
        Assert.True(root.Single().IsDir);

        var inner = await CloudExplorer.ListAsync("gdrive", "Yedekler", default);
        Assert.Equal(new[] { "Alt Ğü", "kok.txt" }, inner.Select(e => e.Name));
        Assert.True(inner[0].IsDir);
        Assert.Equal(3, inner[1].Size);
        Assert.Equal("Yedekler/kok.txt", inner[1].Path);

        var down = Directory.CreateDirectory(Path.Combine(s.Root, "indir")).FullName;
        var seen = new List<TransferStats>();
        await CloudExplorer.DownloadAsync("gdrive", inner[1], down, null, seen.Add, default);
        await CloudExplorer.DownloadAsync("gdrive", inner[0], down, null, null, default);
        Assert.Equal("kok", File.ReadAllText(Path.Combine(down, "kok.txt")));
        Assert.Equal("ic", File.ReadAllText(Path.Combine(down, "Alt Ğü", "ic.txt")));
        Assert.Equal((3L, 3L), (seen[^1].Bytes, seen[^1].Total));
    }

    [Fact]
    public async Task Retention_handles_turkish_job_names()
    {
        using var s = new Sandbox();
        var folder = Directory.CreateDirectory(Path.Combine(s.Cloud1, "Günlük Şirket")).FullName;
        File.WriteAllText(Path.Combine(folder, "Günlük Şirket_20200101-000000.7z"), "eski");
        File.WriteAllText(Path.Combine(folder, "Günlük Şirket_20200102-000000.7z"), "yeni");
        Assert.Equal(1, await Cloud.ApplyRetentionAsync("gdrive", "Günlük Şirket", "7z", 1, 0, null, default));
        Assert.False(File.Exists(Path.Combine(folder, "Günlük Şirket_20200101-000000.7z")));
        Assert.True(File.Exists(Path.Combine(folder, "Günlük Şirket_20200102-000000.7z")));
    }

    [Fact]
    public async Task Listing_missing_target_or_folder_reports_error()
    {
        using var s = new Sandbox();
        await Assert.ThrowsAsync<IOException>(() => CloudExplorer.ListAsync("yok", "", default));
        await Assert.ThrowsAsync<IOException>(() => CloudExplorer.ListAsync("gdrive", "olmayan/klasor", default));
    }
}
