## Yeni sürüm

### Eklendi
- **Gelişmiş pano:** Günlük, haftalık ve aylık aktarım grafiği; geçerli iş durumu; en büyük ve en uzun süren işler
- **SQL Server taraması:** Bu bilgisayardaki ve yerel ağdaki örnekleri listeleme; varsayılan port 1433
- **Gün bazlı yedekte klasör yapısı:** İş / Tarih / Klasör / Alt klasörler
- **Daha hızlı aktarım:** Aynı anda 8 dosya
- Tablolarda ızgara görünümü, ekrana göre boyutlanan pencereler

### Düzeltildi
- Uygulama kapatıldığında sürecin görünmez halde açık kalması
- Paylaşılan ortak Google kimliği artık kullanılmıyor

### Önemli
Bu sürümde hazır Google bağlantısı yoktur. Google Drive için **kendi Client ID'nizi** girmeniz gerekir; adımlar README'de.

## 2026.11.09.1409

### Eklendi
- **WPF arayüz:** Windows 11 görünümü, koyu/açık tema, vurgu renkleri, gruplu yan menü
- **Ayrı bölümler:** Klasör Yedekleme, Klasör Eşitleme, SQL Yedekleme, SQL Eşitleme
- **Eşitleme:** Başka klasöre, NAS'a veya farklı PC'ye yalnızca farkları aktarma, "Farkları göster"
- **Bulut Gezgini:** İçeriği gezme, indirme, klasör oluşturma
- **Disk olarak bağlama:** Hedefi sürücü harfiyle bağlama (WinFsp gerekir)
- **Anlık takip:** Kartlarda sıkıştırma ve yükleme yüzdesi, o anki işlem, dosya dosya akış; Canlı Günlük sayfası
- **Hedef tanımlama:** Tarayıcı, token, anahtar veya şifre ile; SFTP, FTP, WebDAV, SMB, S3; bulut logoları
- **Klasör bazlı yedek varsayılan oldu;** sıkıştırma yalnızca seçilirse yapılır

### Düzeltildi
- Google Drive yedeklerinin "hata" ile bitmesi
- Türkçe karakterli dosya ve iş adlarının bozulması
- Kota hatasında sessizce sonsuz bekleme: artık hızlıca ve anlaşılır biçimde bildiriliyor

### Önemli
Google Drive için **kendi Client ID'niz gereklidir**; rclone'un ortak kimliği kota sınırına takılıyor. Adımlar README'de.

## İlk sürüm (2026.11.09.1153)

- **9 arşiv formatı:** 7z, zip, rar, tar, tar.gz, tar.bz2, tar.xz, tar.zst, wim; varsayılan olarak en yüksek sıkıştırma
- **Gönderim modları:** tek arşiv, alt klasörleri tarih bazlı gönderim (sıkıştırılmış veya klasör olarak), klasör eşitleme
- **Veritabanı yedeği:** SQL Server (Full, Differential, Log, Always On), MySQL/MariaDB, SQLite
- **Çoklu kaynak ve hedef:** Google Drive, OneDrive, Dropbox, yerel ve ağ klasörleri
- **Gün bazlı kurallar:** son N günde değişenleri gönderme, N günden eski yedekleri silme
- **Gruplar:** işleri gruplama ve grubu sırayla çalıştırma
- **Zamanlama:** Windows Görev Zamanlayıcı ile dakika, saat, gün ve hafta bazlı
- **Klasör taşıma:** yeni gelen dosyaları diğer klasöre otomatik taşıma
- **Canlı pano:** aşama animasyonu, hız, kalan süre, zamanlama geri sayımı
- **Bağlantı testi ve aktarım süresi hesabı**
- **Hatalarda SMTP e-posta bildirimi**
- Giriş ekranı, taşınabilir SQLite veritabanı, kurulum sihirbazı ve portable ZIP

Doğrulama: indirdiğiniz dosyaları `SHA256SUMS.txt` ile karşılaştırabilirsiniz.
