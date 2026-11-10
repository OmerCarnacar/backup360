using System.Globalization;
using System.Text.RegularExpressions;

namespace Backup360.Core;

public sealed record RestorePoint(Job Job, string Target, string Folder, string Path, bool IsArchive, string Item, string Stamp, string Extension, long Size, string Md5)
{
    public string When => DateTime.TryParseExact(Stamp, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.ToString("dd.MM.yyyy HH:mm") : Stamp;
    public bool IsDatabase => IsArchive && Job.Mode == "database" && Job.DbEngine != "sqlite";
}

/// <summary>Bir işin hedeflerdeki geri yükleme noktalarını listeler.</summary>
public static class RestoreCatalog
{
    private static readonly Regex Stamped = new(@"^(?<item>.+)_(?<stamp>\d{8}-\d{6})\.(?<ext>[A-Za-z0-9.]+)$", RegexOptions.Compiled);

    private static string BaseOf(Job j) => Cloud.JobFolder(j);

    public static async Task<List<RestorePoint>> ListAsync(Job job, CancellationToken ct)
    {
        var points = new List<RestorePoint>();
        var archive = job.Mode is "archive" or "database" || (job.Mode == "dated" && job.DatedOutput == "archive");
        if (job.Mode == "sync")
        {
            foreach (var pair in SyncPlanner.Pairs(job))
            {
                var name = pair.Folder[(pair.Folder.LastIndexOf('/') + 1)..];
                points.Add(new RestorePoint(job, pair.Target, pair.Folder, "", false, name, "", "", 0, ""));
            }
            return points;
        }

        var folder = BaseOf(job);
        foreach (var target in job.Targets)
        {
            if (archive)
            {
                foreach (var (path, info) in await Cloud.ListHashesAsync(target, folder, ct))
                {
                    var match = Stamped.Match(path[(path.LastIndexOf('/') + 1)..]);
                    if (!match.Success || Formats.All.All(f => f.Extension != match.Groups["ext"].Value)) continue;
                    points.Add(new RestorePoint(job, target, folder, path, true, match.Groups["item"].Value, match.Groups["stamp"].Value, match.Groups["ext"].Value, info.Size, info.Md5));
                }
            }
            else
            {
                var entries = await CloudExplorer.ListAsync(target, folder, ct);
                foreach (var e in entries.Where(x => x.IsDir && x.Name != "_silinenler"))
                    points.Add(new RestorePoint(job, target, folder, e.Name, false, e.Name, "", "", 0, ""));
            }
        }
        return points.OrderByDescending(p => p.Stamp, StringComparer.Ordinal).ThenBy(p => p.Item, StringComparer.CurrentCulture).ToList();
    }

    /// <summary>SQL Server fark ve günlük yedeği tek başına açılmaz: gereken yedek zincirini kurar.</summary>
    public static List<RestorePoint> Chain(RestorePoint selected, IReadOnlyList<RestorePoint> all)
    {
        if (selected.Job.DbEngine != "mssql") return new List<RestorePoint> { selected };
        string Db(RestorePoint p) => p.Item[..p.Item.LastIndexOf('_')];
        string Type(RestorePoint p) => p.Item[(p.Item.LastIndexOf('_') + 1)..];
        var type = Type(selected);
        if (type == "FULL") return new List<RestorePoint> { selected };

        var same = all.Where(p => p.Target == selected.Target && p.Extension == selected.Extension && Db(p) == Db(selected) && string.CompareOrdinal(p.Stamp, selected.Stamp) <= 0).ToList();
        var full = same.Where(p => Type(p) == "FULL").OrderByDescending(p => p.Stamp, StringComparer.Ordinal).FirstOrDefault()
                   ?? throw new InvalidOperationException("Bu yedek için gereken tam yedek hedefte bulunamadı.");
        var chain = new List<RestorePoint> { full };
        var last = full;
        if (type == "DIFF") { chain.Add(selected); return chain; }

        var diff = same.Where(p => Type(p) == "DIFF" && string.CompareOrdinal(p.Stamp, full.Stamp) > 0).OrderByDescending(p => p.Stamp, StringComparer.Ordinal).FirstOrDefault();
        if (diff != null) { chain.Add(diff); last = diff; }
        chain.AddRange(same.Where(p => Type(p) == "LOG" && string.CompareOrdinal(p.Stamp, last.Stamp) > 0).OrderBy(p => p.Stamp, StringComparer.Ordinal));
        return chain;
    }
}

/// <summary>Geri yükleme: önce doğrular, mevcut hiçbir dosya veya veritabanının üzerine yazmaz.</summary>
public static class RestoreEngine
{
    private static readonly Regex SafeDbName = new(@"^[\p{L}\p{N}_ -]{1,100}$", RegexOptions.Compiled);

    private static async Task<string> FetchAsync(RestorePoint p, string work, Action<string> log, Action<int>? percent, CancellationToken ct)
    {
        Directory.CreateDirectory(work);
        var name = p.Path[(p.Path.LastIndexOf('/') + 1)..];
        var local = Path.Combine(work, name);
        log($"İndiriliyor: {p.Target}:{p.Path}");
        await Cloud.DownloadFileAsync(p.Target, p.Folder, p.Path, local, ct, s => { if (s.Total > 0) percent?.Invoke((int)(s.Bytes * 100 / s.Total)); });
        var expected = p.Md5.Length > 0 ? p.Md5 : Db.BackupFiles().FirstOrDefault(f => f.Target == p.Target && f.Base == p.Folder && f.Path == p.Path)?.Md5 ?? "";
        if (expected.Length > 0 && !string.Equals(BackupAudit.Md5Of(local), expected, StringComparison.OrdinalIgnoreCase))
            throw new BackupCorruptException("İndirilen yedeğin parmak izi farklı; dosya bozulmuş.");

        log("Yedek doğrulanıyor...");
        var password = Secrets.Unprotect(p.Job.PasswordEnc);
        try { await Archiver.VerifyAsync(Formats.Get(p.Extension), local, password, ct); }
        catch (Exception e) when (e is not OperationCanceledException) { throw new BackupCorruptException("Yedek bozuk, geri yüklenmedi: " + e.Message, e); }
        return local;
    }

    /// <summary>Dosyaları seçilen klasörün içinde yeni bir klasöre açar.</summary>
    public static async Task<string> RestoreFilesAsync(RestorePoint p, string destinationRoot, Action<string> log, Action<int>? percent, CancellationToken ct)
    {
        // Aynı saniyede ikinci geri yükleme de kendi klasörüne açılır; var olan klasöre dokunulmaz.
        var name = $"{Cloud.SafeName(p.Item)}_GeriYukleme_{Cloud.Stamp()}";
        var destination = Path.Combine(destinationRoot, name);
        for (var i = 2; Directory.Exists(destination) || File.Exists(destination); i++) destination = Path.Combine(destinationRoot, $"{name}-{i}");
        if (!p.IsArchive)
        {
            var folder = p.Path.Length == 0 ? p.Folder : $"{p.Folder}/{p.Path}";
            log($"Klasör indiriliyor: {p.Target}:{folder}");
            Directory.CreateDirectory(destination);
            await Cloud.DownloadFolderAsync(p.Target, folder, destination, ct, s => { if (s.Total > 0) percent?.Invoke((int)(s.Bytes * 100 / s.Total)); });
            return destination;
        }

        var work = WorkFolder.CreateWork();
        try
        {
            var local = await FetchAsync(p, work, log, percent, ct);
            log("Dosyalar açılıyor...");
            await Archiver.ExtractAsync(Formats.Get(p.Extension), local, destination, Secrets.Unprotect(p.Job.PasswordEnc), ct, percent);
            return destination;
        }
        finally { try { Directory.Delete(work, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
    }

    private static async Task<string> ExtractDumpAsync(RestorePoint p, string folder, Action<string> log, Action<int>? percent, CancellationToken ct)
    {
        var work = Path.Combine(folder, "arsiv-" + Guid.NewGuid().ToString("N")[..8]);
        var local = await FetchAsync(p, work, log, percent, ct);
        var output = Path.Combine(work, "acik");
        await Archiver.ExtractAsync(Formats.Get(p.Extension), local, output, Secrets.Unprotect(p.Job.PasswordEnc), ct, percent);
        File.Delete(local);
        return Directory.GetFiles(output, "*", SearchOption.AllDirectories).Single();
    }

    /// <summary>Veritabanını yeni adla geri yükler. Aynı adda veritabanı varsa durur.</summary>
    public static async Task RestoreDatabaseAsync(RestorePoint selected, IReadOnlyList<RestorePoint> all, string newName, Action<string> log, Action<int>? percent, CancellationToken ct)
    {
        var job = selected.Job;
        newName = newName.Trim();
        if (job.DbEngine != "firebird" && !SafeDbName.IsMatch(newName)) throw new ArgumentException("Veritabanı adı yalnızca harf, rakam, boşluk, _ ve - içerebilir.");
        if (job.DbEngine == "firebird" && (newName.Length == 0 || File.Exists(newName))) throw new ArgumentException("Yeni Firebird dosya yolunu yazın; var olan dosyanın üzerine yazılmaz.");

        if (job.DbEngine == "mssql")
        {
            if ((await MsSql.ListAsync(job, ct)).Any(d => string.Equals(d.Name, newName, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"{newName} adında veritabanı zaten var; üzerine yazılmaz.");
            var chain = RestoreCatalog.Chain(selected, all);
            log($"{chain.Count} yedek dosyası kullanılacak.");
            var root = Path.Combine(await MsSql.BackupFolderAsync(job, ct), "b360restore-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(root);
            try
            {
                var files = new List<string>();
                foreach (var point in chain) files.Add(await ExtractDumpAsync(point, root, log, percent, ct));
                log("SQL Server geri yükleniyor...");
                await MsSql.RestoreAsNewAsync(job, files, newName, percent, ct);
                log("DBCC CHECKDB temiz.");
            }
            finally { try { Directory.Delete(root, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
            return;
        }

        var work = WorkFolder.CreateWork();
        Directory.CreateDirectory(work);
        try
        {
            var dump = await ExtractDumpAsync(selected, work, log, percent, ct);
            log("Veritabanı geri yükleniyor...");
            switch (job.DbEngine)
            {
                case "mysql": await MySqlEngine.RestoreAsNewAsync(job, dump, newName, work, ct); break;
                case "postgres": await PostgresEngine.RestoreAsNewAsync(job, dump, newName, ct); break;
                case "firebird": await FirebirdEngine.RestoreAsNewAsync(job, dump, newName, ct); break;
                default: throw new InvalidOperationException("Bu veritabanı türü dosya olarak geri yüklenir.");
            }
        }
        finally { try { Directory.Delete(work, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
    }
}
