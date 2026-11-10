using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace Backup360.Core;

public sealed record Target(string Name, string Type)
{
    public string TypeLabel => Type switch
    {
        "drive" => "Google Drive",
        "onedrive" => "OneDrive",
        "dropbox" => "Dropbox",
        "alias" or "local" => "Yerel / ağ klasörü",
        "s3" => "S3 uyumlu depolama",
        "sftp" => "SFTP",
        "ftp" => "FTP",
        "webdav" => "WebDAV",
        "smb" => "SMB ağ paylaşımı",
        _ => Type,
    };
}

/// <summary>Hedef tanımı. Auth: browser (tarayıcı), token (token/anahtar), password (şifre), none.</summary>
public sealed class TargetDefinition
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "drive";
    public string Auth { get; set; } = "browser";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string Token { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public string Url { get; set; } = "";
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
    public bool Tls { get; set; }
    public bool FullAccess { get; set; }
    public string AccessKey { get; set; } = "";
    public string SecretKey { get; set; } = "";
    public string Endpoint { get; set; } = "";
    public string Region { get; set; } = "";
    /// <summary>Klasör hedefinde klasör yolu, S3'te bucket, SMB'de paylaşım adı.</summary>
    public string Path { get; set; } = "";

    public void Validate()
    {
        void Need(string value, string label) { if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{label} boş olamaz."); }
        Need(Name, "Hedef adı");
        switch (Type)
        {
            case "drive":
                // Bulut hesaplarına yalnızca kullanıcının kendi uygulama kimliğiyle bağlanılır; paylaşılan kimlik kullanılmaz.
                if (string.IsNullOrWhiteSpace(ClientId) && !GoogleClient.HasBuiltIn)
                    throw new ArgumentException("Google Drive için Client ID gerekli. Paylaşılan ortak kimlik kullanılmaz.");
                if (!string.IsNullOrWhiteSpace(ClientId) != !string.IsNullOrWhiteSpace(ClientSecret))
                    throw new ArgumentException("Client ID ve Client Secret birlikte girilmelidir.");
                break;
            case "onedrive": Need(ClientId, "Client ID"); break;
            case "dropbox": Need(ClientId, "App key"); break;
            case "s3": Need(AccessKey, "Access Key"); Need(SecretKey, "Secret Key"); Need(Path, "Bucket"); break;
            case "sftp" or "ftp": Need(Host, "Sunucu"); Need(User, "Kullanıcı"); Need(Password, "Şifre"); break;
            case "smb": Need(Host, "Sunucu"); Need(Path, "Paylaşım adı"); Need(User, "Kullanıcı"); Need(Password, "Şifre"); break;
            case "webdav":
                Need(Url, "Adres");
                if (Auth == "password") { Need(User, "Kullanıcı"); Need(Password, "Şifre"); }
                break;
            case "alias": Need(Path, "Klasör"); break;
        }
        if (Auth == "token" && Type is "drive" or "onedrive" or "dropbox" or "webdav") Need(Token, "Token");
    }
}

public sealed record TransferStats(long Bytes, long Total, double Speed, long Eta, int Files, int FilesTotal, string? Current, int? CurrentPercent, string[] Active);

/// <summary>
/// Bulut ve klasör hedeflerine erişim. Her hedefe doğrudan kendi API'si veya protokolüyle bağlanılır;
/// araya başka bir program girmez.
/// </summary>
public static class Cloud
{
    private const int Workers = 4;

    /// <summary>Sağlayıcı hatalarını kullanıcının ne yapacağını söyleyen metne çevirir.</summary>
    public static string Explain(string text)
    {
        bool Has(string part) => text.Contains(part, StringComparison.OrdinalIgnoreCase);
        if (Has("accessNotConfigured") || Has("has not been used in project") || Has("SERVICE_DISABLED"))
            return "Google projenizde Drive API etkin değil. Hedef tanımlama ekranındaki \"Drive API'yi etkinleştir\" düğmesiyle açın, birkaç dakika sonra yeniden deneyin.";
        if (Has("rateLimitExceeded") || Has("RATE_LIMIT_EXCEEDED"))
            return "Google istek sınırına ulaşıldı. Birkaç dakika sonra yeniden deneyin.";
        if (Has("storageQuotaExceeded") || Has("insufficient_space") || Has("quotaLimitReached"))
            return "Hedefin depolama alanı dolu.";
        if (Has("invalid_grant") || Has("invalid_client") || Has("expired_access_token") || Has("InvalidAuthenticationToken"))
            return "Hesap oturumu geçersiz. Hedefi kaldırıp yeniden tanımlayın.";
        if (Has("redirect_uri_mismatch"))
            return $"Yönlendirme adresi uygulama kaydınızdakiyle eşleşmiyor. Kayda {OAuth.FixedRedirect} adresini ekleyin.";
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(3);
        return Web.Trim(string.Join(" | ", lines), 500);
    }

    public static Task<List<Target>> ListTargetsAsync(CancellationToken ct = default) => Task.FromResult(TargetStore.List());

    public static string CleanTargetName(string name)
    {
        var map = name.Trim().Replace('ı', 'i').Replace('İ', 'I').Replace('ş', 's').Replace('Ş', 'S').Replace('ğ', 'g').Replace('Ğ', 'G')
            .Replace('ü', 'u').Replace('Ü', 'U').Replace('ö', 'o').Replace('Ö', 'O').Replace('ç', 'c').Replace('Ç', 'C');
        var cleaned = Regex.Replace(map, "[^A-Za-z0-9_-]", "_").Trim('_', '-');
        // Tek harfli ad sürücü harfiyle (C:) karışır.
        if (cleaned.Length < 2) throw new ArgumentException("Hedef adı en az iki harf veya rakam içermelidir.");
        return cleaned;
    }

    public static bool UsesAccount(string type) => type is "drive" or "onedrive" or "dropbox";

    /// <summary>Tarayıcıda hesap onayı alır ve token'ı JSON olarak döndürür.</summary>
    public static async Task<string> AuthorizeAsync(TargetDefinition d, Action<string> open, CancellationToken ct) =>
        (await OAuth.AuthorizeAsync(OAuth.AppFor(d), open, ct)).ToJson();

    /// <summary>
    /// Hedefi tanımlar. Tarayıcı ile girişte onay sayfasının adresi <paramref name="log"/> ile bildirilir.
    /// Google Drive'da varsayılan drive.file kapsamıdır: uygulama yalnızca kendi yüklediği dosyaları görür.
    /// </summary>
    public static async Task CreateTargetAsync(TargetDefinition d, CancellationToken ct, Action<string>? log = null)
    {
        d.Validate();
        d.Name = CleanTargetName(d.Name);
        if (TargetStore.Exists(d.Name)) throw new ArgumentException($"Bu adda bir hedef zaten var: {d.Name}");
        if (UsesAccount(d.Type))
        {
            if (d.Auth == "browser") d.Token = await AuthorizeAsync(d, url => log?.Invoke(url), ct);
            else d.Token = OAuthToken.Parse(d.Token).ToJson();
        }
        TargetStore.Save(d);
    }

    public static Task RemoveTargetAsync(string name, CancellationToken ct = default)
    {
        TargetStore.Delete(name);
        return Task.CompletedTask;
    }

    /// <summary>İşin hedef listesindeki tam yol (D:\Yedek, \\nas\paylasim), buluta göndermeden yapılan yerel yedeğin klasörüdür.</summary>
    public static bool IsLocalPath(string target) => target.Length > 2 && Path.IsPathFullyQualified(target);

    public static bool TargetExists(string target) => IsLocalPath(target) || TargetStore.Exists(target);

    public static string TargetLabel(string target) => IsLocalPath(target) ? "Yerel: " + target : target;

    public static Remote Open(string target)
    {
        if (IsLocalPath(target)) return new LocalRemote(target);
        var d = TargetStore.Load(target) ?? throw new IOException("hedef tanımlı değil.");
        return d.Type switch
        {
            "alias" or "local" => new LocalRemote(d.Path),
            "smb" => LocalRemote.Smb(d),
            "drive" => new DriveRemote(new ApiClient(new TokenSource(d)), d.Name),
            "onedrive" => new GraphRemote(new ApiClient(new TokenSource(d))),
            "dropbox" => new DropboxRemote(new ApiClient(new TokenSource(d))),
            "s3" => new S3Remote(d),
            "sftp" => new SftpRemote(d),
            "ftp" => new FtpRemote(d),
            "webdav" => new WebDavRemote(d),
            _ => throw new IOException($"desteklenmeyen hedef türü: {d.Type}"),
        };
    }

    private static bool Wrap(Exception e) => e is not (OperationCanceledException or RemoteException or ArgumentException);

    private static async Task<T> UseAsync<T>(string target, Func<Remote, Task<T>> work, CancellationToken ct)
    {
        Remote remote;
        try { remote = Open(target); }
        catch (Exception e) when (Wrap(e)) { throw new RemoteException($"{target}: {Explain(e.Message)}", e); }
        try { return await work(remote); }
        catch (Exception e) when (Wrap(e))
        {
            ct.ThrowIfCancellationRequested();
            throw new RemoteException($"{target}: {Explain(e.Message)}", e);
        }
        finally
        {
            try { await remote.DisposeAsync(); } catch (Exception e) when (e is not OutOfMemoryException) { }
        }
    }

    private static bool Permanent(Exception e) =>
        e is UnauthorizedAccessException or FileNotFoundException or ArgumentException ||
        new[] { "invalid_grant", "invalid_client", "storageQuotaExceeded", "accessNotConfigured", "HTTP 401", "kabul edilmedi" }.Any(k => e.Message.Contains(k, StringComparison.OrdinalIgnoreCase));

    private static async Task RetryAsync(Func<Task> work, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await work();
                return;
            }
            catch (Exception e) when (e is not OperationCanceledException && attempt < 3 && !Permanent(e))
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct);
            }
        }
    }

    // ---- aktarım ölçümü

    /// <summary>Birden çok dosyanın ilerlemesini tek hız, kalan süre ve yüzde olarak toplar.</summary>
    private sealed class Meter(Action<TransferStats>? sink)
    {
        private readonly object _gate = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly Dictionary<string, (long Done, long Size)> _active = new();
        private readonly Queue<(long Time, long Bytes)> _samples = new();
        private long _finished, _published = -1000;
        private int _files;
        public long Total { get; init; }
        public int FilesTotal { get; init; }
        public long Bytes { get { lock (_gate) return _finished; } }

        public Action<long> Track(string name, long size)
        {
            lock (_gate) _active[name] = (0, size);
            return done =>
            {
                lock (_gate) _active[name] = (done, size);
                Publish(false);
            };
        }

        public void Done(string name, long size)
        {
            lock (_gate)
            {
                _active.Remove(name);
                _finished += size;
                _files++;
            }
            Publish(true);
        }

        public void Drop(string name)
        {
            lock (_gate) _active.Remove(name);
        }

        public void Publish(bool force)
        {
            if (sink == null) return;
            TransferStats stats;
            lock (_gate)
            {
                var now = _clock.ElapsedMilliseconds;
                if (!force && now - _published < 400) return;
                _published = now;
                var bytes = Math.Min(Total, _finished + _active.Values.Sum(a => Math.Min(a.Done, a.Size)));
                _samples.Enqueue((now, bytes));
                while (_samples.Count > 2 && now - _samples.Peek().Time > 8000) _samples.Dequeue();
                var (firstTime, firstBytes) = _samples.Peek();
                var speed = now > firstTime ? (bytes - firstBytes) * 1000.0 / (now - firstTime) : 0;
                var eta = bytes >= Total ? 0 : speed > 0 ? (long)((Total - bytes) / speed) : -1;
                var active = _active.Select(a => $"{(a.Value.Size > 0 ? "%" + a.Value.Done * 100 / a.Value.Size : ""),5}  {a.Key}").ToArray();
                var current = _active.Count > 0 ? _active.First() : default;
                stats = new TransferStats(bytes, Total, Math.Max(0, speed), eta, _files, FilesTotal, current.Key,
                    current.Key != null && current.Value.Size > 0 ? (int)(current.Value.Done * 100 / current.Value.Size) : null, active);
            }
            sink(stats);
        }
    }

    // ---- kaynaklar

    private sealed record SourceFile(string Rel, long Size, DateTime Modified, string? Local);

    private interface ISource : IAsyncDisposable
    {
        bool Single { get; }
        Task<List<SourceFile>> FilesAsync(CancellationToken ct);
        Task<string> FetchAsync(SourceFile file, CancellationToken ct);
        void Release(string local);
    }

    private sealed class LocalSource(string path) : ISource
    {
        public bool Single { get; } = File.Exists(path);

        public Task<List<SourceFile>> FilesAsync(CancellationToken ct) => Task.Run(() =>
        {
            var list = new List<SourceFile>();
            if (Single)
            {
                var file = new FileInfo(path);
                list.Add(new SourceFile(file.Name, file.Length, file.LastWriteTimeUtc, file.FullName));
                return list;
            }
            var root = new DirectoryInfo(path);
            // Kaynak okunamıyorsa boş liste dönmek, hedefteki her şeyin "fazla" sayılıp kaldırılmasına yol açardı.
            if (!root.Exists) throw new DirectoryNotFoundException("Kaynak klasör bulunamadı: " + path);
            Walk(root, "", list, ct);
            return list;
        }, ct);

        private static void Walk(DirectoryInfo dir, string prefix, List<SourceFile> list, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            FileSystemInfo[] entries;
            try { entries = dir.GetFileSystemInfos(); }
            catch (Exception e) when (prefix.Length > 0 && e is UnauthorizedAccessException or IOException) { return; }
            foreach (var entry in entries)
            {
                if (entry is FileInfo file) list.Add(new SourceFile(prefix + file.Name, file.Length, file.LastWriteTimeUtc, file.FullName));
                // Bağlantı noktaları (junction) izlenmez; sonsuz döngüye girebilir.
                else if (entry is DirectoryInfo sub && !sub.Attributes.HasFlag(FileAttributes.ReparsePoint)) Walk(sub, prefix + sub.Name + "/", list, ct);
            }
        }

        public Task<string> FetchAsync(SourceFile file, CancellationToken ct) => Task.FromResult(file.Local!);
        public void Release(string local) { }
        public ValueTask DisposeAsync() => default;
    }

    /// <summary>Başka bir hedefteki klasör: dosyalar geçici klasöre indirilip oradan gönderilir.</summary>
    private sealed class RemoteSource(Remote remote, string path) : ISource
    {
        private readonly string _temp = WorkFolder.CreateWork();
        public bool Single => false;

        public async Task<List<SourceFile>> FilesAsync(CancellationToken ct) =>
            (await remote.ListTreeAsync(path, false, ct)).Where(i => !i.IsDir).Select(i => new SourceFile(i.Path, i.Size, i.Modified ?? DateTime.MinValue, null)).ToList();

        public async Task<string> FetchAsync(SourceFile file, CancellationToken ct)
        {
            var local = System.IO.Path.Combine(_temp, Guid.NewGuid().ToString("N"));
            await remote.DownloadAsync(Remote.Join(path, file.Rel), local, null, ct);
            if (file.Modified.Year > 1980) File.SetLastWriteTimeUtc(local, file.Modified);
            return local;
        }

        public void Release(string local)
        {
            try { File.Delete(local); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }

        public async ValueTask DisposeAsync()
        {
            try { Directory.Delete(_temp, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            await remote.DisposeAsync();
        }
    }

    /// <summary>"hedef:klasör" biçimindeki kaynağı ayırır. Sürücü harfleri (C:) hedef sayılmaz.</summary>
    private static bool IsRemoteSource(string source, out string target, out string path)
    {
        var cut = source.IndexOf(':');
        (target, path) = cut >= 2 ? (source[..cut], source[(cut + 1)..]) : ("", "");
        return cut >= 2 && TargetStore.Exists(target);
    }

    private static ISource OpenSource(string source)
    {
        if (IsRemoteSource(source, out var target, out var path)) return new RemoteSource(Open(target), path);
        if (!File.Exists(source) && !Directory.Exists(source)) throw new DirectoryNotFoundException("Kaynak bulunamadı: " + source);
        return new LocalSource(source);
    }

    /// <summary>Desen dosya ya da klasör adına uyuyorsa o öğe aktarılmaz (7-Zip'in -xr! kuralıyla aynı).</summary>
    private static Func<string, bool> Excluder(IEnumerable<string> patterns)
    {
        var rules = patterns.Select(p => Remote.Clean(p)).Where(p => p.Length > 0)
            .Select(p => p.EndsWith("/**", StringComparison.Ordinal) ? p[..^3] : p.EndsWith("/*", StringComparison.Ordinal) ? p[..^2] : p)
            .Select(p => new Regex("^" + Regex.Escape(p).Replace("\\*\\*", ".*").Replace("\\*", "[^/]*").Replace("\\?", "[^/]") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            .ToList();
        if (rules.Count == 0) return _ => false;
        return rel =>
        {
            var parts = rel.Split('/');
            return rules.Any(r => r.IsMatch(rel) || parts.Any(r.IsMatch));
        };
    }

    /// <summary>
    /// Zamanı koruyan hedefte boyut ve zaman eşitse dosya aynıdır. Korumayan hedefte (S3, WebDAV)
    /// hedefteki zaman yükleme anıdır: kaynak ondan sonra değişmediyse aynıdır.
    /// </summary>
    private static bool Same(SourceFile source, RemoteItem remote, bool sizeOnly, bool keepsModTime)
    {
        if (source.Size != remote.Size) return false;
        if (sizeOnly || remote.Modified is not { } there || source.Modified.Year <= 1980) return true;
        return keepsModTime ? Math.Abs((source.Modified - there).TotalSeconds) <= 2 : source.Modified <= there.AddSeconds(2);
    }

    private static async Task<Dictionary<string, RemoteItem>> RemoteFilesAsync(Remote remote, string dest, ISource source, List<SourceFile> files, CancellationToken ct)
    {
        var map = new Dictionary<string, RemoteItem>(StringComparer.OrdinalIgnoreCase);
        if (source.Single)
        {
            if (await remote.StatAsync(dest, false, ct) is { IsDir: false } item) map[files[0].Rel] = item;
        }
        else foreach (var item in await remote.ListTreeAsync(dest, false, ct))
            if (!item.IsDir) map[item.Path] = item;
        return map;
    }

    /// <summary>
    /// Yalnızca yeni ve değişmiş dosyaları gönderir. <paramref name="backupDir"/> verilmişse üzerine
    /// yazılacak ve kaynaktan kalkmış dosyalar silinmez, oraya taşınır.
    /// </summary>
    private static async Task<long> CopyAsync(ISource source, Remote remote, string dest, bool delete, string? backupDir, IEnumerable<string> excludes,
        Action<string>? log, Action<TransferStats>? stats, CancellationToken ct)
    {
        var excluded = Excluder(excludes);
        var files = (await source.FilesAsync(ct)).Where(f => source.Single || !excluded(f.Rel)).ToList();
        var existing = await RemoteFilesAsync(remote, dest, source, files, ct);
        var plan = files.Where(f => !existing.TryGetValue(f.Rel, out var there) || !Same(f, there, false, remote.KeepsModTime)).ToList();
        var meter = new Meter(stats) { Total = plan.Sum(f => f.Size), FilesTotal = plan.Count };
        var errors = new ConcurrentQueue<string>();
        var queue = new ConcurrentQueue<SourceFile>(plan);

        async Task WorkAsync()
        {
            while (queue.TryDequeue(out var file))
            {
                ct.ThrowIfCancellationRequested();
                var path = source.Single ? dest : Remote.Join(dest, file.Rel);
                var replaced = existing.ContainsKey(file.Rel);
                try
                {
                    if (replaced && backupDir != null) await remote.MoveAsync(path, Remote.Join(backupDir, file.Rel), ct);
                    var local = await source.FetchAsync(file, ct);
                    try
                    {
                        var progress = meter.Track(file.Rel, file.Size);
                        await RetryAsync(() => remote.UploadAsync(local, path, progress, ct), ct);
                    }
                    finally { source.Release(local); }
                    meter.Done(file.Rel, file.Size);
                    log?.Invoke($"  {(replaced ? "Güncellendi" : "Yeni")}: {file.Rel}");
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    meter.Drop(file.Rel);
                    errors.Enqueue($"{file.Rel}: {Explain(e.Message)}");
                    log?.Invoke($"  HATA: {file.Rel}: {Explain(e.Message)}");
                }
            }
        }
        await Task.WhenAll(Enumerable.Range(0, Math.Clamp(plan.Count, 1, Workers)).Select(_ => Task.Run(WorkAsync, ct)));

        if (delete && !source.Single)
        {
            var kept = files.Select(f => f.Rel).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var extra in existing.Values.Where(r => !kept.Contains(r.Path) && !excluded(r.Path)))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (backupDir != null) await remote.MoveAsync(Remote.Join(dest, extra.Path), Remote.Join(backupDir, extra.Path), ct);
                    else await remote.DeleteAsync(Remote.Join(dest, extra.Path), false, ct);
                    log?.Invoke($"  Silindi: {extra.Path}");
                }
                catch (Exception e) when (e is not OperationCanceledException) { errors.Enqueue($"{extra.Path}: {Explain(e.Message)}"); }
            }
        }

        meter.Publish(true);
        if (!errors.IsEmpty) throw new IOException(string.Join(" ; ", errors.Take(3)) + (errors.Count > 3 ? $" (+{errors.Count - 3} hata daha)" : ""));
        return meter.Bytes;
    }

    // ---- işlemler

    /// <summary>Hedefte klasör oluşturur; ara klasörler de oluşturulur.</summary>
    public static Task CreateFolderAsync(string target, string path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Klasör yolunu yazın.");
        return UseAsync(target, async r => { await r.MkdirAsync(Remote.Clean(path), ct); return 0; }, ct);
    }

    /// <summary>Dosyayı yükler; hedefteki kopyanın boyutunu ve (sağlayıcı veriyorsa) özet değerini karşılaştırır.</summary>
    public static Task UploadAsync(string localFile, string target, string folder, Action<string>? log, Action<TransferStats>? stats, CancellationToken ct) => UseAsync(target, async r =>
    {
        var name = Path.GetFileName(localFile);
        var path = Remote.Join(folder, name);
        var size = new FileInfo(localFile).Length;
        var meter = new Meter(stats) { Total = size, FilesTotal = 1 };
        var progress = meter.Track(name, size);
        await RetryAsync(() => r.UploadAsync(localFile, path, progress, ct), ct);
        meter.Done(name, size);

        var info = await r.StatAsync(path, true, ct);
        if (info == null || info.Size != size) throw new IOException("yüklenen dosya doğrulanamadı: hedefteki boyut farklı.");
        if (info.Md5.Length > 0 && !string.Equals(info.Md5, await Task.Run(() => BackupAudit.Md5Of(localFile), ct), StringComparison.OrdinalIgnoreCase))
            throw new IOException("yüklenen dosya doğrulanamadı: hedefteki parmak izi farklı.");
        log?.Invoke($"  Yeni: {name}");
        return 0;
    }, ct);

    /// <summary>Sıkıştırmadan birebir gönderir. Silme açıksa hedeften kalkanlar önce _silinenler altına alınır.</summary>
    public static Task MirrorAsync(string source, string target, string folder, bool delete, string stamp, IEnumerable<string> excludes,
        Action<string>? log, Action<TransferStats>? stats, CancellationToken ct, string? asName = null) => UseAsync(target, async r =>
    {
        var name = asName ?? Path.GetFileName(Path.TrimEndingDirectorySeparator(source));
        if (name.Length == 0) name = source.Replace(":", "").Trim('\\', '/');
        if (!File.Exists(source) && !Directory.Exists(source)) throw new DirectoryNotFoundException("Kaynak bulunamadı: " + source);
        await using var src = new LocalSource(source);
        return await CopyAsync(src, r, Remote.Join(folder, name), delete, delete ? Remote.Join(folder, $"_silinenler/{stamp}") : null, excludes, log, stats, ct);
    }, ct);

    /// <summary>Tek eşleşmeyi eşitler; yalnızca farklı dosyalar aktarılır. Kaynak yerel klasör veya başka bir hedef olabilir.</summary>
    public static Task SyncAsync(SyncPair pair, bool delete, string backupRoot, string stamp, IEnumerable<string> excludes,
        Action<string>? log, Action<TransferStats>? stats, CancellationToken ct) => UseAsync(pair.Target, async r =>
    {
        await using var src = OpenSource(pair.Source);
        return await CopyAsync(src, r, pair.Folder, delete, delete ? Remote.Join(backupRoot, $"_silinenler/{stamp}") : null, excludes, log, stats, ct);
    }, ct);

    /// <summary>Aktarmadan iki tarafı karşılaştırır.</summary>
    public static Task<List<DiffEntry>> CompareAsync(SyncPair pair, IEnumerable<string> excludes, bool sizeOnly, CancellationToken ct) => UseAsync(pair.Target, async r =>
    {
        await using var src = OpenSource(pair.Source);
        var excluded = Excluder(excludes);
        var files = (await src.FilesAsync(ct)).Where(f => src.Single || !excluded(f.Rel)).ToList();
        var existing = await RemoteFilesAsync(r, pair.Folder, src, files, ct);
        var result = files.Select(f => new DiffEntry(!existing.TryGetValue(f.Rel, out var there) ? SyncPlanner.New : Same(f, there, sizeOnly, r.KeepsModTime) ? SyncPlanner.Same : SyncPlanner.Changed, f.Rel)).ToList();
        var kept = files.Select(f => f.Rel).ToHashSet(StringComparer.OrdinalIgnoreCase);
        result.AddRange(existing.Values.Where(e => !kept.Contains(e.Path) && !excluded(e.Path)).Select(e => new DiffEntry(SyncPlanner.Extra, e.Path)));
        return result;
    }, ct);

    /// <summary>Klasördeki dosyaların boyut ve (sağlayıcı veriyorsa) MD5 değerlerini okur. Klasör yoksa boş döner.</summary>
    public static Task<Dictionary<string, (long Size, string Md5)>> ListHashesAsync(string target, string folder, CancellationToken ct) => UseAsync(target, async r =>
    {
        var map = new Dictionary<string, (long Size, string Md5)>();
        // Bazı sağlayıcılar aynı adı iki kez barındırabilir; ilk gelen esas alınır.
        foreach (var item in await r.ListTreeAsync(folder, true, ct))
            if (!item.IsDir) map.TryAdd(item.Path, (item.Size, item.Md5));
        return map;
    }, ct);

    public static Task DownloadFileAsync(string target, string folder, string path, string local, CancellationToken ct, Action<TransferStats>? stats = null) => UseAsync(target, async r =>
    {
        var full = Remote.Join(folder, path);
        var size = stats == null ? 0 : (await r.StatAsync(full, false, ct))?.Size ?? 0;
        var meter = new Meter(stats) { Total = size, FilesTotal = 1 };
        await r.DownloadAsync(full, local, meter.Track(Remote.Split(full).Name, size), ct);
        meter.Done(Remote.Split(full).Name, size);
        return 0;
    }, ct);

    public static Task DownloadFolderAsync(string target, string folder, string local, CancellationToken ct, Action<TransferStats>? stats = null) => UseAsync(target, async r =>
    {
        var files = (await r.ListTreeAsync(folder, false, ct)).Where(i => !i.IsDir).ToList();
        var meter = new Meter(stats) { Total = files.Sum(f => f.Size), FilesTotal = files.Count };
        Directory.CreateDirectory(local);
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var destination = Path.Combine(local, file.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!Path.GetFullPath(destination).StartsWith(Path.GetFullPath(local), StringComparison.OrdinalIgnoreCase)) continue;
            await r.DownloadAsync(Remote.Join(folder, file.Path), destination, meter.Track(file.Path, file.Size), ct);
            meter.Done(file.Path, file.Size);
        }
        return 0;
    }, ct);

    /// <summary>İçi boşalan klasörü ve onunla birlikte boşalan üst klasörlerini kaldırır; kökün kendisine dokunmaz.</summary>
    private static async Task PruneEmptyAsync(Remote r, string root, IEnumerable<string> folders, CancellationToken ct)
    {
        foreach (var start in folders.Where(f => f.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(f => f.Length))
        {
            for (var dir = start; dir.Length > 0; dir = Remote.Split(dir).Parent)
            {
                try
                {
                    if ((await r.ListAsync(Remote.Join(root, dir), ct)).Count > 0) break;
                }
                catch (DirectoryNotFoundException) { continue; }
                await r.DeleteAsync(Remote.Join(root, dir), true, ct);
            }
        }
    }

    /// <summary>
    /// Her öğe ve format için son N yedeği bırakır. Yalnızca Backup360'ın ad kalıbına uyan dosyalar
    /// silinir; "yalnızca değişenler" açıkken değişmeyen klasörün tek yedeği de böylece korunur.
    /// </summary>
    public static Task<int> ApplyRetentionAsync(string target, string folder, string extension, int keep, int keepDays, Action<string>? log, CancellationToken ct)
    {
        if (keep <= 0 && keepDays <= 0) return Task.FromResult(0);
        return UseAsync(target, async r =>
        {
            var cutoff = keepDays > 0 ? DateTime.Now.AddDays(-keepDays) : DateTime.MinValue;
            bool Old(string stamp) => DateTime.ParseExact(stamp, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) < cutoff;
            var pattern = new Regex($@"^(?:\d{{4}}-\d{{2}}-\d{{2}}/|\d{{2}}-\d{{2}}-\d{{4}}_\d{{2}}_\d{{2}}/)?(?<item>.+)_(?<stamp>\d{{8}}-\d{{6}})\.{Regex.Escape(extension)}$", RegexOptions.CultureInvariant);
            var old = (await r.ListTreeAsync(folder, false, ct)).Where(i => !i.IsDir)
                .Select(i => (i.Path, Match: pattern.Match(i.Path)))
                .Where(x => x.Match.Success)
                .GroupBy(x => x.Match.Groups["item"].Value, StringComparer.OrdinalIgnoreCase)
                // En yeni kopya hiçbir kuralla silinmez: değişmeyen klasörün tek yedeği korunur.
                .SelectMany(g => g.OrderByDescending(x => x.Match.Groups["stamp"].Value, StringComparer.Ordinal)
                    .Where((x, i) => i > 0 && ((keep > 0 && i >= keep) || Old(x.Match.Groups["stamp"].Value))))
                .Select(x => x.Path)
                .ToList();

            foreach (var path in old)
            {
                await r.DeleteAsync(Remote.Join(folder, path), false, ct);
                Db.ForgetBackup(target, folder, path);
                log?.Invoke($"  eski yedek silindi: {target}:{path}");
            }
            await PruneEmptyAsync(r, folder, old.Select(p => Remote.Split(p).Parent), ct);
            return old.Count;
        }, ct);
    }

    /// <summary>İşin hedefteki klasörü. Alt klasörleri ayrı gönderen iş, kendi adını taşıyan klasöre doğrudan yazar.</summary>
    public static string JobFolder(Job j)
    {
        var root = j.RemoteFolder.Replace('\\', '/').Trim('/');
        var name = SafeName(j.Name);
        var last = root[(root.LastIndexOf('/') + 1)..];
        return j.Mode == "dated" && !j.UseDateFolder && string.Equals(last, name, StringComparison.OrdinalIgnoreCase) ? root : $"{root}/{name}";
    }

    private static DateTime FolderDate(string name) =>
        WorkFolder.ParseRunFolder(name) ?? DateTime.ParseExact(name, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Sıkıştırılmamış tarih klasörlerine saklama kuralını uygular; yalnızca tarih/öğe yapısına dokunur.</summary>
    public static Task<int> ApplyFolderRetentionAsync(string target, string folder, int keep, int keepDays, Action<string>? log, CancellationToken ct, int depth = 2)
    {
        if (keep <= 0 && keepDays <= 0) return Task.FromResult(0);
        return UseAsync(target, async r =>
        {
            var cutoff = keepDays > 0 ? DateTime.Today.AddDays(-keepDays) : DateTime.MinValue;
            var pattern = new Regex(@"^(?<date>\d{4}-\d{2}-\d{2}|\d{2}-\d{2}-\d{4}_\d{2}_\d{2})/(?<item>[^/]+" + string.Concat(Enumerable.Repeat("/[^/]+", depth - 2)) + ")$", RegexOptions.CultureInvariant);
            var old = (await r.ListTreeAsync(folder, false, ct, depth)).Where(i => i.IsDir)
                .Select(i => pattern.Match(i.Path))
                .Where(m => m.Success)
                .GroupBy(m => m.Groups["item"].Value, StringComparer.OrdinalIgnoreCase)
                .SelectMany(g => g.OrderByDescending(m => FolderDate(m.Groups["date"].Value))
                    .Where((m, i) => i > 0 && ((keep > 0 && i >= keep) || FolderDate(m.Groups["date"].Value).Date < cutoff)))
                .Select(m => m.Value)
                .ToList();
            foreach (var path in old)
            {
                await r.DeleteAsync(Remote.Join(folder, path), true, ct);
                log?.Invoke($"  eski klasör silindi: {target}:{path}");
            }
            await PruneEmptyAsync(r, folder, old.Select(p => Remote.Split(p).Parent), ct);
            return old.Count;
        }, ct);
    }

    /// <summary>Gerçek bir küçük dosya yükleyip silerek bağlantıyı ve yükleme hızını ölçer.</summary>
    public static async Task<ConnectionResult> TestAsync(string target, CancellationToken ct, Action<string>? step = null)
    {
        const string remoteFolder = ".backup360-baglanti-testi";   // Tek seviye: test sonunda hedefte iz kalmaz
        const int size = 4 * 1024 * 1024;
        var dir = WorkFolder.CreateWork();
        long latency = 0;
        try
        {
            return await UseAsync(target, async r =>
            {
                step?.Invoke("Hedefe bağlanılıyor");
                var clock = Stopwatch.StartNew();
                await r.ListAsync("", ct);
                latency = clock.ElapsedMilliseconds;

                step?.Invoke("Yükleme hızı ölçülüyor (4 MB)");
                var file = Path.Combine(dir, $"test-{Guid.NewGuid():N}.bin");
                await File.WriteAllBytesAsync(file, System.Security.Cryptography.RandomNumberGenerator.GetBytes(size), ct);
                clock.Restart();
                await r.UploadAsync(file, Remote.Join(remoteFolder, Path.GetFileName(file)), null, ct);
                var seconds = Math.Max(0.001, clock.Elapsed.TotalSeconds);
                step?.Invoke("Test dosyası siliniyor");
                await r.DeleteAsync(remoteFolder, true, ct);
                var speed = size / seconds;
                Estimator.StoreSpeed(target, speed);
                return new ConnectionResult(true, "Bağlantı başarılı", latency, speed);
            }, ct);
        }
        catch (IOException e)
        {
            return new ConnectionResult(false, e.Message, latency, 0);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    public static string SafeName(string name)
    {
        var cleaned = Regex.Replace(name.Trim(), @"[^\p{L}\p{Nd} _.-]", "_").Trim('.', ' ');
        return cleaned.Length == 0 ? "yedek" : cleaned;
    }

    public static string Stamp() => DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
}
