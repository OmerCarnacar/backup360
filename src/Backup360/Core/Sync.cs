namespace Backup360.Core;

public sealed record SyncPair(string Source, string Target, string Folder);

public sealed record DiffEntry(string Kind, string Path);

public sealed record DiffResult(List<DiffEntry> Items)
{
    public int Count(string kind) => Items.Count(i => i.Kind == kind);
}

/// <summary>Eşitleme: yedekleri başka klasöre, NAS'a veya farklı bir PC'ye yalnızca farklı dosyaları aktararak kopyalar.</summary>
public static class SyncPlanner
{
    public const string New = "Yeni";
    public const string Changed = "Değişti";
    public const string Extra = "Hedefte fazla";
    public const string Same = "Aynı";
    public const string Error = "Okunamadı";

    private static string Combine(string root, string name)
    {
        var r = root.Replace('\\', '/').Trim('/');
        return r.Length == 0 ? name : $"{r}/{name}";
    }

    /// <summary>İşin kaynak-hedef eşleşmelerini üretir.</summary>
    public static List<SyncPair> Pairs(Job job)
    {
        var pairs = new List<SyncPair>();
        if (job.SyncFromJob != 0)
        {
            var from = Db.GetJob(job.SyncFromJob) ?? throw new InvalidOperationException("Eşitlenecek SQL yedek işi bulunamadı.");
            if (from.Targets.Count == 0) throw new InvalidOperationException($"{from.Name} işinin hedefi yok.");
            var name = Cloud.SafeName(from.Name);
            var source = $"{from.Targets[0]}:{Cloud.JobFolder(from)}";
            foreach (var target in job.Targets)
            {
                var folder = Combine(job.RemoteFolder, name);
                if (string.Equals(source, $"{target}:{folder}", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException($"Kaynak ve hedef aynı olamaz: {source}");
                pairs.Add(new SyncPair(source, target, folder));
            }
        }
        foreach (var source in job.Sources)
        {
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(source));
            if (name.Length == 0) name = source.Replace(":", "").Trim('\\', '/');
            foreach (var target in job.Targets) pairs.Add(new SyncPair(source, target, Combine(job.RemoteFolder, name)));
        }
        return pairs;
    }

    /// <summary>Aktarmadan önce iki taraf arasındaki farkı listeler; boyut ve değiştirilme zamanı karşılaştırılır.</summary>
    public static async Task<DiffResult> DiffAsync(SyncPair pair, IEnumerable<string> excludes, CancellationToken ct, bool sizeOnly = false) =>
        new(await Cloud.CompareAsync(pair, excludes, sizeOnly, ct));
}
