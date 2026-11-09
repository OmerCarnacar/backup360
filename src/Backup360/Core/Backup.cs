using System.Globalization;

namespace Backup360.Core;

public static class BackupRunner
{
    public sealed record Outcome(string Status, string Detail, long Bytes);

    private sealed record Item(string Name, List<string> Paths, string? DateFolder, string? Fingerprint);

    public static async Task<Outcome> RunAsync(long jobId, Action<string>? ui, CancellationToken ct)
    {
        var job = Db.GetJob(jobId) ?? throw new InvalidOperationException($"İş bulunamadı: {jobId}");
        using var gate = RunGate.TryEnter($"job-{jobId}");
        if (gate == null)
        {
            ui?.Invoke("Bu iş zaten çalışıyor, ikinci kopya başlatılmadı.");
            return new Outcome("skipped", "Zaten çalışıyor", 0);
        }

        using var log = new RunLog($"Yedekleme: {job.Name}", ui, "job", job.Id, job.Name, job.Mode);
        var runId = Db.StartRun("job", job.Id, job.Name);
        var work = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "Backup360", Guid.NewGuid().ToString("N"))).FullName;
        var outcome = new Outcome("failed", "", 0);
        var started = DateTime.Now;
        try
        {
            outcome = job.Mode switch
            {
                "mirror" => await MirrorAsync(job, log, ct),
                "database" => await DatabaseAsync(job, work, log, ct),
                _ => await ArchiveAsync(job, work, log, ct),
            };
            return outcome;
        }
        catch (OperationCanceledException)
        {
            log.Write("İptal edildi.");
            outcome = new Outcome("canceled", "Kullanıcı iptal etti", 0);
            return outcome;
        }
        catch (Exception e)
        {
            log.Write("HATA: " + e.Message);
            outcome = new Outcome("failed", e.Message, 0);
            return outcome;
        }
        finally
        {
            Db.FinishRun(runId, outcome.Status, outcome.Detail, outcome.Bytes);
            try { Directory.Delete(work, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            await Notifier.ReportAsync("Yedekleme", job.Name, outcome.Status, outcome.Detail, started, outcome.Bytes, log.Write);
        }
    }

    private static async Task CheckTargetsAsync(Job job, CancellationToken ct)
    {
        var known = (await Cloud.ListTargetsAsync(ct)).Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = job.Targets.Where(t => !known.Contains(t)).ToList();
        if (missing.Count > 0) throw new InvalidOperationException("Hedef bağlı değil: " + string.Join(", ", missing));
    }

    private static void ApplyStats(RunLog log, TransferStats s) => log.Update(l =>
    {
        l.Done = s.Bytes; l.Total = s.Total; l.Speed = s.Speed; l.Eta = s.Eta;
        l.Files = s.Files; l.FilesTotal = s.FilesTotal;
        if (s.Current != null) l.Item = s.Current;
        l.Percent = s.Total > 0 ? (int)(s.Bytes * 100 / s.Total) : -1;
    });

    private static void Stage(RunLog log, string stage, string item) => log.Update(l =>
    {
        l.Stage = stage; l.Item = item; l.Percent = -1; l.Speed = 0; l.Eta = -1; l.Done = 0; l.Total = 0;
    });

    private static async Task<Outcome> MirrorAsync(Job job, RunLog log, CancellationToken ct)
    {
        Validation.Check(job);
        await CheckTargetsAsync(job, ct);
        var folder = $"{job.RemoteFolder.Trim('/', '\\')}/{Cloud.SafeName(job.Name)}";
        var stamp = Cloud.Stamp();
        var errors = new List<string>();
        var warnings = false;
        long total = 0;
        var step = 0;
        var steps = job.Sources.Count * job.Targets.Count;
        foreach (var source in job.Sources)
        {
            if (!File.Exists(source) && !Directory.Exists(source))
            {
                log.Write("  uyarı: kaynak bulunamadı: " + source);
                warnings = true;
                continue;
            }
            foreach (var target in job.Targets)
            {
                step++;
                var current = step;
                Stage(log, "upload", source);
                log.Update(l => { l.Step = current; l.Steps = steps; });
                log.Write($"Birebir gönderiliyor: {source} -> {target}");
                long sent = 0;
                try
                {
                    await Cloud.MirrorAsync(source, target, folder, job.MirrorDelete, stamp, job.Excludes, log.Write, s => { sent = s.Bytes; ApplyStats(log, s); }, ct);
                    total += sent;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception e)
                {
                    log.Write("HATA: " + e.Message);
                    errors.Add(e.Message);
                }
            }
        }
        var status = errors.Count > 0 ? "failed" : warnings ? "warning" : "success";
        log.Write($"Bitti: {status}");
        return new Outcome(status, errors.Count > 0 ? string.Join(" ; ", errors) : "Birebir gönderim tamamlandı", total);
    }

    private static (int Count, long Size, DateTime Last) Scan(string dir)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System };
        int count = 0; long size = 0; var last = DateTime.MinValue;
        foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", options))
        {
            count++;
            size += f.Length;
            if (f.LastWriteTime > last) last = f.LastWriteTime;
        }
        return (count, size, last);
    }

    /// <summary>Her alt klasör ayrı öğedir. Klasör köküne doğrudan konmuş dosyalar da atlanmaz, tek öğede toplanır.</summary>
    private static List<Item> BuildDatedItems(Job job, RunLog log)
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var items = new List<Item>();
        foreach (var source in job.Sources)
        {
            if (!Directory.Exists(source))
            {
                log.Write("  uyarı: klasör bulunamadı: " + source);
                continue;
            }
            var prefix = job.Sources.Count > 1 ? Cloud.SafeName(Path.GetFileName(Path.TrimEndingDirectorySeparator(source))) + "__" : "";
            var root = new DirectoryInfo(source);
            foreach (var dir in root.EnumerateDirectories().Where(d => !d.Attributes.HasFlag(FileAttributes.System)).OrderBy(d => d.Name))
            {
                var (count, size, last) = Scan(dir.FullName);
                if (count == 0) { log.Write($"  boş klasör atlandı: {dir.Name}"); continue; }
                Add(prefix + Cloud.SafeName(dir.Name), new List<string> { dir.FullName }, count, size, last);
            }
            var loose = root.EnumerateFiles().Where(f => !f.Attributes.HasFlag(FileAttributes.System)).ToList();
            if (loose.Count > 0)
                Add(prefix + "_KokDosyalar", loose.Select(f => f.FullName).ToList(), loose.Count, loose.Sum(f => f.Length), loose.Max(f => f.LastWriteTime));
        }
        return items;

        void Add(string name, List<string> paths, int count, long size, DateTime last)
        {
            if (job.DaysFilter > 0 && last < DateTime.Now.AddDays(-job.DaysFilter))
            {
                log.Write($"  son {job.DaysFilter} günde değişmedi, atlandı: {name}");
                return;
            }
            var fingerprint = $"{count}|{size}|{last.Ticks}";
            if (job.OnlyChanged && Db.GetFingerprint(job.Id, name) == fingerprint)
            {
                log.Write($"  değişmedi, atlandı: {name}");
                return;
            }
            var date = job.DateBy == "folder" ? last.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : today;
            items.Add(new Item(name, paths, date, fingerprint));
        }
    }

    private static async Task<Outcome> ArchiveAsync(Job job, string work, RunLog log, CancellationToken ct)
    {
        Validation.Check(job);
        await CheckTargetsAsync(job, ct);
        var password = Secrets.Unprotect(job.PasswordEnc);
        var baseFolder = $"{job.RemoteFolder.Trim('/', '\\')}/{Cloud.SafeName(job.Name)}";
        var stamp = Cloud.Stamp();
        var warnings = false;

        List<Item> items;
        if (job.Mode == "dated")
        {
            if (!job.Sources.Any(Directory.Exists)) throw new IOException("Kaynak klasörlerin hiçbiri bulunamadı.");
            warnings = !job.Sources.All(Directory.Exists);
            Stage(log, "scan", "Klasörler taranıyor");
            items = BuildDatedItems(job, log);
        }
        else
        {
            var present = job.Sources.Where(s => File.Exists(s) || Directory.Exists(s)).ToList();
            foreach (var missing in job.Sources.Except(present)) log.Write("  uyarı: kaynak bulunamadı: " + missing);
            if (present.Count == 0) throw new IOException("Kaynakların hiçbiri bulunamadı.");
            warnings = present.Count != job.Sources.Count;
            items = new List<Item> { new(Cloud.SafeName(job.Name), present, null, null) };
        }

        if (items.Count == 0)
        {
            log.Write("Gönderilecek yeni veya değişmiş klasör yok.");
            return new Outcome(warnings ? "warning" : "success", "Değişiklik yok", 0);
        }

        if (job.Mode == "dated" && job.DatedOutput == "folder")
            return await SendFoldersAsync(job, items, baseFolder, stamp, warnings, work, log, ct);

        var run = new ItemRun(job, work, baseFolder, stamp, password, log, items.Count * job.Formats.Count) { Warnings = warnings };
        foreach (var item in items) await ProcessItemAsync(run, item, ct);
        return Finish(run, $"{items.Count} öğe, {job.Formats.Count} format, {job.Targets.Count} hedef");
    }

    /// <summary>Alt klasörleri sıkıştırmadan tarih klasörlerine gönderir.</summary>
    private static async Task<Outcome> SendFoldersAsync(Job job, List<Item> items, string baseFolder, string stamp, bool warnings, string work, RunLog log, CancellationToken ct)
    {
        var run = new ItemRun(job, work, baseFolder, stamp, null, log, items.Count * job.Targets.Count) { Warnings = warnings };
        foreach (var item in items)
        {
            var dateFolder = $"{baseFolder}/{item.DateFolder}";
            var itemOk = true;
            foreach (var target in job.Targets)
            {
                run.Step++;
                var current = run.Step;
                Stage(log, "upload", item.Name);
                log.Update(l => { l.Step = current; l.Steps = run.Steps; });
                log.Write($"[{item.Name}] klasör olarak gönderiliyor: {target}:{dateFolder}");
                long sent = 0;
                try
                {
                    foreach (var path in item.Paths)
                    {
                        // Klasör öğe adıyla yazılır; kök dosyaları öğe klasörünün içine konur.
                        if (Directory.Exists(path)) await Cloud.MirrorAsync(path, target, dateFolder, false, stamp, job.Excludes, log.Write, s => { sent = s.Bytes; ApplyStats(log, s); }, ct, item.Name);
                        else await Cloud.MirrorAsync(path, target, $"{dateFolder}/{item.Name}", false, stamp, job.Excludes, log.Write, s => { sent = s.Bytes; ApplyStats(log, s); }, ct);
                    }
                    run.Total += sent;
                    Stage(log, "retention", item.Name);
                    await Cloud.ApplyFolderRetentionAsync(target, baseFolder, job.Keep, job.KeepDays, log.Write, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception e)
                {
                    log.Write($"[{item.Name}] HATA: {e.Message}");
                    run.Errors.Add($"{item.Name}: {e.Message}");
                    itemOk = false;
                }
            }
            if (itemOk && item.Fingerprint != null) Db.SetFingerprint(job.Id, item.Name, item.Fingerprint);
        }
        return Finish(run, $"{items.Count} klasör, {job.Targets.Count} hedef");
    }

    private sealed class ItemRun(Job job, string work, string baseFolder, string stamp, string? password, RunLog log, int steps)
    {
        public Job Job { get; } = job;
        public string Work { get; } = work;
        public string BaseFolder { get; } = baseFolder;
        public string Stamp { get; } = stamp;
        public string? Password { get; } = password;
        public RunLog Log { get; } = log;
        public int Steps { get; } = steps;
        public int Step { get; set; }
        public long Total { get; set; }
        public bool Warnings { get; set; }
        public List<string> Errors { get; } = new();
    }

    private static Outcome Finish(ItemRun run, string summary)
    {
        var status = run.Errors.Count > 0 ? "failed" : run.Warnings ? "warning" : "success";
        var detail = run.Errors.Count > 0 ? string.Join(" ; ", run.Errors.Take(5))
            : run.Warnings ? "Bazı öğeler alınamadı, ayrıntı günlükte."
            : summary;
        run.Log.Write($"Bitti: {status}");
        return new Outcome(status, detail, run.Total);
    }

    /// <summary>Bir öğeyi her formatta arşivler, doğrular, tüm hedeflere gönderir.</summary>
    private static async Task ProcessItemAsync(ItemRun run, Item item, CancellationToken ct)
    {
        var (job, log) = (run.Job, run.Log);
        var folder = item.DateFolder == null ? run.BaseFolder : $"{run.BaseFolder}/{item.DateFolder}";
        var request = new ArchiveRequest(item.Paths, job.Excludes, Path.Combine(run.Work, $"{item.Name}_{run.Stamp}"), job.Level, run.Password);
        var itemOk = true;
        foreach (var key in job.Formats)
        {
            run.Step++;
            var current = run.Step;
            log.Update(l => { l.Step = current; l.Steps = run.Steps; });
            var format = Formats.Get(key);
            var label = $"{item.Name}.{key}";
            string? file = null;
            try
            {
                Stage(log, "archive", label);
                log.Write($"[{label}] sıkıştırılıyor...");
                var result = await Archiver.CreateAsync(format, request, log.Write, ct, p => log.Update(l => l.Percent = p));
                file = result.Path;
                run.Warnings |= result.HadWarnings;
                var size = new FileInfo(file).Length;

                Stage(log, "verify", label);
                log.Write($"[{label}] doğrulanıyor ({size / 1048576.0:0.0} MB)...");
                await Archiver.VerifyAsync(format, file, run.Password, ct, p => log.Update(l => l.Percent = p));

                foreach (var target in job.Targets)
                {
                    Stage(log, "upload", label);
                    log.Write($"[{label}] gönderiliyor: {target}:{folder}");
                    await Cloud.UploadAsync(file, target, folder, log.Write, s => ApplyStats(log, s), ct);
                    Stage(log, "retention", label);
                    await Cloud.ApplyRetentionAsync(target, run.BaseFolder, format.Extension, job.Keep, job.KeepDays, log.Write, ct);
                }
                run.Total += size;
                log.Write($"[{label}] tamam.");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                log.Write($"[{label}] HATA: {e.Message}");
                run.Errors.Add($"{label}: {e.Message}");
                itemOk = false;
            }
            finally
            {
                // Sonraki format için disk yeri açılır.
                if (file != null) try { File.Delete(file); } catch (IOException) { }
            }
        }
        // Yalnızca tüm format ve hedeflere ulaşan klasör "gönderildi" sayılır; kalanlar sonraki turda yeniden denenir.
        if (itemOk && item.Fingerprint != null) Db.SetFingerprint(job.Id, item.Name, item.Fingerprint);
    }

    private static async Task<Outcome> DatabaseAsync(Job job, string work, RunLog log, CancellationToken ct)
    {
        Validation.Check(job);
        await CheckTargetsAsync(job, ct);
        var baseFolder = $"{job.RemoteFolder.Trim('/', '\\')}/{Cloud.SafeName(job.Name)}";
        var today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var stamp = Cloud.Stamp();
        var password = Secrets.Unprotect(job.PasswordEnc);

        Stage(log, "scan", "Veritabanları listeleniyor");
        var plan = new List<(string Name, Func<Task<(string File, bool Remote)>> Produce)>();
        var warnings = false;
        var preErrors = new List<string>();

        void Note(DecisionKind kind, string db, string reason)
        {
            log.Write($"  {db}: {reason}");
            if (kind == DecisionKind.Warn) warnings = true;
            if (kind == DecisionKind.Fail) preErrors.Add($"{db}: {reason}");
        }

        switch (job.DbEngine)
        {
            case "mssql":
            {
                var all = await MsSql.ListAsync(job, ct);
                var chosen = MsSql.Select(job, all).ToList();
                if (!job.DbAll)
                    foreach (var missing in job.DbNames.Where(n => !all.Any(d => string.Equals(d.Name, n, StringComparison.OrdinalIgnoreCase))))
                        preErrors.Add($"{missing}: veritabanı sunucuda bulunamadı");
                var dir = await MsSql.BackupFolderAsync(job, ct);
                foreach (var db in chosen)
                {
                    var decision = AlwaysOn.Decide(db, job.DbType, job.DbAgPolicy, job.DbCopyOnly);
                    if (decision.Kind != DecisionKind.Run) { Note(decision.Kind, db.Name, decision.Reason); continue; }
                    var name = $"{Cloud.SafeName(db.Name)}_{job.DbType.ToUpperInvariant()}";
                    if (db.InAg) log.Write($"  {db.Name}: Always On {db.AgName}, rol {db.Role}" + (decision.CopyOnly ? ", COPY_ONLY" : ""));
                    plan.Add((name, async () =>
                    {
                        var path = Path.Combine(dir, $"{name}_{stamp}.{(job.DbType == "log" ? "trn" : "bak")}");
                        Stage(log, "sqlbackup", db.Name);
                        log.Write($"[{db.Name}] SQL Server yedeği alınıyor ({job.DbType})...");
                        try
                        {
                            await MsSql.BackupAsync(job, db, job.DbType, decision.CopyOnly, path, p => log.Update(l => l.Percent = p), ct);
                            Stage(log, "sqlverify", db.Name);
                            log.Write($"[{db.Name}] RESTORE VERIFYONLY...");
                            await MsSql.VerifyAsync(job, path, p => log.Update(l => l.Percent = p), ct);
                        }
                        catch
                        {
                            TryDelete(path);
                            throw;
                        }
                        if (!File.Exists(path))
                        {
                            throw new IOException($"Yedek sunucuda alındı ama bu bilgisayardan okunamıyor: {path}. Uygulamayı SQL Server'da çalıştırın veya her ikisinin eriştiği bir ağ klasörü (UNC) belirtin.");
                        }
                        return (path, true);
                    }));
                }
                break;
            }
            case "mysql":
            {
                var names = job.DbAll ? await MySqlEngine.ListAsync(job, ct) : job.DbNames;
                foreach (var db in names)
                {
                    var name = Cloud.SafeName(db);
                    plan.Add((name, async () =>
                    {
                        var path = Path.Combine(work, $"{name}.sql");
                        Stage(log, "sqlbackup", db);
                        log.Write($"[{db}] mysqldump alınıyor...");
                        await MySqlEngine.DumpAsync(job, db, path, work, ct);
                        return (path, false);
                    }));
                }
                break;
            }
            default:
                foreach (var source in job.DbNames)
                {
                    var name = Cloud.SafeName(Path.GetFileNameWithoutExtension(source));
                    plan.Add((name, async () =>
                    {
                        var path = Path.Combine(work, Path.GetFileName(source));
                        Stage(log, "sqlbackup", source);
                        log.Write($"[{name}] SQLite yedeği alınıyor...");
                        await Task.Run(() => SqliteEngine.Backup(source, path), ct);
                        return (path, false);
                    }));
                }
                break;
        }

        var run = new ItemRun(job, work, baseFolder, stamp, password, log, plan.Count * job.Formats.Count) { Warnings = warnings };
        run.Errors.AddRange(preErrors);
        foreach (var (name, produce) in plan)
        {
            string? file = null;
            try
            {
                (file, _) = await produce();
                await ProcessItemAsync(run, new Item(name, new List<string> { file }, today, null), ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException or Microsoft.Data.SqlClient.SqlException or Microsoft.Data.Sqlite.SqliteException)
            {
                log.Write($"[{name}] HATA: {e.Message}");
                run.Errors.Add($"{name}: {e.Message}");
            }
            finally
            {
                if (file != null) TryDelete(file);
            }
        }

        if (plan.Count == 0 && run.Errors.Count == 0)
        {
            log.Write("Bu replikada veya seçimde yedeklenecek veritabanı yok.");
            return new Outcome(run.Warnings ? "warning" : "success", "Yedeklenecek veritabanı yok", 0);
        }
        return Finish(run, $"{plan.Count} veritabanı, {job.Formats.Count} format, {job.Targets.Count} hedef");
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
