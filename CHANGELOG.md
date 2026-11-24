## 2026.11.24.1409

### Değişti
- **Yarıda kalan yedek kaldığı yerden sürer:** Bilgisayar kapanınca ya da iş kesilince yarım kalan yedek artık yalnızca zamanlanmış çalışmalarda değil, elle başlatılan ve zamanlanmamış işlerde de koruma görevi tarafından yeniden başlatılır. "Yalnızca değişenler" açıksa önceki çalışmada tamamlanan öğeler atlanır, iş kaldığı yerden devam eder. Kesinti 7 günden eskiyse ya da iş üst üste üç kez kesildiyse sürdürülmez
- Art arda kesinti sayısına artık başarılı çalışmalar da dahil edilmiyor; daha önce birkaç başarılı çalışmadan sonra kesilen iş yanlışlıkla sürdürülmeyebiliyordu
## 2026.11.24.0841

### Düzeltildi
- Hata iletilerinde hedefin adı iki kez yazılıyordu ("GOOGLE_OMER: GOOGLE_OMER: …"); artık bir kez yazılır
- Günlükteki "eski yedek silindi" ve "eski klasör silindi" satırları iş klasörünü de gösterir; yol artık yedeğin gerçek konumudur

## 2026.11.23.1019

### Eklendi
- **Tam + fark yedeği:** Sıkıştırılmış yedeklerde iş ayarlarından açılabilir. İlk çalışmada tam yedek alınır; sonraki çalışmalarda yalnızca yeni ve değişen dosyalar ile silinenlerin listesi alınır. Belirlenen gün sayısında bir (varsayılan 7) yeniden tam yedek alınır. Dayanılacak tam yedek silinmişse fark alınmaz, tam yedek alınır. Geri yüklemede fark seçildiğinde tam yedek ve fark birlikte açılır ve dosyalar o günkü hâline getirilir
- **Aynı günde tek yedek:** Açıksa gün içinde alınan önceki yedek, yenisi tamamlanınca silinir. "Şu kadar günden eskileri sil" ayarıyla birlikte günlük yedeklerin birikmesini önler
- **Çalışma günlüğü:** Her çalışmanın ayrı günlük dosyası tutulur. Geçmiş sayfasında bir satıra çift tıklayınca renkli günlük penceresi açılır. Günlük işi kimin başlattığını (kullanıcı, zamanlayıcı, telafi) ve bir özet satırını içerir
- **Yedek sürerken uyku engellenir:** Yedek ve denetim sürerken bilgisayar boşta uykuya geçmez
- Zamanlanmış saatte başlayıp kapanış ya da uyku yüzünden yarıda kalan iş, açılışta koruma görevi tarafından sürdürülür; art arda üç kez yarıda kalırsa denenmez
- Bilgisayar kapalıyken kaçan haftalık yedek denetimi de telafi edilir

### Düzeltildi
- **Denetimde yanlış "yedek bozuk" alarmı:** Saklama süresi dolduğu için silinmiş eski yedekler "eksik" sayılıp "yedek bozuk" alarmı veriyordu. Artık saklama süresini aşmış ve daha yeni yedeği olan dosyaların kayıtları kendiliğinden kaldırılır; bir fark yedeğinin dayandığı tam yedek hiçbir zaman bu kapsamda değildir. Gerçekten eksik olan yedek "bozuk" değil "uyarı" olarak bildirilir
- Silinmiş ve hiçbir işin kullanmadığı hedeflerin eski kayıtları denetimde temizlenir
- **Uykuda art arda hata:** Uyku ya da kapanış sırasında Windows'un sonlandırdığı 7-Zip her öğe için ayrı hata yazıyordu; çalışma artık tek satırla "yarıda kesildi" olarak kaydedilir, tamamlanan yedekler korunur
- Okunamayan özel dosyalar (yuva/socket) "yedeklenemedi" uyarısı üretmez
- Birebir gönderim ve eşitleme, kullanmadıkları geçici klasör için uyarı yazmaz

## 2026.11.16.1232

### Eklendi
- **Kaçan yedek telafi edilir:** Bilgisayar kapalı ya da uykudayken saati geçen günlük ve haftalık işler, bilgisayar açıldıktan sonra koruma görevi tarafından başlatılır. Zamanlama kaydedilmeden önceki saatler için iş başlatılmaz

### Düzeltildi
- **Yedeği silinmiş klasör yeniden alınır:** "Yalnızca değişenler" açıkken değişmeyen klasör, yerel hedefteki yedeği silinmiş olsa bile atlanıyor ve yedeksiz kalıyordu
- **Çalışma klasörü gönderilmez:** Yedek deposunun kendisi buluta gönderildiğinde uygulamanın geçici çalışma klasörü de taranıyor, iş "dosya bulunamadı" hatasıyla bitiyordu
- Listelendikten sonra kaynaktan silinen dosya gönderimi başarısız yapmaz; günlüğe "atlandı" diye yazılır
- Sıkıştırma sırasında kaynaktan silinen dosyalar (derleme çıktıları gibi) yüzlerce uyarı üretmez; günlüğe tek satır not düşülür
- Aynı anda çalışan işler günlük dosyasında birbirinin satırlarını ezmez

## 2026.11.13.1547

### Eklendi
- **Hatadan çözüme tek tık:** Pano'daki uyarı şeridi, durum kutusu ve iş kartlarındaki "Hata" etiketi Sorunlar sayfasını açar. Her sorun için ne yapılacağı yazar; "Çözüme git" ilgili sayfayı açar, "İşi yeniden çalıştır" işi başlatır

### Değişti
- **Klasör olarak yedekleme hızlandı:** Yerel klasöre ve ağ paylaşımına kopyalama Windows'un kendi kopyalamasıyla ve aynı anda 12 dosyayla yapılır; günlük dosyası her satırda yeniden açılmaz

### Düzeltildi
- **"Zamanlama kaydedilemedi" hatası:** Zamanlaması "Elle" olan iş her kaydedildiğinde boş iletili hata çıkıyordu; ayar kaydediliyor ama hata gösteriliyordu
- Uygulama kendi kilit dosyalarını yedekleyemediği için her çalışmada "uyarı" vermez
- Artık tekrarlamayan eski uyarılar, iş bir sonraki kez tamamlandığında Sorunlar'dan kendiliğinden kapanır
- Hariç tutulan klasörlerdeki değişiklik projeyi "değişmiş" saydırmaz; boşuna yeniden sıkıştırma yapılmaz
- İletisi boş gelen hatalar artık türüyle birlikte yazılır ve tam dökümü günlük dosyasına kaydedilir

## 2026.11.10.1600

### Değişti
- Pano yeniden düzenlendi: genel durum ve göstergeler tek satırda, iş durumu yalnızca dolu olanları gösteren etiketler halinde, grafik ve listeler daha sıkı

## 2026.11.10.1516

### Değişti: hedeflere doğrudan bağlantı
- **rclone kaldırıldı.** Google Drive, OneDrive, Dropbox, S3, SFTP, FTP, WebDAV, SMB ve klasör hedeflerine artık doğrudan kendi API'leri ve protokolleriyle bağlanılır; pakette aracı program yoktur
- **Tek tıkla giriş:** Kaydet'e basınca hesap onay sayfası kendiliğinden açılır. "Token al" düğmesi token'ı alıp alana yazar
- **Sayfa düğmeleri:** Client ID, uygulama kaydı ve erişim anahtarı için gereken sayfalar hedef ekranından açılır
- Google Drive, OneDrive ve Dropbox için kendi uygulama kimliğiniz gerekir (adımlar README'de)
- Hedef bilgileri veritabanında şifreli saklanır
- S3 hedefinde bucket, SMB hedefinde paylaşım adı ayrı alandır

### Yükseltme notu
- Önceki sürümde tanımlı bulut hedefleri (Google Drive dahil) **aynı adla yeniden tanımlanmalıdır**; işler değişmeden çalışır. Klasör hedefleri kendiliğinden aktarılır
- **Disk olarak bağlama (Z:) kaldırıldı**; bu özellik rclone'a bağlıydı

### Eklendi
- **Sorunlar sayfası:** Hatalar, uyarılar, yedeklenemeyen dosyalar ve buluta aktarılamayan öğeler gün gün listelenir; öğe sonradan başarıyla tamamlanınca kaydı kendiliğinden kapanır. Açık sorun varsa Pano uyarır
- Okunamayan (kilitli, izinsiz) dosyalar artık adıyla bildirilir
- **Yerel yedek ve buluta gönderim ayrı seçenekler:** İş yalnızca yerel klasöre, yalnızca buluta veya ikisine birden yedekleyebilir
- **Yeniden üretilebilen klasörleri atla:** İş ekranında tek tıkla `node_modules`, `.next`, `obj`, `.vs` gibi klasörler yedekten çıkarılır
- **Hazır Google bağlantısı:** Sürüme gömülü kimlik varsa kullanıcı Client ID girmeden "Kaydet ve giriş yap" ile bağlanır; indirilen kimlik dosyası kendiliğinden algılanır
- **Geçici klasör seçimi:** Sıkıştırma seçilen klasörde yapılır (Ayarlar)
- **Yerel yedek saklama:** Sıkıştırılmış yedekler geçici klasörde kalır, ayarlanan günden eskileri silinir; her öğenin en yeni kopyası korunur
- **Alt klasör yedekleri çalıştırma klasöründe:** Tüm arşivler `09-11-2026_17_10` biçimindeki tek klasöre yazılır
- Ana pencere tam ekran açılır

### Değişti
- Sıkıştırma ve gönderim birbirini beklemez; aynı anda iki arşiv gönderilir

### Düzeltildi
- **Yarıda kalan gönderim kaldığı bayttan sürer:** Google Drive'a büyük dosya gönderilirken bağlantı kopar ya da uygulama kapanırsa sonraki çalıştırma baştan başlamaz; yaklaşık 15 dakikaya kadar kesintide iş kendiliğinden bekleyip devam eder
- Pano daha kompakt: canlı işler yan yana, aşama şeridi ve yukarı kayan akış alanı animasyonlu
- Canlı kartlar tek düzende: altı sabit gösterge, saat:dakika:saniye süre biçimi, siyah üzerine yeşil akış alanı
- Yarıda kesilen yedek "çalışıyor" görünmeye devam etmez; yarım kalan arşiv yerel yedek klasörüne girmez ve kalıntıları temizlenir
- Bellek yetmediğinde 7-Zip hatası: Yedek iptal olmaz, daha az bellekle yeniden denenir

## 2026.11.09.1658

### Eklendi
- **Geri Yükle:** Tüm arşiv biçimleri, klasör yedekleri ve SQL Server, MySQL, PostgreSQL, Firebird, SQLite. Yedek önce doğrulanır, dosyalar yeni klasöre, veritabanları yeni adla açılır; SQL Server fark ve günlük yedeklerinde gereken zincir otomatik kurulur
- **Kartlarda gün bazlı özet:** Son 5 günün tarihi, saati, yedek adedi, boyutu ve durumu
- **Alt klasörleri ayrı gönderme:** Klasörün içindeki klasörler sırayla, ayrı arşiv ya da olduğu gibi gönderilir

## 2026.11.09.1611

### Eklendi
- **PostgreSQL ve Firebird** yedekleme
- **Fidye yazılımı koruması:** Şifrelenme belirtisi veya toplu değişimde yedek ve eşitleme durur, e-posta ve ekran uyarısı verilir
- **Yedek Sağlığı:** Hedefteki yedeklerin düzenli denetimi, eksik ve bozuk dosya bildirimi
- **Geri yükleme sınavı:** Yedek geçici veritabanına geri yüklenir, SQL Server'da `DBCC CHECKDB` çalışır
- **"Yedek bozuk" durumu:** Bozuk yedek gönderilmez, ayrı e-posta ve ekran uyarısı verilir
- SQL Server güvenlik uyarıları (`sa`, `xp_cmdshell`)
- SQL motor logoları, turuncu seçili menü, kartlarda hover efekti

### Önemli
Bu sürümde hazır Google bağlantısı yoktur. Google Drive için **kendi Client ID'nizi** girmeniz gerekir; adımlar README'de.

## 2026.11.09.1503

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
