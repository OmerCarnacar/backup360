using Backup360.Core;
using Backup360.UI;
using Xunit;

namespace Backup360.Tests;

public class ProblemsTests
{
    [Theory]
    [InlineData("[Proje.7z] HATA: 7-Zip hata verdi (kod 8)", "error", "Proje.7z", "7-Zip hata verdi (kod 8)")]
    [InlineData("HATA: Hedef bağlı değil: Google", "error", "", "Hedef bağlı değil: Google")]
    [InlineData("HATA (drive): Hesap onayı 5 dakika içinde gelmedi.", "error", "", "Hesap onayı 5 dakika içinde gelmedi.")]
    [InlineData("  HATA: Alt/rapor.pdf: Bağlantı yanıt vermedi (zaman aşımı).", "error", "Alt/rapor.pdf", "Bağlantı yanıt vermedi (zaman aşımı).")]
    [InlineData("  SORUN gdrive:Yedek/A_20260101-000000.7z: Hedefte bulunamadı", "error", "gdrive:Yedek/A_20260101-000000.7z", "Hedefte bulunamadı")]
    [InlineData(@"  uyarı: yedeklenemedi: D:\Veri\acik.xlsx : Dosya kullanılıyor.", "warning", @"D:\Veri\acik.xlsx", "Yedeklenemedi: Dosya kullanılıyor.")]
    [InlineData("  uyarı: kaynak bulunamadı: D:\\Yok", "warning", "", "kaynak bulunamadı: D:\\Yok")]
    [InlineData("DURDURULDU, fidye yazılımı şüphesi: 40 dosya", "error", "", "DURDURULDU, fidye yazılımı şüphesi: 40 dosya")]
    public void Log_lines_are_classified(string line, string level, string item, string message) =>
        Assert.Equal((level, item, message), Problems.Classify(line));

    [Theory]
    [InlineData("[A.7z] sıkıştırılıyor...")]
    [InlineData("  Yeni: A_20260101-000000.7z")]
    [InlineData("  uyarı: Scan WARNINGS: 1")]
    [InlineData("Bitti: success")]
    [InlineData("  değişmedi, atlandı: HATALAR klasörü")]
    public void Ordinary_lines_are_not_problems(string line) => Assert.Null(Problems.Classify(line));

    [Fact]
    public void Problem_closes_when_the_same_item_succeeds_and_all_close_when_the_run_succeeds()
    {
        using var s = new Sandbox();
        Problems.Capture("job", 1, "İş", "[A.7z] HATA: yükleme başarısız");
        Problems.Capture("job", 1, "İş", "[B.7z] HATA: yükleme başarısız");
        Problems.Capture("job", 2, "Başka", "[A.7z] HATA: başka işin hatası");
        Assert.Equal((3, 0), Problems.OpenCounts());

        Problems.Capture("job", 1, "İş", "[A.7z] tamam.");
        Assert.Equal(new[] { "A.7z", "B.7z" }, Problems.List(0, true).Select(p => p.Item).OrderBy(i => i));
        Assert.Equal(3, Problems.List(1, false).Count);

        Problems.Capture("job", 1, "İş", "Bitti: success");
        var open = Problems.List(0, true);
        Assert.Equal("Başka", Assert.Single(open).Name);

        Problems.Resolve(open.Select(p => p.Id));
        Assert.Equal((0, 0), Problems.OpenCounts());
        Assert.Equal(0, Problems.Prune());
    }

    [Fact]
    public void Open_items_are_grouped_per_job_and_item_with_errors_first()
    {
        using var s = new Sandbox();
        Problems.Capture("job", 1, "İş", "  uyarı: kaynak bulunamadı: D:\\Yok");
        Problems.Capture("job", 1, "İş", "[A.7z] HATA: ilk");
        Problems.Capture("job", 1, "İş", "[A.7z] HATA: ikinci");
        var rows = ProblemsPage.GroupOpen(Problems.List(0, true));
        Assert.Equal(2, rows.Count);
        Assert.Equal(("Hata", "A.7z", "2", "ikinci"), (rows[0].Level, rows[0].Item, rows[0].Count, rows[0].Message));
        Assert.Equal(("Uyarı", "(işin tamamı)"), (rows[1].Level, rows[1].Item));
    }

    [Theory]
    [InlineData("Hedef bağlı değil: Google", "targets", "aynı adla tanımlayın")]
    [InlineData("GOOGLE_OMER: Hesap oturumu geçersiz. Hedefi kaldırıp yeniden tanımlayın.", "targets", "yeniden tanımlayın")]
    [InlineData("gdrive: Hedefin depolama alanı dolu.", "job", "yer açın")]
    [InlineData("Yedeklenemedi: Dosya başka bir işlem tarafından kullanıldığından bu işlem dosyaya erişemiyor.", "job", "Hariç tutulacaklar")]
    [InlineData("7-Zip hata verdi (kod 8): ERROR: Can't allocate required memory!", "job", "seviyesini düşürün")]
    [InlineData("Bağlantı uzun süre kurulamadı; gönderim sonraki çalıştırmada kaldığı yerden sürer.", "retry", "kaldığı yerden")]
    [InlineData("kaynak bulunamadı: E:\\Projeler", "job", "Kaynaklar sekmesini")]
    [InlineData("Parmak izi değişmiş: dosya bozulmuş veya değiştirilmiş", "health", "Yedek Sağlığı")]
    [InlineData("bilinmeyen bir şey oldu", "retry", "yeniden çalıştırın")]
    public void Every_problem_gets_a_concrete_next_step(string message, string go, string expected)
    {
        var (text, where) = Problems.Advice(message);
        Assert.Equal(go, where);
        Assert.Contains(expected, text);
    }

    [Fact]
    public void Solution_page_depends_on_the_problem_and_the_job_type()
    {
        using var s = new Sandbox();
        var folder = new Job { Name = "Klasor", Mode = "archive", Sources = new() { s.Root }, Formats = new() { "zip" }, Targets = new() { "gdrive" } };
        var sql = new Job { Name = "Sql", Mode = "database", DbEngine = "sqlite", DbNames = new() { "x" }, Formats = new() { "zip" }, Targets = new() { "gdrive" } };
        Db.Save(folder);
        Db.Save(sql);
        Problems.Capture("job", folder.Id, folder.Name, "HATA: Hedef bağlı değil: Google");
        Problems.Capture("job", sql.Id, sql.Name, "[erp.zip] HATA: 7-Zip hata verdi: Can't allocate required memory");
        Problems.Capture("job", folder.Id, folder.Name, @"  uyarı: yedeklenemedi: D:\a.xlsx : Dosya kullanılıyor.");
        var rows = ProblemsPage.GroupOpen(Problems.List(0, true));
        Type PageOf(string item) => ProblemsPage.PageFor(rows.Single(r => r.Item == item));
        Assert.Equal(typeof(TargetsPage), PageOf("(işin tamamı)"));
        Assert.Equal(typeof(SqlBackupPage), PageOf("erp.zip"));
        Assert.Equal(typeof(FolderBackupPage), PageOf(@"D:\a.xlsx"));
        Assert.All(rows, r => Assert.False(string.IsNullOrWhiteSpace(r.Advice)));
    }

    [Fact]
    public void Error_without_a_message_is_still_described()
    {
        Assert.Contains("InvalidOperationException", RunLog.Describe(new InvalidOperationException("")));
        Assert.Equal("disk dolu", RunLog.Describe(new IOException("disk dolu")));
        var (level, _, message) = Problems.Classify("HATA: " + RunLog.Describe(new InvalidOperationException(""))).Value;
        Assert.Equal(Problems.Error, level);
        Assert.NotEqual("", message);
    }

    [Fact]
    public void Seven_zip_output_lists_the_files_it_could_not_read()
    {
        const string output = "Scanning the drive:\nWARNING: Sistem dosyaya erişemiyor.\nD:\\Veri\\kilitli.db\n\n12 files, 34 bytes\n\nScan WARNINGS for files and folders:\n\nD:\\Veri\\kilitli.db : Sistem dosyaya erişemiyor.\nD:\\Veri\\Alt\\izin yok.txt : Erişim engellendi.\n----------------\nScan WARNINGS: 2\n";
        Assert.Equal(new[] { @"D:\Veri\kilitli.db : Sistem dosyaya erişemiyor.", @"D:\Veri\Alt\izin yok.txt : Erişim engellendi." }, Archiver.UnreadFiles(output));
        Assert.Empty(Archiver.UnreadFiles("Everything is Ok"));
    }

    [Fact]
    public async Task Locked_file_is_reported_by_name_and_cleared_after_a_clean_run()
    {
        using var s = new Sandbox();
        var src = s.Tree("Belgeler");
        var locked = Path.Combine(src, "acik-defter.xlsx");
        File.WriteAllText(locked, "kilitli");
        var job = new Job { Name = "Kilitli", Mode = "archive", Sources = new() { src }, Formats = new() { "zip" }, Targets = new() { "gdrive" } };
        Db.Save(job);

        using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var r = await BackupRunner.RunAsync(job.Id, null, default);
            Assert.Equal("warning", r.Status);
        }
        var open = Assert.Single(Problems.List(0, true));
        Assert.Equal(Problems.Warning, open.Level);
        Assert.EndsWith("acik-defter.xlsx", open.Item);
        Assert.StartsWith("Yedeklenemedi", open.Message);

        await Task.Delay(1100);
        Assert.Equal("success", (await BackupRunner.RunAsync(job.Id, null, default)).Status);
        Assert.Equal((0, 0), Problems.OpenCounts());
        Assert.Single(Problems.List(1, false));
    }

    [Fact]
    public async Task Failed_upload_is_listed_as_an_item_that_did_not_reach_the_target()
    {
        using var s = new Sandbox();
        var job = new Job { Name = "Ulasmayan", Mode = "archive", Sources = new() { s.Tree("V") }, Formats = new() { "zip" }, Targets = new() { "gdrive" } };
        Db.Save(job);
        // Hedef klasörün yerine dosya konur: yazılamaz.
        Directory.Delete(s.Cloud1, true);
        File.WriteAllText(s.Cloud1, "klasor degil");
        var r = await BackupRunner.RunAsync(job.Id, null, default);
        Assert.Equal("failed", r.Status);
        var row = Assert.Single(ProblemsPage.GroupOpen(Problems.List(0, true)));
        Assert.Equal(("Hata", "Ulasmayan", "Ulasmayan.zip"), (row.Level, row.Job, row.Item));

        File.Delete(s.Cloud1);
        Directory.CreateDirectory(s.Cloud1);
        Assert.Equal("success", (await BackupRunner.RunAsync(job.Id, null, default)).Status);
        Assert.Equal((0, 0), Problems.OpenCounts());
    }
}
