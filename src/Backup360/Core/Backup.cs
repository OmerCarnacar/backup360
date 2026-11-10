using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;

namespace Backup360.Core;

public static class BackupRunner
{
    public sealed record Outcome(string Status, string Detail, long Bytes);

    /// <summary>RemotePath: klasör olarak gönderimde gün klasörünün altındaki yol.</summary>
    private sealed record Item(string Name, List<string> Paths, string? DateFolder, string? Fingerprint, string? RemotePath = null);

    /// <summary>Sıkıştırılmış, gönderim sırasında bekleyen arşiv.</summary>
    private sealed record Packed(Item Item, FormatDef Format, string Label, string File, ItemTracker Tracker);

    private const int UploadWorkers = 2;
    private const int QueueLimit = 3;

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
        // Kilit bizdeyse bu işin "çalışıyor" görünen eski kaydı yarıda kesilmiş bir çalışmadır.
        Db.CloseOrphanRuns("job", job.Id);
        var runId = Db.StartRun("job", job.Id, job.Name);
        var space = WorkFolder.Create(job, log.Write);
        var work = space.Work;
        var outcome = new Outcome("failed", "", 0);
        var started = DateTime.Now;
        try
        {
            if (job.Mode != "database" && RansomGuard.Enabled && job.Sources.Count > 0)
            {
                Stage(log, "scan", "Fidye yazılımı kontrolü");
                var guard = await Task.Run(() => RansomGuard.Scan(job.Sources, DateTime.Now.AddDays(-30), ct), ct);
                if (guard.Alarm) throw new RansomwareException(RansomGuard.Describe(guard));
            }
            outcome = job.Mode switch
            {
                "mirror" => await MirrorAsync(job, log, ct),
                "database" => await DatabaseAsync(job, space, log, ct),
                "sync" => await SyncAsync(job, log, ct),
                _ => await ArchiveAsync(job, space, log, ct),
            };
            return outcome;
        }
        catch (OperationCanceledException)
        {
            log.Write("İptal edildi.");
            outcome = new Outcome("canceled", "Kullanıcı iptal etti", 0);
            return outcome;
        }
        catch (RansomwareException e)
        {
            log.Write("DURDURULDU, fidye yazılımı şüphesi: " + e.Message);
            outcome = new Outcome("ransomware", "Yedekleme durduruldu, hedefteki yedeklere dokunulmadı. " + e.Message, 0);
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
        await Task.CompletedTask;
        var missing = job.Targets.Where(t => !Cloud.TargetExists(t)).ToList();
        if (missing.Count > 0) throw new InvalidOperationException("Hedef bağlı değil: " + string.Join(", ", missing));
    }

    private static void ApplyStats(RunLog log, TransferStats s) => log.Update(l =>
    {
        l.Done = s.Bytes; l.Total = s.Total; l.Speed = s.Speed; l.Eta = s.Eta;
        l.Transfers = s.Active.ToList();
        l.Files = s.Files; l.FilesTotal = s.FilesTotal;
        if (s.Current != null) l.Item = s.Current;
        l.Percent = s.Total > 0 ? (int)(s.Bytes * 100 / s.Total) : -1;
    });

    private static void Stage(RunLog log, string stage, string item) => log.Update(l =>
    {
        l.Stage = stage; l.Item = item; l.Percent = -1; l.Speed = 0; l.Eta = -1; l.Done = 0; l.Total = 0;
    });

    private static async Task<Outcome> SyncAsync(Job job, RunLog log, CancellationToken ct)
    {
        Validation.Check(job);
        await CheckTargetsAsync(job, ct);
        var pairs = SyncPlanner.Pairs(job);
        var stamp = Cloud.Stamp();
        if (RansomGuard.Enabled)
        {
            Stage(log, "scan", "Toplu değişim kontrolü");
            foreach (var pair in pairs.Where(p => Directory.Exists(p.Source)))
            {
                var diff = await SyncPlanner.DiffAsync(pair, job.Excludes, ct, sizeOnly: true);
                if (RansomGuard.IsMassChange(diff))
                    throw new RansomwareException($"{pair.Source}: dosyaların yarısından fazlası bir anda değişmiş veya kaybolmuş ({diff.Count(SyncPlanner.Changed)} değişen, {diff.Count(SyncPlanner.Extra)} kayıp, {diff.Count(SyncPlanner.Same)} aynı).");
            }
        }
        var errors = new List<string>();
        long total = 0;
        for (var i = 0; i < pairs.Count; i++)
        {
            var pair = pairs[i];
            var (current, count) = (i + 1, pairs.Count);
            Stage(log, "scan", pair.Source);
            log.Update(l => { l.Step = current; l.Steps = count; });
            log.Write($"Eşitleniyor: {pair.Source} -> {pair.Target}:{pair.Folder}");
            long sent = 0;
            try
            {
                Stage(log, "upload", pair.Source);
                await Cloud.SyncAsync(pair, job.MirrorDelete, job.RemoteFolder, stamp, job.Excludes, log.Write, s => { sent = s.Bytes; ApplyStats(log, s); }, ct);
                total += sent;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                log.Write("HATA: " + e.Message);
                errors.Add(e.Message);
            }
        }
        var status = errors.Count > 0 ? "failed" : "success";
        log.Write($"Bitti: {status}");
        return new Outcome(status, errors.Count > 0 ? string.Join(" ; ", errors.Take(5)) : $"{pairs.Count} eşitleme, {total / 1048576.0:0.0} MB fark aktarıldı", total);
    }

    private static async Task<Outcome> MirrorAsync(Job job, RunLog log, CancellationToken ct)
    {
        Validation.Check(job);
        await CheckTargetsAsync(job, ct);
        var folder = Cloud.JobFolder(job);
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
    private static List<Item> BuildDatedItems(Job job, string runFolder, RunLog log)
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
            var sourceName = Cloud.SafeName(Path.GetFileName(Path.TrimEndingDirectorySeparator(source)));
            var prefix = job.Sources.Count > 1 ? sourceName + "__" : "";
            var parent = job.Sources.Count > 1 ? sourceName + "/" : "";
            var root = new DirectoryInfo(source);
            foreach (var dir in root.EnumerateDirectories().Where(d => !d.Attributes.HasFlag(FileAttributes.System)).OrderBy(d => d.Name))
            {
                var (count, size, last) = Scan(dir.FullName);
                if (count == 0) { log.Write($"  boş klasör atlandı: {dir.Name}"); continue; }
                Add(prefix + Cloud.SafeName(dir.Name), parent + Cloud.SafeName(dir.Name), new List<string> { dir.FullName }, count, size, last);
            }
            var loose = root.EnumerateFiles().Where(f => !f.Attributes.HasFlag(FileAttributes.System)).ToList();
            if (loose.Count > 0)
                Add(prefix + "_KokDosyalar", parent + "_KokDosyalar", loose.Select(f => f.FullName).ToList(), loose.Count, loose.Sum(f => f.Length), loose.Max(f => f.LastWriteTime));
        }
        return items;

        void Add(string name, string remotePath, List<string> paths, int count, long size, DateTime last)
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
            var date = !job.UseDateFolder ? runFolder : job.DateBy == "folder" ? last.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : today;
            items.Add(new Item(name, paths, date, fingerprint, remotePath));
        }
    }

    private static async Task<Outcome> ArchiveAsync(Job job, WorkSpace space, RunLog log, CancellationToken ct)
    {
        var (work, runFolder) = (space.Work, WorkFolder.RunFolder());
        Validation.Check(job);
        await CheckTargetsAsync(job, ct);
        var password = Secrets.Unprotect(job.PasswordEnc);
        var baseFolder = Cloud.JobFolder(job);
        var stamp = Cloud.Stamp();
        var warnings = false;

        List<Item> items;
        if (job.Mode == "dated")
        {
            if (!job.Sources.Any(Directory.Exists)) throw new IOException("Kaynak klasörlerin hiçbiri bulunamadı.");
            warnings = !job.Sources.All(Directory.Exists);
            Stage(log, "scan", "Klasörler taranıyor");
            items = BuildDatedItems(job, runFolder, log);
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

        var run = new ItemRun(job, work, baseFolder, stamp, password, log, items.Count * job.Formats.Count) { Warnings = warnings, LocalFolder = WorkFolder.RunDirectory(space.Store, runFolder) };
        await RunPipelineAsync(run, async () => { foreach (var item in items) await CompressItemAsync(run, item, ct); }, ct);
        WorkFolder.Prune(space.Store, WorkFolder.KeepDays, DateTime.Today, log.Write);
        return Finish(run, $"{items.Count} öğe, {job.Formats.Count} format, {job.Targets.Count} hedef");
    }

    /// <summary>Alt klasörleri sıkıştırmadan tarih klasörlerine gönderir.</summary>
    private static async Task<Outcome> SendFoldersAsync(Job job, List<Item> items, string baseFolder, string stamp, bool warnings, string work, RunLog log, CancellationToken ct)
    {
        var run = new ItemRun(job, work, baseFolder, stamp, null, log, items.Count * job.Targets.Count) { Warnings = warnings };
        foreach (var item in items)
        {
            var dateFolder = item.DateFolder == null ? baseFolder : $"{baseFolder}/{item.DateFolder}";
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
                        if (Directory.Exists(path)) await Cloud.MirrorAsync(path, target, dateFolder, false, stamp, job.Excludes, log.Write, s => { sent = s.Bytes; ApplyStats(log, s); }, ct, item.RemotePath ?? item.Name);
                        else await Cloud.MirrorAsync(path, target, $"{dateFolder}/{item.RemotePath ?? item.Name}", false, stamp, job.Excludes, log.Write, s => { sent = s.Bytes; ApplyStats(log, s); }, ct);
                    }
                    run.Total += sent;
                    Stage(log, "retention", item.Name);
                    await Cloud.ApplyFolderRetentionAsync(target, baseFolder, job.Keep, job.KeepDays, log.Write, ct, job.Sources.Count > 1 ? 3 : 2);
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
        private readonly object _gate = new();
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
        public bool Corrupt { get; private set; }
        public List<string> Errors { get; } = new();
        /// <summary>Doluysa arşivler gönderimden sonra da burada kalır.</summary>
        public string? LocalFolder { get; init; }
        public Channel<Packed> Queue { get; } = Channel.CreateBounded<Packed>(new BoundedChannelOptions(QueueLimit) { SingleWriter = true });
        public SemaphoreSlim Retention { get; } = new(1, 1);
        public SendBoard Board { get; } = new(log);

        public void AddBytes(long bytes) { lock (_gate) Total += bytes; }

        public void Fail(string label, string message, bool corrupt)
        {
            lock (_gate)
            {
                Errors.Add($"{label}: {message}");
                Corrupt |= corrupt;
            }
        }
    }

    /// <summary>Bir öğenin tüm format ve hedeflere ulaştığını izler; kalanlar sonraki turda yeniden denenir.</summary>
    private sealed class ItemTracker(Job job, Item item, int parts)
    {
        private readonly object _gate = new();
        private int _left = parts;
        private bool _ok = true;

        public void Done(bool ok)
        {
            bool complete;
            lock (_gate)
            {
                _ok &= ok;
                complete = --_left == 0 && _ok;
            }
            if (complete && item.Fingerprint != null) Db.SetFingerprint(job.Id, item.Name, item.Fingerprint);
        }
    }

    /// <summary>Arka plandaki gönderimleri canlı duruma yazar. Sıkıştırma bitince ilk gönderim ana göstergeye geçer.</summary>
    private sealed class SendBoard(RunLog log)
    {
        private readonly object _gate = new();
        private readonly List<SendLine> _lines = new();
        private bool _alone;

        public void CompressionFinished()
        {
            lock (_gate) _alone = true;
            Stage(log, "upload", "Gönderimler tamamlanıyor");
            Publish(null, null);
        }

        public void Show(string label, string stage, int percent = -1) => Set(new SendLine(label, stage, percent, 0, -1), null);

        public void Report(string label, TransferStats s) =>
            Set(new SendLine(label, "upload", s.Total > 0 ? (int)(s.Bytes * 100 / s.Total) : -1, s.Speed, s.Eta), s);

        public void Remove(string label)
        {
            lock (_gate) _lines.RemoveAll(l => l.Label == label);
            Publish(null, null);
        }

        private void Set(SendLine line, TransferStats? stats)
        {
            lock (_gate)
            {
                var index = _lines.FindIndex(l => l.Label == line.Label);
                if (index >= 0) _lines[index] = line;
                else _lines.Add(line);
            }
            Publish(line, stats);
        }

        private void Publish(SendLine? changed, TransferStats? stats)
        {
            List<SendLine> visible;
            bool lead;
            lock (_gate)
            {
                lead = _alone && changed != null && _lines.FirstOrDefault()?.Label == changed.Label;
                visible = (_alone ? _lines.Skip(1) : _lines).ToList();
            }
            log.Update(l =>
            {
                l.Sending = visible;
                if (!lead) return;
                l.Stage = changed!.Stage;
                l.Item = changed.Label;
                l.Percent = changed.Percent;
                if (stats != null) return;
                l.Speed = 0; l.Eta = -1; l.Done = 0; l.Total = 0;
            });
            if (lead && stats != null) ApplyStats(log, stats);
        }
    }

    private static Outcome Finish(ItemRun run, string summary)
    {
        var status = run.Corrupt ? "corrupt" : run.Errors.Count > 0 ? "failed" : run.Warnings ? "warning" : "success";
        var detail = run.Errors.Count > 0 ? (run.Corrupt ? "YEDEK BOZUK: " : "") + string.Join(" ; ", run.Errors.Take(5))
            : run.Warnings ? "Bazı öğeler alınamadı, ayrıntı günlükte."
            : summary;
        run.Log.Write($"Bitti: {status}");
        return new Outcome(status, detail, run.Total);
    }

    /// <summary>Sıkıştırma tek tek yürür; bellek yetmezliğini önler. Gönderim arka planda sürer, ikisi birbirini beklemez.</summary>
    private static async Task RunPipelineAsync(ItemRun run, Func<Task> compress, CancellationToken ct)
    {
        if (run.LocalFolder != null) run.Log.Write($"Yerel yedek klasörü: {run.LocalFolder}");
        var workers = Enumerable.Range(0, UploadWorkers).Select(_ => Task.Run(() => SendWorkerAsync(run, ct), CancellationToken.None)).ToArray();
        ExceptionDispatchInfo? failure = null;
        try { await compress(); }
        catch (Exception e) { failure = ExceptionDispatchInfo.Capture(e); }

        run.Queue.Writer.TryComplete();
        run.Board.CompressionFinished();
        try { await Task.WhenAll(workers); }
        catch (OperationCanceledException) when (failure != null) { }
        failure?.Throw();
    }

    /// <summary>Bir öğeyi her formatta arşivler ve gönderim sırasına koyar.</summary>
    private static async Task CompressItemAsync(ItemRun run, Item item, CancellationToken ct)
    {
        var (job, log) = (run.Job, run.Log);
        var request = new ArchiveRequest(item.Paths, job.Excludes, Path.Combine(run.Work, $"{item.Name}_{run.Stamp}"), job.Level, run.Password);
        var tracker = new ItemTracker(job, item, job.Formats.Count);
        foreach (var key in job.Formats)
        {
            run.Step++;
            var current = run.Step;
            log.Update(l => { l.Step = current; l.Steps = run.Steps; });
            var format = Formats.Get(key);
            var label = $"{item.Name}.{key}";
            try
            {
                Stage(log, "archive", label);
                log.Write($"[{label}] sıkıştırılıyor...");
                var result = await Archiver.CreateAsync(format, request, log.Write, ct, p => log.Update(l => l.Percent = p));
                run.Warnings |= result.HadWarnings;
                var packed = new Packed(item, format, label, result.Path, tracker);
                if (!run.Queue.Writer.TryWrite(packed))
                {
                    Stage(log, "upload", "Gönderim sırası bekleniyor");
                    await run.Queue.Writer.WriteAsync(packed, ct);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                log.Write($"[{label}] HATA: {e.Message}");
                run.Fail(label, e.Message, e is BackupCorruptException);
                tracker.Done(false);
            }
        }
    }

    /// <summary>Sıradaki arşivi doğrular ve tüm hedeflere gönderir.</summary>
    private static async Task SendWorkerAsync(ItemRun run, CancellationToken ct)
    {
        var (job, log) = (run.Job, run.Log);
        await foreach (var p in run.Queue.Reader.ReadAllAsync(ct))
        {
            var folder = p.Item.DateFolder == null ? run.BaseFolder : $"{run.BaseFolder}/{p.Item.DateFolder}";
            var file = p.File;
            var keepLocal = false;
            var ok = false;
            try
            {
                var size = new FileInfo(file).Length;
                run.Board.Show(p.Label, "verify");
                log.Write($"[{p.Label}] doğrulanıyor ({size / 1048576.0:0.0} MB)...");
                try { await Archiver.VerifyAsync(p.Format, file, run.Password, ct, percent => run.Board.Show(p.Label, "verify", percent)); }
                catch (Exception e) when (e is not OperationCanceledException) { throw new BackupCorruptException($"Arşiv bozuk, gönderilmedi: {e.Message}", e); }
                if (run.LocalFolder != null)
                {
                    // Yarım veya bozuk arşiv saklama klasörüne hiç girmez.
                    var kept = Path.Combine(run.LocalFolder, Path.GetFileName(file));
                    File.Move(file, kept, true);
                    (file, keepLocal) = (kept, true);
                }

                var fingerprint = await Task.Run(() => BackupAudit.Md5Of(file), ct);
                var name = Path.GetFileName(file);
                var relative = p.Item.DateFolder == null ? name : $"{p.Item.DateFolder}/{name}";
                foreach (var target in job.Targets)
                {
                    run.Board.Show(p.Label, "upload");
                    log.Write($"[{p.Label}] gönderiliyor: {target}:{folder}");
                    await Cloud.UploadAsync(file, target, folder, log.Write, s => run.Board.Report(p.Label, s), ct);
                    Db.RecordBackup(target, run.BaseFolder, relative, job.Id, job.Name, size, fingerprint);
                    run.Board.Show(p.Label, "retention");
                    await run.Retention.WaitAsync(ct);
                    try { await Cloud.ApplyRetentionAsync(target, run.BaseFolder, p.Format.Extension, job.Keep, job.KeepDays, log.Write, ct); }
                    finally { run.Retention.Release(); }
                }
                run.AddBytes(size);
                log.Write($"[{p.Label}] tamam.");
                ok = true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                log.Write($"[{p.Label}] HATA: {e.Message}");
                run.Fail(p.Label, e.Message, e is BackupCorruptException);
            }
            finally
            {
                run.Board.Remove(p.Label);
                if (!keepLocal) TryDelete(file);
            }
            p.Tracker.Done(ok);
        }
    }

    private static async Task<Outcome> DatabaseAsync(Job job, WorkSpace space, RunLog log, CancellationToken ct)
    {
        var (work, runFolder) = (space.Work, WorkFolder.RunFolder());
        Validation.Check(job);
        await CheckTargetsAsync(job, ct);
        var baseFolder = Cloud.JobFolder(job);
        var today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var stamp = Cloud.Stamp();
        var password = Secrets.Unprotect(job.PasswordEnc);

        Stage(log, "scan", "Veritabanları listeleniyor");
        var plan = new List<(string Name, Func<Task<(string File, bool Remote)>> Produce)>();
        var warnings = false;
        var restoreSkipped = false;
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
                            if (job.DbRestoreTest && job.DbType == "full")
                            {
                                Stage(log, "sqlrestore", db.Name);
                                log.Write($"[{db.Name}] geri yükleme sınavı ve DBCC CHECKDB...");
                                try
                                {
                                    await MsSql.RestoreTestAsync(job, path, p => log.Update(l => l.Percent = p), ct);
                                    log.Write($"[{db.Name}] geri yükleme ve CHECKDB temiz.");
                                }
                                catch (Microsoft.Data.SqlClient.SqlException e) when (!BackupHealth.IsCorruption(e))
                                {
                                    // Disk veya yetki yetersizliği yedeğin bozuk olduğunu göstermez; yine de kullanıcı bilmeli.
                                    restoreSkipped = true;
                                    log.Write($"  uyarı: {db.Name} geri yükleme sınavı yapılamadı: {e.Message}");
                                }
                            }
                        }
                        catch (Microsoft.Data.SqlClient.SqlException e) when (BackupHealth.IsCorruption(e))
                        {
                            TryDelete(path);
                            throw new BackupCorruptException($"SQL Server yedeği bozuk, gönderilmedi: {e.Message}", e);
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
            case "postgres":
            {
                var names = job.DbAll ? await PostgresEngine.ListAsync(job, ct) : job.DbNames;
                foreach (var db in names)
                {
                    var name = Cloud.SafeName(db);
                    plan.Add((name, async () =>
                    {
                        var path = Path.Combine(work, $"{name}.dump");
                        Stage(log, "sqlbackup", db);
                        log.Write($"[{db}] pg_dump alınıyor...");
                        await PostgresEngine.DumpAsync(job, db, path, ct);
                        Stage(log, "sqlverify", db);
                        await PostgresEngine.VerifyAsync(job, path, ct);
                        if (job.DbRestoreTest)
                        {
                            Stage(log, "sqlrestore", db);
                            log.Write($"[{db}] geri yükleme sınavı...");
                            try
                            {
                                await PostgresEngine.RestoreTestAsync(job, path, ct);
                                log.Write($"[{db}] geri yükleme sınavı temiz.");
                            }
                            catch (IOException e) when (e is not BackupCorruptException)
                            {
                                restoreSkipped = true;
                                log.Write($"  uyarı: {db} geri yükleme sınavı yapılamadı: {e.Message}");
                            }
                        }
                        return (path, false);
                    }));
                }
                break;
            }
            case "firebird":
                foreach (var source in job.DbNames)
                {
                    var name = Cloud.SafeName(Path.GetFileNameWithoutExtension(source));
                    plan.Add((name, async () =>
                    {
                        var path = Path.Combine(work, $"{name}.fbk");
                        Stage(log, "sqlbackup", source);
                        log.Write($"[{name}] gbak alınıyor...");
                        await FirebirdEngine.BackupAsync(job, source, path, ct);
                        if (job.DbRestoreTest && FirebirdEngine.IsLocal(job))
                        {
                            Stage(log, "sqlrestore", source);
                            log.Write($"[{name}] geri yükleme sınavı...");
                            try
                            {
                                await FirebirdEngine.RestoreTestAsync(job, path, work, ct);
                                log.Write($"[{name}] geri yükleme sınavı temiz.");
                            }
                            catch (IOException e) when (e is not BackupCorruptException)
                            {
                                restoreSkipped = true;
                                log.Write($"  uyarı: {name} geri yükleme sınavı yapılamadı: {e.Message}");
                            }
                        }
                        return (path, false);
                    }));
                }
                break;
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

        var run = new ItemRun(job, work, baseFolder, stamp, password, log, plan.Count * job.Formats.Count) { Warnings = warnings, LocalFolder = WorkFolder.RunDirectory(space.Store, runFolder) };
        run.Errors.AddRange(preErrors);
        await RunPipelineAsync(run, async () =>
        {
            foreach (var (name, produce) in plan)
            {
                string? file = null;
                try
                {
                    (file, _) = await produce();
                    run.Warnings |= restoreSkipped;
                    await CompressItemAsync(run, new Item(name, new List<string> { file }, today, null), ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException or Microsoft.Data.SqlClient.SqlException or Microsoft.Data.Sqlite.SqliteException)
                {
                    log.Write($"[{name}] HATA: {e.Message}");
                    run.Fail(name, e.Message, e is BackupCorruptException);
                }
                finally
                {
                    if (file != null) TryDelete(file);
                }
            }
        }, ct);
        WorkFolder.Prune(space.Store, WorkFolder.KeepDays, DateTime.Today, log.Write);

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
