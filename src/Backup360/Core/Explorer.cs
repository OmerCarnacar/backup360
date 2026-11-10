using System.IO;

namespace Backup360.Core;

public sealed record RemoteEntry(string Name, string Path, long Size, bool IsDir, DateTime? Modified);

/// <summary>Bağlı hedeflerin içeriğini gezme ve indirme.</summary>
public static class CloudExplorer
{
    /// <summary>Hedefteki bir klasörün içeriğini listeler: önce klasörler, sonra dosyalar.</summary>
    public static async Task<List<RemoteEntry>> ListAsync(string target, string path, CancellationToken ct)
    {
        var prefix = Remote.Clean(path);
        Remote remote;
        try { remote = Cloud.Open(target); }
        catch (Exception e) when (e is not (OperationCanceledException or ArgumentException)) { throw new IOException($"{target}: {Cloud.Explain(e.Message)}", e); }
        await using (remote)
        {
            try
            {
                return (await remote.ListAsync(prefix, ct))
                    .Select(i => new RemoteEntry(i.Path, Remote.Join(prefix, i.Path), i.IsDir ? 0 : i.Size, i.IsDir, i.Modified?.ToLocalTime()))
                    .OrderByDescending(x => x.IsDir).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            }
            catch (DirectoryNotFoundException) { throw new IOException($"{target}: klasör bulunamadı: {prefix}"); }
            catch (Exception e) when (e is not (OperationCanceledException or ArgumentException)) { throw new IOException($"{target}: {Cloud.Explain(e.Message)}", e); }
        }
    }

    /// <summary>Dosya veya klasörü yerel klasöre indirir.</summary>
    public static Task DownloadAsync(string target, RemoteEntry entry, string localFolder, Action<string>? log, Action<TransferStats>? stats, CancellationToken ct)
    {
        var destination = Path.Combine(localFolder, entry.Name);
        return entry.IsDir
            ? Cloud.DownloadFolderAsync(target, entry.Path, destination, ct, stats)
            : Cloud.DownloadFileAsync(target, "", entry.Path, destination, ct, stats);
    }
}
