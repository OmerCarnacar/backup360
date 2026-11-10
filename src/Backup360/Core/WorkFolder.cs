using System.Globalization;
using System.Text.RegularExpressions;

namespace Backup360.Core;

/// <summary>Work: geçici dosyalar. Store: işin sıkıştırılmış yedeklerinin yerel kopyaları (kapalıysa null).</summary>
public sealed record WorkSpace(string Work, string? Store);

/// <summary>Sıkıştırmanın yapıldığı klasör ve orada saklanan yerel yedekler.</summary>
public static class WorkFolder
{
    private const string RunFormat = "dd-MM-yyyy_HH_mm";
    private static readonly Regex Stamped = new(@"^(?<item>.+)_(?<stamp>\d{8}-\d{6})\.(?<ext>[A-Za-z0-9.]+)$", RegexOptions.Compiled);

    public static string? Chosen => string.IsNullOrWhiteSpace(Db.GetSetting("work_dir")) ? null : Db.GetSetting("work_dir")!.Trim();
    public static int KeepDays => int.TryParse(Db.GetSetting("work_keep_days"), out var d) && d >= 0 ? d : 1;

    public static string RunFolder() => DateTime.Now.ToString(RunFormat, CultureInfo.InvariantCulture);

    public static DateTime? ParseRunFolder(string name) =>
        DateTime.TryParseExact(name, RunFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    private static string NewDirectory(string root)
    {
        SweepStale(root, TimeSpan.FromHours(6));
        return Directory.CreateDirectory(Path.Combine(root, Guid.NewGuid().ToString("N"))).FullName;
    }

    /// <summary>Yarıda kesilen çalışmaların bıraktığı klasörleri siler. İçinde yakın zamanda yazılmış dosya olan klasöre dokunmaz.</summary>
    public static int SweepStale(string workRoot, TimeSpan age)
    {
        var removed = 0;
        try
        {
            if (!Directory.Exists(workRoot)) return 0;
            foreach (var dir in new DirectoryInfo(workRoot).GetDirectories())
            {
                var newest = dir.EnumerateFileSystemInfos("*", SearchOption.AllDirectories).Select(e => e.LastWriteTimeUtc).Append(dir.LastWriteTimeUtc).Max();
                if (DateTime.UtcNow - newest < age) continue;
                dir.Delete(true);
                removed++;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return removed;
    }
    private static string SystemWork() => NewDirectory(Path.Combine(Path.GetTempPath(), "Backup360"));

    public static bool IsInside(string folder, string parent)
    {
        static string Norm(string p) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)) + Path.DirectorySeparatorChar;
        return Norm(folder).StartsWith(Norm(parent), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Geri yükleme ve denetim için geçici klasör; yedek saklamaz.</summary>
    public static string CreateWork()
    {
        if (Chosen is { } chosen)
            try { return NewDirectory(Path.Combine(chosen, "_calisma")); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        return SystemWork();
    }

    /// <summary>Seçilen klasör kullanılamıyorsa yedek yine alınır, sistem geçici klasörüne düşülür ve günlüğe yazılır.</summary>
    public static WorkSpace Create(Job job, Action<string> warn)
    {
        if (Chosen is not { } chosen) return new WorkSpace(SystemWork(), null);
        if (job.Sources.Any(s => IsInside(chosen, s)))
        {
            warn($"  uyarı: geçici klasör yedeklenen klasörün içinde olduğu için kullanılmadı: {chosen}");
            return new WorkSpace(SystemWork(), null);
        }
        try
        {
            var work = NewDirectory(Path.Combine(chosen, "_calisma"));
            // Yerel klasöre yedekleyen iş kopyasını zaten kendi kuralıyla saklar; ikinci bir yerel kopya tutulmaz.
            var keepsOwnCopy = job.Targets.Any(Cloud.IsLocalPath);
            return new WorkSpace(work, KeepDays > 0 && !keepsOwnCopy ? Directory.CreateDirectory(Path.Combine(chosen, Cloud.SafeName(job.Name))).FullName : null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            warn($"  uyarı: geçici klasör kullanılamadı, sistem klasörüne geçildi: {e.Message}");
            return new WorkSpace(SystemWork(), null);
        }
    }

    public static string? RunDirectory(string? store, string runFolder) =>
        store == null ? null : Directory.CreateDirectory(Path.Combine(store, runFolder)).FullName;

    public static void Save(string? path, int keepDays)
    {
        path = path?.Trim() ?? "";
        if (path.Length > 0)
        {
            if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Klasörün tam yolunu yazın.");
            if (Db.Jobs().SelectMany(j => j.Sources).FirstOrDefault(s => IsInside(path, s)) is { } source)
                throw new ArgumentException($"Geçici klasör yedeklenen klasörün içinde olamaz: {source}");
            Directory.CreateDirectory(path);
            var probe = Path.Combine(path, $".yazma-testi-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
        }
        Db.SetSetting("work_dir", path);
        Db.SetSetting("work_keep_days", Math.Max(0, keepDays).ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Son N takvim gününden eski yerel yedekleri siler. Her öğenin en yeni kopyası hiçbir durumda silinmez.</summary>
    public static int Prune(string? store, int keepDays, DateTime today, Action<string>? log)
    {
        if (store == null || keepDays <= 0 || !Directory.Exists(store)) return 0;
        var cutoff = today.Date.AddDays(1 - keepDays);
        var runs = Directory.GetDirectories(store)
            .Select(d => (Dir: d, Date: ParseRunFolder(Path.GetFileName(d))))
            .Where(r => r.Date != null)
            .ToList();

        var old = runs.SelectMany(r => Directory.GetFiles(r.Dir).Select(f => (File: f, r.Date, Match: Stamped.Match(Path.GetFileName(f)))))
            .Where(x => x.Match.Success)
            .GroupBy(x => x.Match.Groups["item"].Value + "|" + x.Match.Groups["ext"].Value, StringComparer.OrdinalIgnoreCase)
            .SelectMany(g => g.OrderByDescending(x => x.Match.Groups["stamp"].Value, StringComparer.Ordinal).Skip(1))
            .Where(x => x.Date!.Value.Date < cutoff)
            .Select(x => x.File)
            .ToList();

        var removed = 0;
        foreach (var file in old)
        {
            try
            {
                File.Delete(file);
                removed++;
                log?.Invoke($"  eski yerel yedek silindi: {file}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log?.Invoke($"  uyarı: silinemedi: {file}"); }
        }
        foreach (var (dir, _) in runs)
        {
            try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return removed;
    }
}
