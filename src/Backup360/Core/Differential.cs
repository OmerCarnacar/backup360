using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Backup360.Core;

/// <summary>
/// Tam + fark yedeği. Tam yedek alınırken öğedeki dosyaların listesi (boyut ve değiştirilme zamanı) saklanır.
/// Sonraki çalışmalarda yalnızca bu listeden beri yeni, değişen ve silinen dosyalar fark arşivine girer.
/// Her fark doğrudan son tam yedeğe dayanır (zincir yoktur); geri yükleme için hep iki dosya yeter.
/// Fark arşivinin adı dayandığı tam yedeğin zamanını taşır: Öğe_FARK-20261118-160000_20261120-160000.zip
/// </summary>
public static class Differential
{
    public const string Mark = "_FARK-";
    public const string MarkerFile = "_backup360_fark.txt";

    private static readonly Regex DiffItem = new(@"^(?<item>.+)_FARK-(?<base>\d{8}-\d{6})$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Dosya için Size, klasör için -1.</summary>
    public sealed record Entry(long Ticks, long Size)
    {
        public bool IsDir => Size < 0;
    }

    public sealed record Snapshot(string BaseStamp, DateTime Created, IReadOnlyList<string> Formats, Dictionary<string, Entry> Files);

    public sealed record Plan(string Stage, int Changed, int Deleted, int NewDirs)
    {
        /// <summary>Tam yedekten beri hiçbir şey değişmedi: fark dosyası üretmeye gerek yok.</summary>
        public bool Empty => Changed == 0 && Deleted == 0 && NewDirs == 0;
    }

    /// <summary>Fark yedeği yalnızca sıkıştırılmış yedeklerde anlamlıdır.</summary>
    public static bool Applies(Job job) => job.Differential && (job.Mode == "archive" || (job.Mode == "dated" && job.DatedOutput == "archive"));

    public static string DiffName(string item, string baseStamp) => item + Mark + baseStamp;

    /// <summary>Dosya adındaki öğe kısmını ayırır: fark yedeğiyse asıl öğe adı ve dayandığı tam yedeğin zamanı.</summary>
    public static (string Item, string? Base) Split(string item) =>
        DiffItem.Match(item) is { Success: true } m ? (m.Groups["item"].Value, m.Groups["base"].Value) : (item, null);

    // ---- dosya listesi

    /// <summary>
    /// Öğenin arşivdeki yollarıyla dosya ve klasör listesi. Klasör kaynak arşivde kendi adıyla, tek dosya adıyla
    /// yer alır (7-Zip'in davranışı). where doluysa her dosyanın diskteki yolu da yazılır.
    /// </summary>
    public static Dictionary<string, Entry> Collect(IReadOnlyList<string> paths, Func<string, bool> excluded, Dictionary<string, string>? where = null)
    {
        var map = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            if (File.Exists(path))
            {
                var file = new FileInfo(path);
                if (file.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                map[file.Name] = new Entry(file.LastWriteTimeUtc.Ticks, file.Length);
                if (where != null) where[file.Name] = file.FullName;
                continue;
            }
            if (!Directory.Exists(path)) continue;
            var full = Path.GetFullPath(path);
            var root = new DirectoryInfo(full);
            // Sürücü kökü verilmişse arşivde içeriği doğrudan yer alır.
            var name = Path.GetPathRoot(full) == full ? "" : root.Name;
            if (name.Length > 0) map[name] = new Entry(0, -1);
            var pending = new Stack<(DirectoryInfo Dir, string Prefix)>();
            pending.Push((root, name.Length > 0 ? name + "/" : ""));
            while (pending.Count > 0)
            {
                var (dir, prefix) = pending.Pop();
                FileSystemInfo[] entries;
                try { entries = dir.GetFileSystemInfos(); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
                foreach (var entry in entries)
                {
                    var rel = prefix + entry.Name;
                    if (excluded(rel) || entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                    if (entry is DirectoryInfo sub)
                    {
                        map[rel] = new Entry(0, -1);
                        pending.Push((sub, rel + "/"));
                    }
                    else if (entry is FileInfo f)
                    {
                        map[rel] = new Entry(f.LastWriteTimeUtc.Ticks, f.Length);
                        if (where != null) where[rel] = f.FullName;
                    }
                }
            }
        }
        return map;
    }

    // ---- saklanan liste

    private static string FileOf(long jobId, string item) =>
        Path.Combine(Directory.CreateDirectory(Path.Combine(AppPaths.DataDir, "fark", $"is-{jobId}")).FullName, Cloud.SafeName(item) + ".txt.gz");

    public static void Save(long jobId, string item, Snapshot snapshot)
    {
        var path = FileOf(jobId, item);
        var temp = path + ".yeni";
        using (var gz = new GZipStream(File.Create(temp), CompressionLevel.Fastest))
        using (var w = new StreamWriter(gz, new UTF8Encoding(false)))
        {
            w.WriteLine($"tam\t{snapshot.BaseStamp}\t{snapshot.Created.ToString("o", CultureInfo.InvariantCulture)}\t{string.Join(",", snapshot.Formats)}");
            foreach (var (rel, e) in snapshot.Files)
                w.WriteLine($"{(e.IsDir ? "d" : "f")}\t{e.Ticks}\t{e.Size}\t{rel}");
        }
        File.Move(temp, path, true);
    }

    /// <summary>Son tam yedeğin listesi; yoksa ya da okunamıyorsa null (o zaman tam yedek alınır).</summary>
    public static Snapshot? Load(long jobId, string item)
    {
        var path = FileOf(jobId, item);
        if (!File.Exists(path)) return null;
        try
        {
            using var r = new StreamReader(new GZipStream(File.OpenRead(path), CompressionMode.Decompress), Encoding.UTF8);
            var head = r.ReadLine()?.Split('\t');
            if (head is not { Length: 4 } || head[0] != "tam") return null;
            var files = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            while (r.ReadLine() is { } line)
            {
                var p = line.Split('\t', 4);
                if (p.Length == 4) files[p[3]] = new Entry(long.Parse(p[1], CultureInfo.InvariantCulture), long.Parse(p[2], CultureInfo.InvariantCulture));
            }
            return new Snapshot(head[1], DateTime.Parse(head[2], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                head[3].Split(',', StringSplitOptions.RemoveEmptyEntries), files);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or FormatException or OverflowException) { return null; }
    }

    /// <summary>Saklanan tam yedeğe fark eklenebilir mi? Değilse nedeni döner ve tam yedek alınır.</summary>
    public static string? WhyNotUsable(Job job, string baseFolder, string item, Snapshot snapshot, DateTime today)
    {
        if ((today.Date - snapshot.Created.Date).TotalDays >= Math.Max(1, job.FullEveryDays))
            return $"son tam yedek {snapshot.Created:dd.MM.yyyy} tarihli, yenisi zamanı geldi";
        if (job.Formats.Any(f => !snapshot.Formats.Contains(f, StringComparer.OrdinalIgnoreCase)))
            return "biçim değişti";
        var records = Db.BackupFiles();
        foreach (var target in job.Targets)
            foreach (var format in job.Formats)
            {
                var name = $"{item}_{snapshot.BaseStamp}.{Formats.Get(format).Extension}";
                var row = records.FirstOrDefault(f => f.Target == target && f.Base == baseFolder && (f.Path == name || f.Path.EndsWith("/" + name, StringComparison.Ordinal)));
                if (row == null || row.Status is "missing" or "corrupt")
                    return $"dayanılacak tam yedek {target} hedefinde yok";
                if (Cloud.IsLocalPath(target) && !File.Exists(Path.Combine(target, baseFolder.Replace('/', '\\'), row.Path.Replace('/', '\\'))))
                    return $"dayanılacak tam yedek {target} klasöründe yok";
            }
        return null;
    }

    // ---- fark arşivinin hazırlanması

    /// <summary>
    /// Değişen dosyaları arşivdeki yollarıyla geçici klasöre kopyalar ve silinenlerin listesini yazar. Geçici klasörün
    /// kökündeki öğeler sıkıştırılınca fark arşivi, tam yedekle aynı yol düzenine sahip olur.
    /// </summary>
    public static Plan Stage(string work, Snapshot snapshot, Dictionary<string, Entry> current, Dictionary<string, string> where, Action<string> warn)
    {
        var stage = Directory.CreateDirectory(Path.Combine(work, "fark-" + Guid.NewGuid().ToString("N")[..8])).FullName;
        var deleted = snapshot.Files.Keys.Where(k => !current.ContainsKey(k)).ToList();
        var changed = 0;
        var newDirs = 0;
        foreach (var (rel, now) in current)
        {
            var known = snapshot.Files.TryGetValue(rel, out var then);
            var target = Path.Combine(stage, rel.Replace('/', Path.DirectorySeparatorChar));
            if (now.IsDir)
            {
                // Yeni klasör boş olsa da geri yüklemede oluşsun.
                if (!known || !then!.IsDir)
                {
                    Directory.CreateDirectory(target);
                    newDirs++;
                }
                continue;
            }
            if (known && !then!.IsDir && then.Ticks == now.Ticks && then.Size == now.Size) continue;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(where[rel], target, true);
                changed++;
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
            {
                // Listelendikten sonra silindi: geri yüklemede de olmamalı.
                if (known) deleted.Add(rel);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Uygulamanın kendi kilit ve canlı durum dosyaları çalışırken hep açıktır; eksik sayılmaz.
                if (!Archiver.IsOwnTransient(where[rel])) warn($"  uyarı: yedeklenemedi: {where[rel]} : {e.Message}");
            }
        }
        var lines = new List<string> { "# Backup360 fark yedeği", "tam=" + snapshot.BaseStamp };
        lines.AddRange(deleted.Select(d => "silinen=" + d));
        File.WriteAllLines(Path.Combine(stage, MarkerFile), lines, new UTF8Encoding(false));
        return new Plan(stage, changed, deleted.Count, newDirs);
    }

    // ---- geri yükleme

    /// <summary>
    /// Açılmış fark arşivini, tam yedeğin açıldığı klasörün üzerine uygular: yeni ve değişen dosyalar yazılır,
    /// silinenler kaldırılır. Listedeki hiçbir yol hedef klasörün dışına çıkamaz.
    /// </summary>
    public static (int Written, int Removed) Apply(string diffDir, string destination)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        var marker = Path.Combine(diffDir, MarkerFile);
        var deleted = File.Exists(marker)
            ? File.ReadAllLines(marker).Where(l => l.StartsWith("silinen=", StringComparison.Ordinal)).Select(l => l[8..]).ToList()
            : new List<string>();
        var written = 0;
        foreach (var dir in Directory.EnumerateDirectories(diffDir, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(root, Path.GetRelativePath(diffDir, dir)));
        foreach (var file in Directory.EnumerateFiles(diffDir, "*", SearchOption.AllDirectories))
        {
            if (string.Equals(file, marker, StringComparison.OrdinalIgnoreCase)) continue;
            var target = Path.Combine(root, Path.GetRelativePath(diffDir, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
            written++;
        }
        var removed = 0;
        foreach (var rel in deleted.OrderByDescending(d => d.Length))
        {
            var full = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            if (File.Exists(full)) { File.Delete(full); removed++; }
            else if (Directory.Exists(full)) { Directory.Delete(full, true); removed++; }
        }
        return (written, removed);
    }

    // ---- saklama

    /// <summary>
    /// Silinmek üzere seçilen yedeklerden, kalan bir fark yedeğinin dayandığı tam yedeği çıkarır:
    /// fark durdukça tam yedeği silinmez. list aynı öğenin yedekleridir.
    /// </summary>
    public static void ProtectBases(IReadOnlyList<(string Stamp, string? Base)> list, ISet<int> drop)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (drop.Contains(i) || list[i].Base is not { } baseStamp) continue;
            for (var j = 0; j < list.Count; j++)
                if (list[j].Base == null && list[j].Stamp == baseStamp) drop.Remove(j);
        }
    }
}
