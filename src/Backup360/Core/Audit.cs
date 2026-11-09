using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Backup360.Core;

public sealed record BackupFile(string Target, string Base, string Path, long JobId, string JobName, long Size, string Md5, string Created, string? LastCheck, string Status, string Detail);

/// <summary>
/// Hedefe gönderilmiş yedekleri sonradan denetler. Hızlı kontrol dosyanın durduğunu ve
/// parmak izinin değişmediğini, tam kontrol son yedeklerin indirilip açılabildiğini doğrular.
/// </summary>
public static class BackupAudit
{
    public const string Ok = "ok", Missing = "missing", Changed = "changed", Unknown = "unknown";

    private static readonly Regex Stamped = new(@"^(?<item>.+)_(?<stamp>\d{8}-\d{6})\.(?<ext>[A-Za-z0-9.]+)$", RegexOptions.Compiled);

    public static string Md5Of(string file)
    {
        using var s = File.OpenRead(file);
        return Convert.ToHexStringLower(MD5.HashData(s));
    }

    private static string BaseOf(Job j) => $"{j.RemoteFolder.Trim('/', '\\')}/{Cloud.SafeName(j.Name)}";

    private static bool HasArchives(Job j) => j.Mode is "archive" or "database" || (j.Mode == "dated" && j.DatedOutput == "archive");

    /// <summary>Bu özellikten önce gönderilmiş yedekleri mevcut durumlarıyla kayda alır.</summary>
    private static void Adopt(Job job, string target, string folder, Dictionary<string, (long Size, string Md5)> remote, HashSet<string> known)
    {
        foreach (var (path, info) in remote)
        {
            if (known.Contains(path)) continue;
            var name = path[(path.LastIndexOf('/') + 1)..];
            var match = Stamped.Match(name);
            if (!match.Success || Formats.All.All(f => f.Extension != match.Groups["ext"].Value)) continue;
            Db.RecordBackup(target, folder, path, job.Id, job.Name, info.Size, info.Md5);
        }
    }

    public static async Task<BackupRunner.Outcome> RunAsync(bool deep, Action<string>? ui, CancellationToken ct)
    {
        using var gate = RunGate.TryEnter("audit");
        if (gate == null) return new BackupRunner.Outcome("skipped", "Denetim zaten çalışıyor", 0);

        var title = deep ? "Tam yedek denetimi" : "Hızlı yedek denetimi";
        using var log = new RunLog(title, ui, "audit", 0, title, "audit");
        var runId = Db.StartRun("audit", 0, title);
        var started = DateTime.Now;
        var work = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "Backup360", Guid.NewGuid().ToString("N"))).FullName;
        var outcome = new BackupRunner.Outcome("failed", "", 0);
        try
        {
            outcome = await ExecuteAsync(deep, work, log, ct);
            return outcome;
        }
        catch (OperationCanceledException)
        {
            log.Write("İptal edildi.");
            outcome = new BackupRunner.Outcome("canceled", "Kullanıcı iptal etti", 0);
            return outcome;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
            log.Write("HATA: " + e.Message);
            outcome = new BackupRunner.Outcome("failed", e.Message, 0);
            return outcome;
        }
        finally
        {
            Db.FinishRun(runId, outcome.Status, outcome.Detail, outcome.Bytes);
            try { Directory.Delete(work, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            await Notifier.ReportAsync("Yedek denetimi", title, outcome.Status, outcome.Detail, started, outcome.Bytes, log.Write);
        }
    }

    private static async Task<BackupRunner.Outcome> ExecuteAsync(bool deep, string work, RunLog log, CancellationToken ct)
    {
        var jobs = Db.Jobs();
        var targets = (await Cloud.ListTargetsAsync(ct)).Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unreachable = new List<string>();
        int ok = 0, changed = 0, missing = 0;

        log.Update(l => { l.Stage = "scan"; l.Item = "Hedefler karşılaştırılıyor"; l.Percent = -1; });

        // Kayıtlı işlerin hedeflerindeki önceki yedekler de kapsama girsin.
        foreach (var job in jobs.Where(HasArchives))
            foreach (var target in job.Targets.Where(targets.Contains))
            {
                try
                {
                    var folder = BaseOf(job);
                    var known = Db.BackupFiles().Where(f => f.Target == target && f.Base == folder).Select(f => f.Path).ToHashSet();
                    Adopt(job, target, folder, await Cloud.ListHashesAsync(target, folder, ct), known);
                }
                catch (IOException e) { log.Write($"  {target}: {e.Message}"); }
            }

        var groups = Db.BackupFiles().GroupBy(f => (f.Target, f.Base)).ToList();
        var position = 0;
        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();
            position++;
            var (current, count) = (position, groups.Count);
            log.Update(l => { l.Item = $"{group.Key.Target}:{group.Key.Base}"; l.Step = current; l.Steps = count; });
            if (!targets.Contains(group.Key.Target))
            {
                unreachable.Add($"{group.Key.Target}: hedef tanımlı değil");
                continue;
            }

            Dictionary<string, (long Size, string Md5)> remote;
            try { remote = await Cloud.ListHashesAsync(group.Key.Target, group.Key.Base, ct); }
            catch (IOException e)
            {
                // Hedefe ulaşılamaması yedeğin kaybolduğu anlamına gelmez.
                unreachable.Add($"{group.Key.Target}: {e.Message}");
                log.Write($"  Ulaşılamadı: {group.Key.Target}: {e.Message}");
                continue;
            }

            foreach (var f in group)
            {
                string status, detail = "";
                if (!remote.TryGetValue(f.Path, out var info)) { status = Missing; detail = "Hedefte bulunamadı"; missing++; }
                else if (info.Size != f.Size || (f.Md5.Length > 0 && info.Md5.Length > 0 && !string.Equals(info.Md5, f.Md5, StringComparison.OrdinalIgnoreCase)))
                {
                    status = Changed;
                    detail = "Parmak izi değişmiş: dosya bozulmuş veya değiştirilmiş";
                    changed++;
                }
                else { status = Ok; ok++; }
                Db.UpdateBackupCheck(f, status, detail);
                if (status != Ok) log.Write($"  SORUN {f.Target}:{f.Base}/{f.Path}: {detail}");
            }
        }

        if (deep)
        {
            var newest = Db.BackupFiles().Where(f => f.Status == Ok && targets.Contains(f.Target))
                .Select(f => (File: f, Match: Stamped.Match(f.Path[(f.Path.LastIndexOf('/') + 1)..])))
                .Where(x => x.Match.Success)
                .GroupBy(x => (x.File.Target, x.File.JobId, Item: x.Match.Groups["item"].Value, Ext: x.Match.Groups["ext"].Value))
                .Select(g => g.OrderByDescending(x => x.Match.Groups["stamp"].Value, StringComparer.Ordinal).First().File)
                .ToList();
            for (var i = 0; i < newest.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var f = newest[i];
                var (current, count) = (i + 1, newest.Count);
                log.Update(l => { l.Stage = "audit"; l.Item = f.Path; l.Step = current; l.Steps = count; l.Percent = -1; });
                log.Write($"İndirilip sınanıyor: {f.Target}:{f.Path}");
                var name = f.Path[(f.Path.LastIndexOf('/') + 1)..];
                var local = Path.Combine(work, name);
                try
                {
                    await Cloud.DownloadFileAsync(f.Target, f.Base, f.Path, local, ct);
                    var ext = Stamped.Match(name).Groups["ext"].Value;
                    var password = Secrets.Unprotect(jobs.FirstOrDefault(j => j.Id == f.JobId)?.PasswordEnc);
                    try
                    {
                        if (f.Md5.Length > 0 && !string.Equals(Md5Of(local), f.Md5, StringComparison.OrdinalIgnoreCase)) throw new IOException("İndirilen dosyanın parmak izi farklı");
                        await Archiver.VerifyAsync(Formats.Get(ext), local, password, ct);
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        changed++;
                        ok--;
                        Db.UpdateBackupCheck(f, Changed, "Tam kontrolde açılamadı: " + e.Message);
                        log.Write($"  SORUN {f.Target}:{f.Path}: {e.Message}");
                    }
                }
                catch (Exception e) when (e is IOException or InvalidOperationException)
                {
                    unreachable.Add($"{f.Path}: {e.Message}");
                }
                finally
                {
                    try { File.Delete(local); } catch (IOException) { }
                }
            }
        }

        var summary = $"{ok} sağlam, {changed} bozuk, {missing} eksik";
        if (unreachable.Count > 0) summary += $", {unreachable.Count} kontrol edilemedi";
        log.Write("Sonuç: " + summary);
        var status2 = changed + missing > 0 ? "corrupt" : unreachable.Count > 0 ? "failed" : "success";
        return new BackupRunner.Outcome(status2, unreachable.Count > 0 ? summary + ": " + string.Join(" ; ", unreachable.Take(3)) : summary, 0);
    }
}
