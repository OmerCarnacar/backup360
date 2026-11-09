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
            var source = $"{from.Targets[0]}:{from.RemoteFolder.Replace('\\', '/').Trim('/')}/{name}";
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

    /// <summary>
    /// Aktarmadan önce iki taraf arasındaki farkı listeler. Karşılaştırma mümkünse özet değeriyle,
    /// değilse boyut ve tarihle yapılır.
    /// </summary>
    public static async Task<DiffResult> DiffAsync(SyncPair pair, IEnumerable<string> excludes, CancellationToken ct, bool sizeOnly = false)
    {
        var (code, output, error) = await Cloud.CheckAsync(pair, excludes, ct, sizeOnly);
        var items = new List<DiffEntry>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var text = line.TrimEnd('\r');
            if (text.Length < 3) continue;
            var kind = text[0] switch { '+' => New, '-' => Extra, '*' => Changed, '=' => Same, '!' => Error, _ => "" };
            if (kind.Length > 0) items.Add(new DiffEntry(kind, text[2..]));
        }
        if (items.Count == 0 && code != 0 && !error.Contains("directory not found", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Karşılaştırma yapılamadı: " + error.Trim());
        return new DiffResult(items);
    }
}
