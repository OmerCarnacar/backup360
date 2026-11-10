using System.IO.Enumeration;
using System.Security.Cryptography;

namespace Backup360.Core;

public static class Validation
{
    public static void Check(Job j)
    {
        if (string.IsNullOrWhiteSpace(j.Name)) throw new ArgumentException("İş adı boş olamaz.");
        if (j.Mode == "sync")
        {
            if (j.Sources.Count == 0 && j.SyncFromJob == 0) throw new ArgumentException("Eşitlenecek kaynağı seçin.");
            if (j.Targets.Count == 0) throw new ArgumentException("En az bir hedef seçin.");
            return;
        }
        if (j.Mode == "database")
        {
            if (j.DbEngine != "sqlite" && string.IsNullOrWhiteSpace(j.DbServer)) throw new ArgumentException("Veritabanı sunucusunu yazın.");
            if (j.DbEngine is "mysql" or "postgres" or "firebird" && string.IsNullOrWhiteSpace(j.DbUser)) throw new ArgumentException("Veritabanı kullanıcı adını yazın.");
            if ((j.DbEngine is "sqlite" or "firebird" || !j.DbAll) && j.DbNames.Count == 0) throw new ArgumentException("En az bir veritabanı seçin.");
            var stems = j.DbEngine is "sqlite" or "firebird" ? j.DbNames.Select(Path.GetFileNameWithoutExtension) : j.DbNames;
            if (stems.GroupBy(s => Cloud.SafeName(s ?? ""), StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } clash)
                throw new ArgumentException($"Aynı ada sahip iki veritabanı seçilemez: {clash.Key}");
        }
        else if (j.Sources.Count == 0) throw new ArgumentException("En az bir dosya veya klasör seçin.");
        if (j.Targets.Count == 0) throw new ArgumentException("Yerel klasöre yedeklemeyi veya en az bir hedefe göndermeyi seçin.");
        // Yedek klasörü kaynağın içindeyse her çalışmada kendi yedeklerini de yedekler.
        foreach (var local in j.Targets.Where(Cloud.IsLocalPath))
            if (j.Mode != "database" && j.Sources.FirstOrDefault(s => Directory.Exists(s) && WorkFolder.IsInside(local, s)) is { } inside)
                throw new ArgumentException($"Yerel yedek klasörü yedeklenen klasörün içinde olamaz: {inside}");
        if (j.Mode == "mirror" || (j.Mode == "dated" && j.DatedOutput == "folder")) return;   // Sıkıştırmasız gönderimde arşiv ayarları kullanılmaz.
        if (j.Formats.Count == 0) throw new ArgumentException("En az bir sıkıştırma formatı seçin.");

        var duplicate = j.Sources.GroupBy(s => Path.GetFileName(Path.TrimEndingDirectorySeparator(s)), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Key.Length > 0 && g.Count() > 1);
        if (duplicate != null)
            throw new ArgumentException($"Aynı ada sahip iki kaynak aynı arşive konamaz: {duplicate.Key}");

        if (j.PasswordEnc != null)
        {
            var unsupported = j.Formats.Where(f => !Formats.Get(f).Password).ToList();
            if (unsupported.Count > 0)
                throw new ArgumentException($"Parola yalnızca 7z, zip ve rar için geçerlidir. Şunları kaldırın veya parolayı silin: {string.Join(", ", unsupported)}");
        }
    }

    public static void Check(MoveRule r)
    {
        if (string.IsNullOrWhiteSpace(r.Name)) throw new ArgumentException("Kural adı boş olamaz.");
        if (string.IsNullOrWhiteSpace(r.Source) || string.IsNullOrWhiteSpace(r.Target)) throw new ArgumentException("Kaynak ve hedef klasörü seçin.");
        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(r.Source)) + Path.DirectorySeparatorChar;
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(r.Target)) + Path.DirectorySeparatorChar;
        if (target.StartsWith(source, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Hedef klasör, kaynak klasörle aynı veya onun içinde olamaz.");
    }
}

public static class JobModes
{
    /// <summary>Ekrandaki seçimi kayıt biçimine çevirir: sıkıştırma yalnızca istenirse uygulanır.</summary>
    public static (string Mode, string DatedOutput) Map(bool dated, bool database, bool compress) =>
        database ? ("database", "archive")
        : dated ? ("dated", compress ? "archive" : "folder")
        : (compress ? "archive" : "mirror", "archive");
}

public static class MoveEngine
{
    public sealed record Outcome(string Status, int Moved, int Failed, long Bytes);

    private static readonly string[] IgnoredPatterns = { "*.tmp", "~$*", "*.crdownload", "*.part", "*.partial", "*.b360tmp", "desktop.ini", "thumbs.db" };

    public static string RelativeKey(string root, string file) => Path.GetRelativePath(root, file);

    private static IEnumerable<FileInfo> Enumerate(MoveRule r)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = r.Recursive,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint,
        };
        return new DirectoryInfo(r.Source).EnumerateFiles("*", options);
    }

    /// <summary>"Yalnızca yeni dosyalar" için kuralın başladığı andaki klasör içeriğini kaydeder.</summary>
    public static void CaptureBaseline(MoveRule r)
    {
        using var c = Db.Open();
        using var tx = c.BeginTransaction();
        using (var del = Db.Command(c, "DELETE FROM rule_baseline WHERE rule_id=$r", ("$r", r.Id))) { del.Transaction = tx; del.ExecuteNonQuery(); }
        if (r.OnlyNew && Directory.Exists(r.Source))
        {
            using var ins = Db.Command(c, "INSERT OR REPLACE INTO rule_baseline(rule_id,rel_path,size,mtime) VALUES($r,$p,$s,$m)",
                ("$r", r.Id), ("$p", ""), ("$s", 0L), ("$m", 0L));
            ins.Transaction = tx;
            foreach (var f in Enumerate(r))
            {
                ins.Parameters["$p"].Value = RelativeKey(r.Source, f.FullName);
                ins.Parameters["$s"].Value = f.Length;
                ins.Parameters["$m"].Value = f.LastWriteTimeUtc.Ticks;
                ins.ExecuteNonQuery();
            }
        }
        tx.Commit();
    }

    private static Dictionary<string, (long Size, long Mtime)> LoadBaseline(long ruleId)
    {
        var map = new Dictionary<string, (long, long)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (p, s, m) in Db.Query("SELECT rel_path,size,mtime FROM rule_baseline WHERE rule_id=$r",
                     x => (x.GetString(0), x.GetInt64(1), x.GetInt64(2)), ("$r", ruleId)))
            map[p] = (s, m);
        return map;
    }

    private static bool Matches(string name, string[] patterns, IEnumerable<string> list) =>
        list.Any(p => FileSystemName.MatchesSimpleExpression(p, name)) is var ignored && !ignored
        && patterns.Any(p => FileSystemName.MatchesSimpleExpression(p, name));

    private static bool IsLocked(FileInfo f)
    {
        try
        {
            using var s = f.Open(FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    private static string? ResolveTarget(string path, string conflict)
    {
        if (!File.Exists(path) || conflict == "overwrite") return path;
        if (conflict == "skip") return null;
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private static byte[] Hash(string path)
    {
        using var s = File.OpenRead(path);
        return SHA256.HashData(s);
    }

    /// <summary>Farklı sürücüde önce kopyalar, özeti doğrular; kaynak ancak bundan sonra silinir.</summary>
    private static void MoveFile(string source, string target, bool overwrite)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (string.Equals(Path.GetPathRoot(source), Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase))
        {
            File.Move(source, target, overwrite);
            return;
        }
        var temp = target + ".b360tmp";
        try
        {
            File.Copy(source, temp, true);
            if (!Hash(source).SequenceEqual(Hash(temp))) throw new IOException("Kopya doğrulanamadı.");
            File.Move(temp, target, overwrite);
        }
        catch
        {
            try { File.Delete(temp); } catch (IOException) { }
            throw;
        }
        File.Delete(source);
    }

    public static async Task<Outcome> RunAsync(long ruleId, Action<string>? ui, CancellationToken ct)
    {
        var rule = Db.GetRule(ruleId) ?? throw new InvalidOperationException($"Kural bulunamadı: {ruleId}");
        return await Task.Run(() => Run(rule, ui, ct), ct);
    }

    private static Outcome Run(MoveRule r, Action<string>? ui, CancellationToken ct)
    {
        using var gate = RunGate.TryEnter($"rule-{r.Id}");
        if (gate == null) return new Outcome("skipped", 0, 0, 0);
        try
        {
            Validation.Check(r);
            if (!Directory.Exists(r.Source)) throw new DirectoryNotFoundException("Kaynak klasör bulunamadı: " + r.Source);
            Directory.CreateDirectory(r.Target);

            var baseline = r.OnlyNew ? LoadBaseline(r.Id) : new();
            var patterns = r.Pattern.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (patterns.Length == 0) patterns = new[] { "*" };

            RunLog? log = null;
            int moved = 0, failed = 0, waiting = 0;
            long bytes = 0;
            var errors = new List<string>();
            foreach (var file in Enumerate(r).ToList())
            {
                ct.ThrowIfCancellationRequested();
                if (!Matches(file.Name, patterns, IgnoredPatterns)) continue;
                var rel = RelativeKey(r.Source, file.FullName);
                if (baseline.TryGetValue(rel, out var known) && known.Size == file.Length && known.Mtime == file.LastWriteTimeUtc.Ticks) continue;

                // Hâlâ yazılan veya başka program tarafından açık dosya sonraki turda alınır.
                if ((DateTime.UtcNow - file.LastWriteTimeUtc).TotalSeconds < r.StableSeconds || IsLocked(file)) { waiting++; continue; }

                var target = ResolveTarget(Path.Combine(r.Target, rel), r.Conflict);
                if (target == null) { waiting++; continue; }

                log ??= new RunLog($"Taşıma: {r.Name}", ui, "rule", r.Id, r.Name, "move");
                log.Update(l => { l.Stage = "move"; l.Item = rel; l.Files = moved; });
                try
                {
                    var length = file.Length;
                    MoveFile(file.FullName, target, r.Conflict == "overwrite");
                    moved++;
                    bytes += length;
                    log.Update(l => { l.Done = bytes; l.Files = moved; });
                    log.Write($"  {rel} -> {target}");
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    failed++;
                    errors.Add($"{rel}: {e.Message}");
                    log.Write($"  HATA {rel}: {e.Message}");
                }
            }

            log?.Dispose();
            var status = failed > 0 ? "failed" : "success";
            // Boş turlar geçmişi doldurmasın: yalnızca iş yapıldıysa kayıt düşülür.
            if (moved > 0 || failed > 0)
            {
                var id = Db.StartRun("rule", r.Id, r.Name);
                var summary = $"{moved} dosya taşındı" + (failed > 0 ? $", {failed} hata: {string.Join(" ; ", errors.Take(5))}" : "");
                Db.FinishRun(id, status, summary, bytes);
                if (failed > 0) Notifier.ReportAsync("Klasör taşıma", r.Name, status, summary, DateTime.Now, bytes, null).GetAwaiter().GetResult();
            }
            else ui?.Invoke($"{DateTime.Now:HH:mm:ss}  Yeni dosya yok" + (waiting > 0 ? $" ({waiting} dosya bekliyor)." : "."));
            return new Outcome(status, moved, failed, bytes);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            var id = Db.StartRun("rule", r.Id, r.Name);
            Db.FinishRun(id, "failed", e.Message, 0);
            new RunLog($"Taşıma: {r.Name}", ui).Write("HATA: " + e.Message);
            Notifier.ReportAsync("Klasör taşıma", r.Name, "failed", e.Message, DateTime.Now, 0, null).GetAwaiter().GetResult();
            return new Outcome("failed", 0, 1, 0);
        }
    }
}

/// <summary>Zamanlama Windows Görev Zamanlayıcı'da tutulur: uygulama kapalıyken de çalışır.</summary>
public static class TaskPlanner
{
    public static string JobTask(long id) => $"Backup360-Yedek-{id}";
    public static string RuleTask(long id) => $"Backup360-Tasima-{id}";

    private static string Quote(string s) => "'" + s.Replace("'", "''") + "'";

    private static async Task PowerShellAsync(string script)
    {
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes("$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue';" + script));
        var (code, text) = await Proc.RunAsync("powershell.exe",
            new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded }, null, CancellationToken.None);
        if (code != 0) throw new InvalidOperationException("Zamanlama kaydedilemedi: " + text);
    }

    private static string Trigger(Schedule s) => s.Type switch
    {
        "minutes" => $"New-ScheduledTaskTrigger -Once -At (Get-Date) -RepetitionInterval (New-TimeSpan -Minutes {Math.Max(1, s.Interval)})",
        "hourly" => $"New-ScheduledTaskTrigger -Once -At (Get-Date) -RepetitionInterval (New-TimeSpan -Hours {Math.Max(1, s.Interval)})",
        "daily" => $"New-ScheduledTaskTrigger -Daily -At {Quote(s.Time)}",
        "weekly" => $"New-ScheduledTaskTrigger -Weekly -DaysOfWeek {s.Days} -At {Quote(s.Time)}",
        _ => throw new ArgumentException("Geçersiz zamanlama."),
    };

    public static async Task ApplyAsync(string taskName, string arguments, Schedule s)
    {
        if (s.Type == "none")
        {
            await RemoveAsync(taskName);
            return;
        }
        await PowerShellAsync($"""
            $a = New-ScheduledTaskAction -Execute {Quote(AppPaths.ExePath)} -Argument {Quote(arguments)} -WorkingDirectory {Quote(AppPaths.BaseDir.TrimEnd('\\'))};
            $t = {Trigger(s)};
            $o = New-ScheduledTaskSettingsSet -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Seconds 0);
            $p = New-ScheduledTaskPrincipal -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType Interactive -RunLevel Limited;
            Register-ScheduledTask -TaskName {Quote(taskName)} -Action $a -Trigger $t -Settings $o -Principal $p -Force | Out-Null
            """);
    }

    public static Task RemoveAsync(string taskName) =>
        PowerShellAsync($"Get-ScheduledTask -TaskName {Quote(taskName)} -ErrorAction SilentlyContinue | Unregister-ScheduledTask -Confirm:$false");

    public static Task<bool> ExistsAsync(string taskName) => Task.Run(async () =>
    {
        var (_, text) = await Proc.RunAsync("schtasks.exe", new[] { "/Query", "/TN", taskName, "/FO", "LIST" }, null, CancellationToken.None);
        return text.Contains(taskName, StringComparison.OrdinalIgnoreCase);
    });

    /// <summary>Windows'un kendi kaydından sonraki ve son çalışma zamanlarını okur.</summary>
    public static async Task<Dictionary<string, (DateTime? Next, DateTime? Last)>> ReadTimesAsync()
    {
        var script = "$ErrorActionPreference='SilentlyContinue';$ProgressPreference='SilentlyContinue';[Console]::OutputEncoding=[Text.Encoding]::UTF8;" +
                     "@(Get-ScheduledTask -TaskName 'Backup360-*' | Get-ScheduledTaskInfo | ForEach-Object { [pscustomobject]@{N=$_.TaskName;A=if($_.NextRunTime){$_.NextRunTime.ToString('o')}else{''};L=if($_.LastRunTime -and $_.LastRunTime.Year -gt 2000){$_.LastRunTime.ToString('o')}else{''}} }) | ConvertTo-Json -Compress";
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        var (_, text) = await Proc.RunAsync("powershell.exe", new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded }, null, CancellationToken.None);
        var map = new Dictionary<string, (DateTime?, DateTime?)>(StringComparer.OrdinalIgnoreCase);
        // Çıktıya PowerShell'in stderr satırları karışabilir; yalnızca JSON satırı alınır.
        text = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault(l => l.StartsWith('[') || l.StartsWith('{')) ?? "";
        if (text.Length == 0) return map;
        using var doc = System.Text.Json.JsonDocument.Parse(text.StartsWith('[') ? text : $"[{text}]");
        static DateTime? Parse(string? s) => DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d : null;
        foreach (var e in doc.RootElement.EnumerateArray())
            map[e.GetProperty("N").GetString()!] = (Parse(e.GetProperty("A").GetString()), Parse(e.GetProperty("L").GetString()));
        return map;
    }

    public const string AuditTask = "Backup360-Saglik";

    public static Schedule AuditSchedule() =>
        Db.GetSetting("audit_schedule") is { } json ? System.Text.Json.JsonSerializer.Deserialize<Schedule>(json) ?? new() : new Schedule { Type = "weekly", Days = "Sunday", Time = "03:00" };

    private static Task ApplyAuditAsync() =>
        ApplyAsync(AuditTask, Db.GetSetting("audit_deep") == "1" ? "--run-audit deep" : "--run-audit quick", AuditSchedule());

    public static async Task SaveAuditAsync(Schedule schedule, bool deep)
    {
        Db.SetSetting("audit_schedule", System.Text.Json.JsonSerializer.Serialize(schedule));
        Db.SetSetting("audit_deep", deep ? "1" : "0");
        await ApplyAuditAsync();
    }

    public const string GuardTask = "Backup360-Koruma";

    public static int GuardMinutes => int.TryParse(Db.GetSetting("guard_minutes"), out var m) && m > 0 ? m : 15;

    public static async Task SaveGuardAsync(bool enabled, int minutes)
    {
        Db.SetSetting("guard_enabled", enabled ? "1" : "0");
        Db.SetSetting("guard_minutes", Math.Max(1, minutes).ToString());
        await ApplyAsync(GuardTask, "--run-guard", enabled ? new Schedule { Type = "minutes", Interval = Math.Max(1, minutes) } : new Schedule());
    }

    public static async Task EnsureGuardAsync()
    {
        if (Db.GetSetting("guard_minutes") == null) await SaveGuardAsync(true, 15);
    }

    /// <summary>İlk açılışta haftalık hızlı denetimi kurar.</summary>
    public static async Task EnsureAuditAsync()
    {
        if (Db.GetSetting("audit_schedule") == null) await SaveAuditAsync(AuditSchedule(), false);
    }

    public static Task ApplyAsync(Job j) => ApplyAsync(JobTask(j.Id), $"--run-job {j.Id}", j.Schedule);
    public static Task ApplyAsync(MoveRule r) => ApplyAsync(RuleTask(r.Id), $"--run-rule {r.Id}", r.Schedule);

    /// <summary>Uygulama klasörü taşındıysa görevleri yeni exe yoluna göre yeniden kaydeder.</summary>
    public static async Task ResyncIfMovedAsync()
    {
        if (string.Equals(Db.GetSetting("exe_path"), AppPaths.ExePath, StringComparison.OrdinalIgnoreCase)) return;
        foreach (var j in Db.Jobs()) await ApplyAsync(j);
        foreach (var r in Db.Rules()) await ApplyAsync(r);
        if (Db.GetSetting("audit_schedule") != null) await ApplyAuditAsync();
        if (Db.GetSetting("guard_minutes") != null) await SaveGuardAsync(RansomGuard.Enabled, GuardMinutes);
        Db.SetSetting("exe_path", AppPaths.ExePath);
    }
}
