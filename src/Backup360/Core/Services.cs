using System.Globalization;
using System.Text;
using System.Text.Json;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Backup360.Core;

public sealed class SmtpSettings
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public string Security { get; set; } = "auto";   // auto | ssl | starttls | none
    public string User { get; set; } = "";
    public string? PasswordEnc { get; set; }
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public bool OnWarning { get; set; } = true;
    public bool OnSuccess { get; set; }
}

public static class Notifier
{
    public static SmtpSettings Load() =>
        Db.GetSetting("smtp") is { } json ? JsonSerializer.Deserialize<SmtpSettings>(json) ?? new() : new();

    public static void Save(SmtpSettings s) => Db.SetSetting("smtp", JsonSerializer.Serialize(s));

    public static async Task SendAsync(SmtpSettings s, string subject, string body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(s.Host)) throw new ArgumentException("SMTP sunucusunu yazın.");
        if (string.IsNullOrWhiteSpace(s.To)) throw new ArgumentException("Alıcı e-posta adresini yazın.");
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(string.IsNullOrWhiteSpace(s.From) ? s.User : s.From));
        foreach (var to in s.To.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = body };

        using var client = new SmtpClient { Timeout = 30_000 };
        var security = s.Security switch
        {
            "ssl" => SecureSocketOptions.SslOnConnect,
            "starttls" => SecureSocketOptions.StartTls,
            "none" => SecureSocketOptions.None,
            _ => SecureSocketOptions.Auto,
        };
        await client.ConnectAsync(s.Host, s.Port, security, ct);
        if (!string.IsNullOrEmpty(s.User)) await client.AuthenticateAsync(s.User, Secrets.Unprotect(s.PasswordEnc) ?? "", ct);
        await client.SendAsync(message, ct);
        await client.DisconnectAsync(true, ct);
    }

    public static bool ShouldNotify(SmtpSettings s, string status) =>
        s.Enabled && status switch { "failed" => true, "warning" => s.OnWarning, "success" => s.OnSuccess, _ => false };

    /// <summary>Sonucu e-posta ile bildirir. Bildirim hatası yedeğin sonucunu asla değiştirmez.</summary>
    public static async Task ReportAsync(string kind, string name, string status, string detail, DateTime started, long bytes, Action<string>? log)
    {
        try
        {
            var settings = Load();
            if (!ShouldNotify(settings, status)) return;
            var label = status switch { "failed" => "HATA", "warning" => "UYARI", _ => "BAŞARILI" };
            var seconds = (DateTime.Now - started).TotalSeconds;
            var body = new StringBuilder()
                .AppendLine($"İş       : {name} ({kind})")
                .AppendLine($"Durum    : {label}")
                .AppendLine($"Bilgisayar: {Environment.MachineName}")
                .AppendLine($"Başlangıç: {started:yyyy-MM-dd HH:mm:ss}")
                .AppendLine($"Süre     : {(long)seconds} sn")
                .AppendLine($"Aktarılan: {bytes / 1048576.0:0.0} MB")
                .AppendLine().AppendLine("Ayrıntı:").AppendLine(detail).ToString();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await SendAsync(settings, $"[Backup360] {label}: {name}", body, cts.Token);
            log?.Invoke("E-posta bildirimi gönderildi.");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            log?.Invoke("E-posta gönderilemedi: " + e.Message);
        }
    }
}

public sealed record ConnectionResult(bool Ok, string Message, long LatencyMs, double UploadBytesPerSecond);

public static class Estimator
{
    public static double? KnownSpeed(string target) =>
        double.TryParse(Db.GetSetting("speed:" + target), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : null;

    public static void StoreSpeed(string target, double bytesPerSecond) =>
        Db.SetSetting("speed:" + target, bytesPerSecond.ToString("R", CultureInfo.InvariantCulture));

    public static long MeasureSources(IEnumerable<string> sources)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System };
        long total = 0;
        foreach (var s in sources)
        {
            if (File.Exists(s)) total += new FileInfo(s).Length;
            else if (Directory.Exists(s)) total += new DirectoryInfo(s).EnumerateFiles("*", options).Sum(f => f.Length);
        }
        return total;
    }

    /// <summary>Yeterli veri olan her hedef için tahmini aktarım süresini açıklayan metin üretir.</summary>
    public static string Describe(Job job)
    {
        var sb = new StringBuilder();
        var last = Db.LastRun("job", job.Id);
        var folderBased = job.Mode == "mirror" || (job.Mode == "dated" && job.DatedOutput == "folder");
        var source = job.Mode == "database" ? 0 : MeasureSources(job.Sources);
        if (job.Mode != "database") sb.AppendLine($"Kaynak boyutu: {Size(source)}");

        long payload;
        if (last is { Status: "success" or "warning", Bytes: > 0 })
        {
            payload = last.Bytes;
            sb.AppendLine($"Gönderilecek veri: {Size(payload)} (son çalışmaya göre)");
            if (DateTime.TryParse(last.Started, out var s) && DateTime.TryParse(last.Ended, out var e))
                sb.AppendLine($"Son çalışma süresi: {Time((e - s).TotalSeconds)}");
        }
        else if (job.Mode == "database")
        {
            sb.AppendLine("Veritabanı boyutu ilk yedekten sonra hesaplanır.");
            return sb.ToString();
        }
        else
        {
            payload = folderBased ? source : source * job.Formats.Count;
            sb.AppendLine($"Gönderilecek veri: en çok {Size(payload)} (henüz çalışmadı, sıkıştırma hesaba katılmadı)");
        }

        if (!folderBased && job.Mode != "database")
        {
            var rate = job.Level >= 9 ? 12d * 1048576 : job.Level >= 5 ? 35d * 1048576 : 90d * 1048576;
            sb.AppendLine($"Yaklaşık sıkıştırma süresi: {Time(source * job.Formats.Count / rate)}");
        }

        foreach (var target in job.Targets)
        {
            var speed = KnownSpeed(target);
            sb.AppendLine(speed is { } v
                ? $"{target}: {Size(v)}/sn hızla yaklaşık {Time(payload / v)}"
                : $"{target}: hız bilinmiyor; Hedefler sayfasından bağlantı testi yapın");
        }
        return sb.ToString();
    }

    private static string Size(double bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        var i = 0;
        while (bytes >= 1024 && i < units.Length - 1) { bytes /= 1024; i++; }
        return $"{bytes:0.#} {units[i]}";
    }

    private static string Time(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(1, seconds));
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours} sa {t.Minutes} dk";
        return t.TotalMinutes >= 1 ? $"{t.Minutes} dk {t.Seconds} sn" : $"{t.Seconds} sn";
    }
}
