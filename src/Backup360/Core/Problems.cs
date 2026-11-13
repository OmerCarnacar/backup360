using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Backup360.Core;

public sealed record Problem(long Id, string Time, string Kind, long RefId, string Name, string Item, string Level, string Message, string? Resolved)
{
    public bool IsOpen => Resolved == null;
}

/// <summary>
/// Hata ve uyarı defteri. Günlüğe yazılan her satır buradan geçer: hata, uyarı, yedeklenemeyen dosya ve
/// aktarılamayan öğe ayrı kayıt olur. Aynı öğe sonradan başarıyla tamamlanınca kaydı kendiliğinden kapanır.
/// </summary>
public static class Problems
{
    public const string Error = "error", Warning = "warning";

    private static readonly Regex ItemError = new(@"^\[(?<item>[^\]]+)\]\s+HATA:\s*(?<msg>.*)$", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex ItemDone = new(@"^\[(?<item>[^\]]+)\]\s+tamam\.$", RegexOptions.Compiled);

    /// <summary>Günlük satırını sınıflandırır; sorun değilse null döner.</summary>
    public static (string Level, string Item, string Message)? Classify(string message)
    {
        var text = message.Trim();
        if (text.Length == 0) return null;
        if (ItemError.Match(text) is { Success: true } m) return (Error, m.Groups["item"].Value, m.Groups["msg"].Value);

        if (text.StartsWith("HATA", StringComparison.Ordinal))
        {
            var cut = text.IndexOf(':');
            var rest = cut >= 0 ? text[(cut + 1)..].Trim() : text;
            // Dosya dosya aktarımda satır girintilidir: "  HATA: yol: neden".
            if (message.StartsWith("  ", StringComparison.Ordinal) && rest.IndexOf(": ", StringComparison.Ordinal) is > 0 and var split)
                return (Error, rest[..split], rest[(split + 2)..]);
            return (Error, "", rest);
        }
        if (text.StartsWith("SORUN ", StringComparison.Ordinal))
        {
            var rest = text[6..];
            var split = rest.LastIndexOf(": ", StringComparison.Ordinal);
            return split > 0 ? (Error, rest[..split], rest[(split + 2)..]) : (Error, "", rest);
        }
        if (text.StartsWith("DURDURULDU", StringComparison.Ordinal)) return (Error, "", text);
        if (text.StartsWith("Ulaşılamadı:", StringComparison.Ordinal)) return (Warning, "", text);
        if (text.StartsWith("uyarı:", StringComparison.OrdinalIgnoreCase))
        {
            var rest = text[6..].Trim();
            if (rest.Contains("Scan WARNINGS", StringComparison.OrdinalIgnoreCase)) return null;
            const string unread = "yedeklenemedi: ";
            if (rest.StartsWith(unread, StringComparison.Ordinal))
            {
                var body = rest[unread.Length..];
                var split = body.LastIndexOf(" : ", StringComparison.Ordinal);
                return split > 0 ? (Warning, body[..split], "Yedeklenemedi: " + body[(split + 3)..]) : (Warning, body, "Yedeklenemedi");
            }
            return (Warning, "", rest);
        }
        return null;
    }

    public const string GoTargets = "targets", GoJob = "job", GoRetry = "retry", GoHealth = "health", GoSettings = "settings";

    /// <summary>Hata iletisinden kullanıcının yapacağı işi ve çözümün bulunduğu yeri çıkarır.</summary>
    public static (string Text, string Go) Advice(string message)
    {
        bool Has(params string[] parts) => parts.Any(p => message.Contains(p, StringComparison.OrdinalIgnoreCase));
        if (Has("Hedef bağlı değil", "hedef tanımlı değil"))
            return ("İşin kullandığı hedef tanımlı değil. Hedefler sayfasında aynı adla tanımlayın ya da işin Hedef sekmesinden başka bir hedef seçin.", GoTargets);
        if (Has("yeniden tanımlayın", "oturumu geçersiz", "kabul edilmedi", "invalid_grant"))
            return ("Hedefin giriş bilgisi geçersiz. Hedefler sayfasında hedefi kaldırıp aynı adla yeniden tanımlayın.", GoTargets);
        if (Has("Drive API etkin değil"))
            return ("Google projenizde Drive API kapalı. Hedefler sayfasındaki tanımlama ekranından \"Drive API'yi etkinleştir\" düğmesiyle açın.", GoTargets);
        if (Has("depolama alanı dolu"))
            return ("Hedefte yer kalmadı. Hedefte yer açın ya da işin saklanacak yedek sayısını azaltın, sonra işi yeniden çalıştırın.", GoJob);
        if (Has("istek sınırı"))
            return ("Sağlayıcı geçici olarak yavaşlattı. Birkaç dakika bekleyip işi yeniden çalıştırın.", GoRetry);
        if (Has("fidye"))
            return ("Kaynak klasörlerde şifrelenme belirtisi görüldü. Dosyaları kontrol edin; yanlış alarmsa Ayarlar'daki korumayı gözden geçirin.", GoSettings);
        if (Has("bozuk", "parmak izi", "Hedefte bulunamadı"))
            return ("Yedek dosyası hedefte eksik ya da bozuk. İşi yeniden çalıştırıp yeni bir yedek alın, ardından Yedek Sağlığı denetimini çalıştırın.", GoHealth);
        if (Has("allocate", "bellek"))
            return ("Sıkıştırmaya bellek yetmedi. İşin sıkıştırma seviyesini düşürün ya da büyük klasörleri hariç tutun.", GoJob);
        if (Has("kullanıldığından", "kullanılıyor", "erişemiyor", "Erişim engellendi", "Yedeklenemedi"))
            return ("Dosya açık, kilitli ya da izniniz yok. Dosyayı kullanan programı kapatıp işi yeniden çalıştırın; dosya hep açıksa işin \"Hariç tutulacaklar\" alanına ekleyin.", GoJob);
        if (Has("kaynak bulunamadı", "klasör bulunamadı", "Kaynakların hiçbiri", "Kaynak klasörlerin hiçbiri"))
            return ("Kaynak klasör yerinde değil (taşınmış, silinmiş ya da disk takılı değil). İşin Kaynaklar sekmesini düzeltin.", GoJob);
        if (Has("zaman aşımı", "bağlantı", "kaldığı yerden", "yanıt vermedi"))
            return ("Bağlantı kesildi. İnternet bağlantısını kontrol edip işi yeniden çalıştırın; gönderim kaldığı yerden sürer.", GoRetry);
        return ("İşi yeniden çalıştırın. Sorun sürerse Canlı Günlük'te bu işin ayrıntılı kaydına bakın.", GoRetry);
    }

    private static string Now() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>Günlük satırını işler. Yedeği asla durdurmaz: defter yazılamazsa sessizce geçilir.</summary>
    public static void Capture(string kind, long refId, string name, string message, DateTime? runStarted = null)
    {
        try
        {
            var text = message.Trim();
            // Çalışma tüm öğeleri dolaşıp bitti: bu turda yeniden görülmeyen eski kayıtlar artık geçerli değildir.
            if (runStarted is { } since && text.StartsWith("Bitti:", StringComparison.Ordinal))
                Db.Exec("UPDATE problems SET resolved=$t WHERE resolved IS NULL AND kind=$k AND ref_id=$r AND time<$s",
                    ("$t", Now()), ("$k", kind), ("$r", refId), ("$s", since.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));
            if (ItemDone.Match(text) is { Success: true } done)
            {
                Db.Exec("UPDATE problems SET resolved=$t WHERE resolved IS NULL AND kind=$k AND ref_id=$r AND item=$i", ("$t", Now()), ("$k", kind), ("$r", refId), ("$i", done.Groups["item"].Value));
                return;
            }
            if (text == "Bitti: success")
            {
                Db.Exec("UPDATE problems SET resolved=$t WHERE resolved IS NULL AND kind=$k AND ref_id=$r", ("$t", Now()), ("$k", kind), ("$r", refId));
                return;
            }
            if (Classify(message) is not { } found) return;
            Db.Exec("INSERT INTO problems(time,kind,ref_id,name,item,level,message) VALUES($t,$k,$r,$n,$i,$l,$m)",
                ("$t", Now()), ("$k", kind), ("$r", refId), ("$n", name), ("$i", found.Item), ("$l", found.Level), ("$m", found.Message.Length > 1000 ? found.Message[..1000] : found.Message));
        }
        catch (Exception e) when (e is SqliteException or IOException or InvalidOperationException) { }
    }

    private static Problem Map(SqliteDataReader r) =>
        new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8));

    /// <summary>days 0 ise tüm kayıtlar; yoksa son N takvim günü (1 = yalnızca bugün).</summary>
    public static List<Problem> List(int days, bool onlyOpen)
    {
        var since = days > 0 ? DateTime.Today.AddDays(1 - days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "0000";
        return Db.Query($"SELECT id,time,kind,ref_id,name,item,level,message,resolved FROM problems WHERE time>=$s {(onlyOpen ? "AND resolved IS NULL" : "")} ORDER BY id DESC LIMIT 5000", Map, ("$s", since));
    }

    public static (int Errors, int Warnings) OpenCounts()
    {
        var rows = Db.Query("SELECT level, COUNT(*) FROM problems WHERE resolved IS NULL GROUP BY level", r => (r.GetString(0), r.GetInt32(1)));
        return (rows.Where(r => r.Item1 == Error).Sum(r => r.Item2), rows.Where(r => r.Item1 == Warning).Sum(r => r.Item2));
    }

    public static void Resolve(IEnumerable<long> ids)
    {
        foreach (var id in ids) Db.Exec("UPDATE problems SET resolved=$t WHERE id=$i AND resolved IS NULL", ("$t", Now()), ("$i", id));
    }

    public static int ResolveAll() => Db.Exec("UPDATE problems SET resolved=$t WHERE resolved IS NULL", ("$t", Now()));

    /// <summary>Kapanmış ve 90 günden eski kayıtları siler; açık sorun hiçbir zaman silinmez.</summary>
    public static int Prune() =>
        Db.Exec("DELETE FROM problems WHERE resolved IS NOT NULL AND time<$t", ("$t", DateTime.Today.AddDays(-90).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
}
