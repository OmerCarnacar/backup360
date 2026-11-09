using System.Globalization;
using System.Text.Json;
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
    public string Path { get; set; } = "";

    public void Validate()
    {
        void Need(string value, string label) { if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{label} boş olamaz."); }
        Need(Name, "Hedef adı");
        switch (Type)
        {
            case "s3": Need(AccessKey, "Access Key"); Need(SecretKey, "Secret Key"); break;
            case "sftp" or "ftp" or "smb": Need(Host, "Sunucu"); Need(User, "Kullanıcı"); Need(Password, "Şifre"); break;
            case "webdav":
                Need(Url, "Adres");
                if (Auth == "password") { Need(User, "Kullanıcı"); Need(Password, "Şifre"); }
                else Need(Token, "Token");
                break;
            case "alias": Need(Path, "Klasör"); break;
            default:
                if (Auth == "token") Need(Token, "Token");
                break;
        }
        if (!string.IsNullOrWhiteSpace(ClientId) != !string.IsNullOrWhiteSpace(ClientSecret))
            throw new ArgumentException("Client ID ve Client Secret birlikte girilmelidir.");
    }
}

public sealed record TransferStats(long Bytes, long Total, double Speed, long Eta, int Files, int FilesTotal, string? Current, int? CurrentPercent, string[] Active);

/// <summary>Bulut ve klasör hedeflerine erişim rclone ile yapılır; ayarlar data klasöründe durur.</summary>
public static class Cloud
{
    private static string Exe => Tools.Require(Tools.Rclone(), "rclone");

    private static List<string> Args(params string[] args) => new(args) { "--config", AppPaths.RcloneConfig };

    /// <summary>Sağlayıcı hatalarını kullanıcının ne yapacağını söyleyen metne çevirir.</summary>
    public static string Explain(string text)
    {
        if (text.Contains("rateLimitExceeded", StringComparison.OrdinalIgnoreCase) || text.Contains("RATE_LIMIT_EXCEEDED", StringComparison.OrdinalIgnoreCase))
            return "Google kota sınırı aşıldı. Hedefi kaldırıp kendi Google Client ID'nizle yeniden tanımlayın.";
        if (text.Contains("storageQuotaExceeded", StringComparison.OrdinalIgnoreCase))
            return "Hedefin depolama alanı dolu.";
        if (text.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase))
            return "Hesap oturumu geçersiz. Hedefi kaldırıp yeniden tanımlayın.";
        return string.Join(" | ", text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(3));
    }

    private static string Tail(string text) => Explain(text);

    private static async Task Must(List<string> args, string error, CancellationToken ct)
    {
        var (code, text) = await Proc.RunAsync(Exe, args, null, ct);
        if (code != 0) throw new IOException($"{error}: {Tail(text)}");
    }

    public static async Task<List<Target>> ListTargetsAsync(CancellationToken ct = default)
    {
        var result = new List<Target>();
        if (!File.Exists(AppPaths.RcloneConfig) || Tools.Rclone() == null) return result;
        var (code, text, _) = await Proc.CaptureAsync(Exe, Args("listremotes", "--long"), ct);
        if (code != 0) return result;
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var cut = line.IndexOf(':');
            if (cut > 0) result.Add(new Target(line[..cut], line[(cut + 1)..].Trim()));
        }
        return result;
    }

    public static string CleanTargetName(string name)
    {
        var map = name.Trim().Replace('ı', 'i').Replace('İ', 'I').Replace('ş', 's').Replace('Ş', 'S').Replace('ğ', 'g').Replace('Ğ', 'G')
            .Replace('ü', 'u').Replace('Ü', 'U').Replace('ö', 'o').Replace('Ö', 'O').Replace('ç', 'c').Replace('Ç', 'C');
        var cleaned = Regex.Replace(map, "[^A-Za-z0-9_-]", "_").Trim('_', '-');
        if (cleaned.Length == 0) throw new ArgumentException("Hedef adı geçersiz.");
        return cleaned;
    }

    /// <summary>
    /// Hedefi tanımlar. Google Drive'da drive.file kapsamı kullanılır: uygulama yalnızca kendi
    /// yüklediği dosyaları görür. Şifreler rclone tarafından gizlenmiş biçimde saklanır.
    /// </summary>
    public static async Task CreateTargetAsync(TargetDefinition d, CancellationToken ct, Action<string>? log = null)
    {
        d.Validate();
        var name = CleanTargetName(d.Name);
        if ((await ListTargetsAsync(ct)).Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"Bu adda bir hedef zaten var: {name}");

        var args = Args("config", "create", name, d.Type);
        void Set(string key, string? value) { if (!string.IsNullOrWhiteSpace(value)) args.Add($"{key}={value.Trim()}"); }

        switch (d.Type)
        {
            case "drive":
                Set("scope", d.FullAccess ? "drive" : "drive.file");
                Set("client_id", d.ClientId);
                Set("client_secret", d.ClientSecret);
                break;
            case "s3":
                Set("provider", string.IsNullOrWhiteSpace(d.Endpoint) ? "AWS" : "Other");
                Set("env_auth", "false");
                Set("access_key_id", d.AccessKey);
                Set("secret_access_key", d.SecretKey);
                Set("endpoint", d.Endpoint);
                Set("region", d.Region);
                break;
            case "sftp" or "ftp" or "smb":
                Set("host", d.Host);
                if (d.Port > 0) Set("port", d.Port.ToString(CultureInfo.InvariantCulture));
                Set("user", d.User);
                if (d.Type == "ftp" && d.Tls) Set("explicit_tls", "true");
                break;
            case "webdav":
                Set("url", d.Url);
                Set("vendor", "other");
                if (d.Auth == "password") Set("user", d.User);
                else Set("bearer_token", d.Token);
                break;
            case "alias":
                Set("remote", d.Path);
                break;
        }

        if (d.Auth == "password" && !string.IsNullOrEmpty(d.Password))
        {
            args.Add("pass=" + d.Password);
            args.Add("--obscure");
        }
        if (d.Auth == "token" && d.Type is "drive" or "dropbox")
        {
            Set("token", d.Token);
            Set("config_refresh_token", "false");
        }
        if (d.Auth != "browser") args.Add("--non-interactive");

        var (code, text) = await Proc.RunAsync(Exe, args, log, ct);
        if (code != 0) throw new IOException("Hedef tanımlanamadı: " + Tail(text));
    }

    /// <summary>Hedefte klasör oluşturur; ara klasörler de oluşturulur.</summary>
    public static Task CreateFolderAsync(string target, string path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Klasör yolunu yazın.");
        return Must(Args("mkdir", Join(target, path.Trim())), "Klasör oluşturulamadı", ct);
    }

    public static Task RemoveTargetAsync(string name, CancellationToken ct = default) =>
        Must(Args("config", "delete", name), "Hedef silinemedi", ct);

    private static string Join(string target, string folder, string? name = null)
    {
        var path = folder.Replace('\\', '/').Trim('/');
        if (name != null) path = path.Length == 0 ? name : path + "/" + name;
        return $"{target}:{path}";
    }

    private static Action<string> StatsReader(Action<TransferStats>? stats, Action<string>? log) => line =>
    {
        if (!line.StartsWith('{')) return;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.TryGetProperty("stats", out var s))
            {
                string? current = null;
                int? percent = null;
                var active = new List<string>();
                if (s.TryGetProperty("transferring", out var list) && list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0)
                {
                    var first = list[0];
                    current = first.GetProperty("name").GetString();
                    if (first.TryGetProperty("percentage", out var p) && p.ValueKind == JsonValueKind.Number) percent = p.GetInt32();
                    foreach (var item in list.EnumerateArray())
                    {
                        var pct = item.TryGetProperty("percentage", out var v) && v.ValueKind == JsonValueKind.Number ? $"%{v.GetInt32()}" : "";
                        active.Add($"{pct,5}  {item.GetProperty("name").GetString()}");
                    }
                }
                long Num(string key) => s.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? (long)v.GetDouble() : 0;
                var eta = s.TryGetProperty("eta", out var e) && e.ValueKind == JsonValueKind.Number ? (long)e.GetDouble() : -1;
                stats?.Invoke(new TransferStats(Num("bytes"), Num("totalBytes"), Num("speed"), eta, (int)Num("transfers"), (int)Num("totalTransfers"), current, percent, active.ToArray()));
            }
            else if (root.TryGetProperty("level", out var level) && level.GetString() is "error" or "critical")
            {
                log?.Invoke("  rclone: " + root.GetProperty("msg").GetString());
            }
            else if (root.TryGetProperty("object", out var obj) && obj.ValueKind == JsonValueKind.String && root.TryGetProperty("msg", out var message))
            {
                // Dosya dosya anlık aktarım kaydı
                var text = message.GetString() ?? "";
                var label = text.StartsWith("Copied (new)", StringComparison.Ordinal) ? "Yeni"
                    : text.StartsWith("Copied (", StringComparison.Ordinal) || text.StartsWith("Updated", StringComparison.Ordinal) ? "Güncellendi"
                    : text.StartsWith("Moved", StringComparison.Ordinal) ? "Taşındı"
                    : text.StartsWith("Deleted", StringComparison.Ordinal) ? "Silindi"
                    : null;
                if (label != null) log?.Invoke($"  {label}: {obj.GetString()}");
            }
        }
        catch (JsonException) { }
    };

    private static void AddTransferFlags(List<string> args) =>
        args.AddRange(new[] { "--retries", "3", "--low-level-retries", "6", "--drive-chunk-size", "64M", "--use-json-log", "--stats", "1s", "--log-level", "INFO" });

    /// <summary>Dosyayı yükler ve bulut kopyasını özet değeriyle karşılaştırır.</summary>
    public static async Task UploadAsync(string localFile, string target, string folder, Action<string>? log, Action<TransferStats>? stats, CancellationToken ct)
    {
        var name = Path.GetFileName(localFile);
        var copy = Args("copyto", localFile, Join(target, folder, name));
        AddTransferFlags(copy);
        var (code, text) = await Proc.RunAsync(Exe, copy, StatsReader(stats, log), ct);
        if (code != 0) throw new IOException($"{target}: yükleme başarısız (kod {code}): {Tail(text)}");

        var dir = Path.GetDirectoryName(Path.GetFullPath(localFile))!;
        var check = Args("check", dir, Join(target, folder), "--one-way", "--include", "/" + name);
        var (checkCode, checkText) = await Proc.RunAsync(Exe, check, null, ct);
        if (checkCode != 0) throw new IOException($"{target}: yüklenen dosya doğrulanamadı: {Tail(checkText)}");
    }

    /// <summary>Sıkıştırmadan birebir gönderir. Silme açıksa hedeften kalkanlar önce _silinenler altına alınır.</summary>
    public static async Task MirrorAsync(string source, string target, string folder, bool delete, string stamp, IEnumerable<string> excludes,
        Action<string>? log, Action<TransferStats>? stats, CancellationToken ct, string? asName = null)
    {
        var name = asName ?? Path.GetFileName(Path.TrimEndingDirectorySeparator(source));
        if (name.Length == 0) name = source.Replace(":", "").Trim('\\', '/');

        List<string> args;
        if (File.Exists(source)) args = Args("copyto", source, Join(target, folder, name));
        else if (delete)
        {
            args = Args("sync", source, Join(target, folder, name));
            args.Add("--backup-dir");
            args.Add(Join(target, folder, $"_silinenler/{stamp}"));
        }
        else args = Args("copy", source, Join(target, folder, name));

        foreach (var x in excludes) { args.Add("--exclude"); args.Add(x); }
        AddTransferFlags(args);
        var (code, text) = await Proc.RunAsync(Exe, args, StatsReader(stats, log), ct);
        if (code != 0) throw new IOException($"{target}: birebir gönderim başarısız (kod {code}): {Tail(text)}");
    }

    /// <summary>Hazır komutu aktarım ayarları ve canlı istatistikle çalıştırır.</summary>
    public static async Task RunTransferAsync(List<string> args, Action<string>? log, Action<TransferStats>? stats, CancellationToken ct)
    {
        AddTransferFlags(args);
        var (code, text) = await Proc.RunAsync(Exe, args, StatsReader(stats, log), ct);
        if (code != 0) throw new IOException($"Aktarım başarısız (kod {code}): {Tail(text)}");
    }

    /// <summary>Tek eşleşmeyi eşitler; yalnızca farklı dosyalar aktarılır.</summary>
    public static async Task SyncAsync(SyncPair pair, bool delete, string backupRoot, string stamp, IEnumerable<string> excludes,
        Action<string>? log, Action<TransferStats>? stats, CancellationToken ct)
    {
        var isFile = File.Exists(pair.Source);
        var dest = $"{pair.Target}:{pair.Folder}";
        var args = isFile ? Args("copyto", pair.Source, dest) : Args(delete ? "sync" : "copy", pair.Source, dest);
        if (delete && !isFile)
        {
            args.Add("--backup-dir");
            args.Add(Join(pair.Target, backupRoot, $"_silinenler/{stamp}"));
        }
        foreach (var x in excludes) { args.Add("--exclude"); args.Add(x); }
        AddTransferFlags(args);
        var (code, text) = await Proc.RunAsync(Exe, args, StatsReader(stats, log), ct);
        if (code != 0) throw new IOException($"{pair.Target}: eşitleme başarısız (kod {code}): {Tail(text)}");
    }

    /// <summary>İki tarafı karşılaştırır. Fark bulununca rclone sıfırdan farklı kodla döner; bu hata değildir.</summary>
    public static async Task<(int Code, string Output, string Error)> CheckAsync(SyncPair pair, IEnumerable<string> excludes, CancellationToken ct)
    {
        var args = Args("check", pair.Source, $"{pair.Target}:{pair.Folder}", "--combined", "-");
        foreach (var x in excludes) { args.Add("--exclude"); args.Add(x); }
        return await Proc.CaptureAsync(Exe, args, ct);
    }

    /// <summary>
    /// Her öğe ve format için son N yedeği bırakır. Yalnızca Backup360'ın ad kalıbına uyan dosyalar
    /// silinir; "yalnızca değişenler" açıkken değişmeyen klasörün tek yedeği de böylece korunur.
    /// </summary>
    public static async Task<int> ApplyRetentionAsync(string target, string folder, string extension, int keep, int keepDays, Action<string>? log, CancellationToken ct)
    {
        if (keep <= 0 && keepDays <= 0) return 0;
        var cutoff = keepDays > 0 ? DateTime.Now.AddDays(-keepDays) : DateTime.MinValue;
        bool Old(string stamp) => DateTime.ParseExact(stamp, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) < cutoff;
        var (code, text, err) = await Proc.CaptureAsync(Exe, Args("lsjson", Join(target, folder), "--files-only", "-R"), ct);
        if (code != 0) throw new IOException($"{target}: eski yedekler listelenemedi: {Tail(err)}");

        var pattern = new Regex($@"^(?:\d{{4}}-\d{{2}}-\d{{2}}/)?(?<item>.+)_(?<stamp>\d{{8}}-\d{{6}})\.{Regex.Escape(extension)}$", RegexOptions.CultureInvariant);
        using var doc = JsonDocument.Parse(text);
        var old = doc.RootElement.EnumerateArray()
            .Select(e => e.GetProperty("Path").GetString()!)
            .Select(p => (Path: p, Match: pattern.Match(p)))
            .Where(x => x.Match.Success)
            .GroupBy(x => x.Match.Groups["item"].Value, StringComparer.OrdinalIgnoreCase)
            // En yeni kopya hiçbir kuralla silinmez: değişmeyen klasörün tek yedeği korunur.
            .SelectMany(g => g.OrderByDescending(x => x.Match.Groups["stamp"].Value, StringComparer.Ordinal)
                .Where((x, i) => i > 0 && ((keep > 0 && i >= keep) || Old(x.Match.Groups["stamp"].Value))))
            .Select(x => x.Path)
            .ToList();

        foreach (var path in old)
        {
            await Must(Args("deletefile", Join(target, folder, path)), $"{path} silinemedi", ct);
            log?.Invoke($"  eski yedek silindi: {target}:{path}");
        }
        if (old.Any(p => p.Contains('/')))
            await Proc.RunAsync(Exe, Args("rmdirs", Join(target, folder), "--leave-root"), null, ct);
        return old.Count;
    }

    /// <summary>Sıkıştırılmamış tarih klasörlerine saklama kuralını uygular; yalnızca yyyy-MM-dd/öğe yapısına dokunur.</summary>
    public static async Task<int> ApplyFolderRetentionAsync(string target, string folder, int keep, int keepDays, Action<string>? log, CancellationToken ct, int depth = 2)
    {
        if (keep <= 0 && keepDays <= 0) return 0;
        var (code, text, err) = await Proc.CaptureAsync(Exe, Args("lsjson", Join(target, folder), "--dirs-only", "-R", "--max-depth", depth.ToString(CultureInfo.InvariantCulture)), ct);
        if (code != 0) throw new IOException($"{target}: eski klasörler listelenemedi: {Tail(err)}");
        var cutoff = keepDays > 0 ? DateTime.Today.AddDays(-keepDays) : DateTime.MinValue;
        var pattern = new Regex(@"^(?<date>\d{4}-\d{2}-\d{2})/(?<item>[^/]+" + string.Concat(Enumerable.Repeat("/[^/]+", depth - 2)) + ")$", RegexOptions.CultureInvariant);
        using var doc = JsonDocument.Parse(text);
        var old = doc.RootElement.EnumerateArray()
            .Select(e => pattern.Match(e.GetProperty("Path").GetString()!))
            .Where(m => m.Success)
            .GroupBy(m => m.Groups["item"].Value, StringComparer.OrdinalIgnoreCase)
            .SelectMany(g => g.OrderByDescending(m => m.Groups["date"].Value, StringComparer.Ordinal)
                .Where((m, i) => i > 0 && ((keep > 0 && i >= keep) || DateTime.ParseExact(m.Groups["date"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture) < cutoff)))
            .Select(m => m.Value)
            .ToList();
        foreach (var path in old)
        {
            await Must(Args("purge", Join(target, folder, path)), $"{path} silinemedi", ct);
            log?.Invoke($"  eski klasör silindi: {target}:{path}");
        }
        if (old.Count > 0) await Proc.RunAsync(Exe, Args("rmdirs", Join(target, folder), "--leave-root"), null, ct);
        return old.Count;
    }

    /// <summary>Gerçek bir küçük dosya yükleyip silerek bağlantıyı ve yükleme hızını ölçer.</summary>
    public static async Task<ConnectionResult> TestAsync(string target, CancellationToken ct, Action<string>? step = null)
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "Backup360", Guid.NewGuid().ToString("N"))).FullName;
        const string remoteFolder = ".backup360-baglanti-testi";   // Tek seviye: test sonunda hedefte iz kalmaz
        var file = Path.Combine(dir, $"test-{Guid.NewGuid():N}.bin");
        try
        {
            const int size = 4 * 1024 * 1024;
            step?.Invoke("Hedefe bağlanılıyor");
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var (listCode, listText) = await Proc.RunAsync(Exe, Args("lsf", target + ":", "--max-depth", "1", "--retries", "1", "--low-level-retries", "2"), null, ct);
            var latency = clock.ElapsedMilliseconds;
            if (listCode != 0) return new ConnectionResult(false, Tail(listText), latency, 0);

            step?.Invoke("Yükleme hızı ölçülüyor (4 MB)");
            await File.WriteAllBytesAsync(file, System.Security.Cryptography.RandomNumberGenerator.GetBytes(size), ct);
            clock.Restart();
            var (code, text) = await Proc.RunAsync(Exe, Args("copyto", file, Join(target, remoteFolder, Path.GetFileName(file)), "--retries", "1", "--low-level-retries", "2"), null, ct);
            var seconds = Math.Max(0.001, clock.Elapsed.TotalSeconds);
            if (code != 0) return new ConnectionResult(false, Tail(text), latency, 0);
            step?.Invoke("Test dosyası siliniyor");
            await Proc.RunAsync(Exe, Args("purge", Join(target, remoteFolder)), null, ct);
            var speed = (double)size / seconds;
            Estimator.StoreSpeed(target, speed);
            return new ConnectionResult(true, "Bağlantı başarılı", latency, speed);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    public static string SafeName(string name)
    {
        var cleaned = Regex.Replace(name.Trim(), @"[^\p{L}\p{Nd} _.-]", "_").Trim('.', ' ');
        return cleaned.Length == 0 ? "yedek" : cleaned;
    }

    public static string Stamp() => DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
}
