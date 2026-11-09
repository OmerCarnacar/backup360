namespace Backup360.Core;

public sealed class RansomwareException(string message) : Exception(message);

public sealed record GuardResult(bool Alarm, int Scanned, List<string> Evidence, string Summary);

/// <summary>
/// Kaynaklarda toplu şifrelenme belirtisi arar. Şüphe varsa yedek ve eşitleme durur;
/// böylece şifrelenmiş dosyalar sağlam yedeklerin yerine geçmez.
/// </summary>
public static class RansomGuard
{
    private const int ContentSample = 200;
    private const int Threshold = 5;
    private const double EntropyLimit = 7.5;

    private static readonly HashSet<string> BadExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".locked", ".encrypted", ".enc2", ".crypted", ".cry", ".crypt", ".crypz", ".cryptolocker", ".wncry", ".wnry", ".wcry", ".wannacry",
        ".lockbit", ".lockbit3", ".abcd", ".ryk", ".ryuk", ".conti", ".akira", ".phobos", ".makop", ".dharma", ".wallet", ".arena", ".cerber", ".locky",
        ".zepto", ".odin", ".thor", ".aesir", ".maze", ".sodinokibi", ".revil", ".blackcat", ".hive", ".akiranew", ".safepay", ".3am", ".flocked", "._locked", ".stop", ".djvu", ".nochance", ".pays", ".kraken",
    };

    private static readonly string[] Notes =
    {
        "how_to_decrypt", "how-to-decrypt", "howtodecrypt", "how_to_recover", "how-to-recover", "readme_for_decrypt", "read_me_for_decrypt", "decrypt_instructions", "decrypt-instructions",
        "decrypt_your_files", "recover-files", "recover_files", "restore_files", "restore-my-files", "your_files_are_encrypted", "files_encrypted", "ransom_note", "!!!readme!!!",
    };

    private static readonly Dictionary<string, byte[]> Magic = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "%PDF"u8.ToArray(), [".docx"] = "PK"u8.ToArray(), [".xlsx"] = "PK"u8.ToArray(), [".pptx"] = "PK"u8.ToArray(), [".zip"] = "PK"u8.ToArray(),
        [".doc"] = new byte[] { 0xD0, 0xCF, 0x11, 0xE0 }, [".xls"] = new byte[] { 0xD0, 0xCF, 0x11, 0xE0 }, [".ppt"] = new byte[] { 0xD0, 0xCF, 0x11, 0xE0 },
        [".jpg"] = new byte[] { 0xFF, 0xD8 }, [".jpeg"] = new byte[] { 0xFF, 0xD8 }, [".png"] = new byte[] { 0x89, 0x50, 0x4E, 0x47 }, [".gif"] = "GIF8"u8.ToArray(),
    };

    private static readonly HashSet<string> Text = new(StringComparer.OrdinalIgnoreCase) { ".txt", ".csv", ".sql", ".xml", ".json", ".html", ".log", ".ini" };

    public static bool Enabled => Db.GetSetting("guard_enabled") != "0";

    public static double Entropy(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return 0;
        Span<int> counts = stackalloc int[256];
        foreach (var b in data) counts[b]++;
        double entropy = 0;
        foreach (var c in counts)
        {
            if (c == 0) continue;
            var p = (double)c / data.Length;
            entropy -= p * Math.Log2(p);
        }
        return entropy;
    }

    private static bool LooksEncrypted(FileInfo f)
    {
        try
        {
            using var s = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[65536];
            var read = s.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            if (read < 4096) return false;
            if (Text.Contains(f.Extension) && s.Length > buffer.Length * 2)
            {
                // Kısmi şifreleme dosyanın yalnızca bazı bölümlerini bozar: metin dosyasının ortası ve sonu da okunur.
                foreach (var offset in new[] { s.Length / 2, s.Length - buffer.Length })
                {
                    s.Seek(offset, SeekOrigin.Begin);
                    var part = s.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
                    if (part >= 4096 && Entropy(buffer.AsSpan(0, part)) >= EntropyLimit) return true;
                }
                s.Seek(0, SeekOrigin.Begin);
                read = s.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            }
            if (Entropy(buffer.AsSpan(0, read)) < EntropyLimit) return false;
            return !Magic.TryGetValue(f.Extension, out var header) || !buffer.AsSpan(0, read).StartsWith(header);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    private static IEnumerable<FileInfo> Walk(IEnumerable<string> roots)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint };
        foreach (var root in roots)
        {
            if (File.Exists(root)) yield return new FileInfo(root);
            else if (Directory.Exists(root))
                foreach (var f in new DirectoryInfo(root).EnumerateFiles("*", options)) yield return f;
        }
    }

    public static GuardResult Scan(IEnumerable<string> roots, DateTime since, CancellationToken ct)
    {
        var evidence = new List<string>();
        int notes = 0, extensions = 0, scanned = 0;
        var candidates = new List<FileInfo>();
        foreach (var f in Walk(roots))
        {
            ct.ThrowIfCancellationRequested();
            scanned++;
            var stem = Path.GetFileNameWithoutExtension(f.Name);
            if (Notes.Any(n => stem.Contains(n, StringComparison.OrdinalIgnoreCase))) { notes++; evidence.Add($"Fidye notu: {f.FullName}"); }
            else if (BadExtensions.Contains(f.Extension)) { extensions++; evidence.Add($"Şüpheli uzantı: {f.FullName}"); }
            else if (f.LastWriteTime >= since && (Magic.ContainsKey(f.Extension) || Text.Contains(f.Extension))) candidates.Add(f);
        }

        var sample = candidates.OrderByDescending(f => f.LastWriteTimeUtc).Take(ContentSample).ToList();
        var encrypted = 0;
        foreach (var f in sample)
        {
            ct.ThrowIfCancellationRequested();
            if (!LooksEncrypted(f)) continue;
            encrypted++;
            evidence.Add($"Şifrelenmiş görünüyor: {f.FullName}");
        }

        var ratioHit = encrypted >= Threshold && encrypted >= sample.Count * 0.3;
        var alarm = notes > 0 || extensions + encrypted >= Threshold && (extensions >= Threshold || ratioHit || extensions > 0);
        alarm |= ratioHit;
        var summary = $"{scanned} dosya tarandı: {notes} fidye notu, {extensions} şüpheli uzantı, {encrypted} şifrelenmiş görünen dosya";
        return new GuardResult(alarm, scanned, evidence, summary);
    }

    /// <summary>Eşitleme öncesi: hedefteki dosyaların çoğu birden değişmiş veya kaybolmuşsa saldırı belirtisidir.</summary>
    public static bool IsMassChange(DiffResult diff)
    {
        var existing = diff.Count(SyncPlanner.Same) + diff.Count(SyncPlanner.Changed) + diff.Count(SyncPlanner.Extra);
        var risky = diff.Count(SyncPlanner.Changed) + diff.Count(SyncPlanner.Extra);
        return existing >= 20 && risky * 2 >= existing;
    }

    public static IEnumerable<string> JobSources() =>
        Db.Jobs().Where(j => j.Mode != "database").SelectMany(j => j.Sources)
            .Concat(Db.Rules().Select(r => r.Source)).Distinct(StringComparer.OrdinalIgnoreCase);

    public static string Describe(GuardResult r) =>
        $"{r.Summary}. {string.Join(" ; ", r.Evidence.Take(5))}";

    /// <summary>Zamanlanmış denetim: aynı olay için tekrar tekrar uyarmaz.</summary>
    public static async Task<GuardResult> MonitorAsync(CancellationToken ct)
    {
        using var gate = RunGate.TryEnter("guard");
        if (gate == null) return new GuardResult(false, 0, new(), "Denetim zaten çalışıyor");
        var since = DateTime.TryParse(Db.GetSetting("guard_last"), out var last) ? last.AddHours(-1) : DateTime.Now.AddDays(-1);
        var started = DateTime.Now;
        var result = await Task.Run(() => Scan(JobSources(), since, ct), ct);
        Db.SetSetting("guard_last", started.ToString("o"));

        var key = result.Alarm ? string.Join("|", result.Evidence.Take(20)) : "";
        var isNew = result.Alarm && Db.GetSetting("guard_alert") != key;
        Db.SetSetting("guard_alert", key);
        if (isNew)
        {
            var detail = Describe(result);
            var run = Db.StartRun("guard", 0, "Fidye yazılımı koruması");
            Db.FinishRun(run, "ransomware", detail, 0);
            new RunLog("Fidye yazılımı koruması", null).Write(detail);
            await Notifier.ReportAsync("Fidye yazılımı koruması", "Kaynak klasörler", "ransomware", detail, started, 0, null);
        }
        return result with { Alarm = isNew };
    }
}
