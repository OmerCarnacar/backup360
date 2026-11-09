<div align="center">

<img src="assets/logo.png" alt="Backup360" width="110">

# Backup360

**Dosya, klasör ve veritabanlarınızı en yüksek sıkıştırmayla buluta yedekler**
_Tüm arşiv formatları · SQL Server, Always On, MySQL, SQLite · Zamanlama · Canlı pano_

[![Son sürüm](https://img.shields.io/github/v/release/OmerCarnacar/backup360?label=s%C3%BCr%C3%BCm&color=4f46e5)](https://github.com/OmerCarnacar/backup360/releases/latest)
[![İndirme](https://img.shields.io/github/downloads/OmerCarnacar/backup360/total?label=indirme&color=0ea5e9)](https://github.com/OmerCarnacar/backup360/releases)
[![Lisans](https://img.shields.io/badge/lisans-Ücretsiz%20(satılamaz)-059669)](LICENSE)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11%20x64-0078d4)](#kurulum)

**[⬇ Son sürümü indirin](https://github.com/OmerCarnacar/backup360/releases/latest)**

</div>

---

## Ekran görüntüleri

<img src="docs/img/login.png" alt="Giriş ekranı" width="49%"> <img src="docs/img/pano-genel.png" alt="Pano" width="49%">
<img src="docs/img/isler.png" alt="Canlı ilerlemeli iş kartları" width="49%"> <img src="docs/img/is-gonderim.png" alt="Gönderim ayarı" width="49%">
<img src="docs/img/hedefler.png" alt="Hedefler" width="49%"> <img src="docs/img/gezgin.png" alt="Bulut Gezgini" width="49%">

## Özellikler

| | |
|---|---|
| 🗜️ **Tüm arşiv formatları** | 7z, zip, rar, tar, tar.gz, tar.bz2, tar.xz, tar.zst, wim. Aynı anda birden çok format seçilebilir. Varsayılan seviye en yüksektir. |
| 🛢️ **Veritabanı yedeği** | SQL Server (Full, Differential, Log), MySQL/MariaDB ve SQLite. Her yedek doğrulanır, sıkıştırılır ve gönderilir. |
| 🔎 **SQL Server taraması** | Bu bilgisayardaki ve yerel ağdaki SQL Server örneklerini listeler; varsayılan port 1433. |
| 🔁 **Always On** | Yedek tercih edilen replikadan alınır. İkincilde COPY_ONLY ve eşitleme kuralları otomatik uygulanır. |
| ☁️ **Çoklu hedef** | Birden çok Google Drive hesabı, OneDrive, Dropbox, yerel disk, USB veya ağ klasörü. |
| 📁 **Çoklu kaynak** | Dosyalar, klasörler, USB ve `\\SUNUCU\Paylasim` ağ alanları tek işte toplanabilir. |
| 📅 **Tarih bazlı gönderim** | Seçilen klasörün içindeki her klasör, sıkıştırılmış veya klasör olarak `2026-11-09/` gibi tarih klasörüne gönderilir. |
| 🗓️ **Gün sayısına göre** | Yalnızca son N günde değişen klasörleri gönderin, N günden eski yedekleri otomatik silin. Her öğenin en yeni yedeği korunur. |
| 📂 **Klasör bazlı (varsayılan)** | Dosya ve klasörler olduğu gibi, aynı yapıyla gönderilir. "Sıkıştırarak gönder" seçilirse arşivlenir. |
| 🔄 **Klasör ve SQL eşitleme** | Yedekleri başka klasöre, NAS'a veya farklı PC'ye yalnızca farkları aktararak eşitler. "Farkları göster" ile önce görebilirsiniz. |
| 🗂️ **Bulut Gezgini** | Bağlı hedeflerin içeriğini gezin, dosya ve klasör indirin. |
| 💽 **Disk olarak bağlama** | Hedefi `Z:` gibi bir sürücü harfiyle bağlayın (WinFsp gerekir). |
| 📈 **Anlık takip** | Sıkıştırma ve yükleme yüzdesi, o anki işlem, dosya dosya akış ve Canlı Günlük. |
| 🗂️ **Gruplar** | İşleri ve kuralları gruplayın, bir grubu tek tıkla sırayla çalıştırın. |
| ⏰ **Zamanlama** | Dakika, saat, gün veya hafta bazlı. Uygulama kapalıyken de çalışır, kaçırılan görev bilgisayar açılınca yapılır. |
| 🚚 **Klasör taşıma** | Bir klasöre yeni gelen dosyaları bulup diğer klasöre taşır. Yazılmakta olan ve açık dosyaları bekler. |
| 📊 **Pano** | Günlük, haftalık ve aylık aktarım grafiği, iş durumu, en büyük ve en uzun işler, canlı aktarım ve zamanlama geri sayımı. |
| 🧪 **Bağlantı testi ve süre hesabı** | Hedefin gecikmesini ve gerçek yükleme hızını ölçer, işin tahmini aktarım süresini hesaplar. |
| 📧 **E-posta bildirimi** | Hata olduğunda SMTP ile e-posta gönderir. Uyarı ve başarı bildirimi isteğe bağlıdır. |
| ✅ **Doğrulama** | Arşiv göndermeden önce test edilir, yüklenen dosya özet değeriyle karşılaştırılır. |
| 🔐 **Giriş ve şifreleme** | Parolalı giriş ekranı. 7z, zip ve rar arşivleri parolayla şifrelenebilir. |
| 🗄️ **Taşınabilir SQLite** | Tüm ayarlar uygulama yanındaki `data` klasöründe. Klasörü kopyalamak yeterli. |
| 🆓 **Ücretsiz** | Lisans anahtarı yok, dosya veya boyut sınırı yok. |

## Kurulum

1. **[Son sürümden](https://github.com/OmerCarnacar/backup360/releases/latest)** `Backup360-Setup-….exe` dosyasını indirip çalıştırın. Yönetici izni gerekmez; Python, .NET veya başka bir program kurmanız gerekmez.
2. Açılışta hesabınızı oluşturun.
3. **Hedefler** sayfasından **Yeni hedef tanımla** ile Google Drive ekleyin ([kendi Client ID'niz](#google-client-id) ile) ve tarayıcıda hesabınızı onaylayın. SFTP, FTP, WebDAV, SMB ve S3 için şifre veya anahtar girebilirsiniz.
4. **Yedekleme İşleri** sayfasından **Yeni iş** oluşturun.

Kurulum istemiyorsanız `Backup360-Portable-….zip` dosyasını açıp `Backup360.exe` dosyasını çalıştırın.

## Bilmeniz gerekenler

- **Google Drive izni:** Varsayılan `drive.file` kapsamında uygulama yalnızca kendi yüklediği dosyaları görür. Tüm Drive'ı Gezgin'de görmek için hedef tanımlarken "tüm dosyalara eriş" seçeneğini işaretleyin.
- **Disk olarak bağlama:** Windows'a ücretsiz [WinFsp](https://winfsp.dev/rel/) kurulu olmalıdır. Bağlantı uygulama açıkken sürer.
- **Eşitleme farkı dosya düzeyindedir:** Değişen dosyanın tamamı yeniden kopyalanır.
- **SQL Server yedek klasörü:** SQL Server servis hesabının yazabildiği, uygulamanın da okuyabildiği bir klasör olmalıdır. Uzak sunucuda ortak bir ağ yolu (UNC) kullanın.
- **Always On:** Tercih edilen replikaya göre yedek almak için işi her replikada kurun. İkincil replikada Differential yedek alınamaz; SQL Server bunu desteklemez.
- **Yedek zinciri:** Log yedekleri günlük zincirini etkiler. Başka bir yedekleme aracı kullanıyorsanız yalnızca COPY_ONLY tam yedek alın.
- **MySQL/MariaDB:** Bilgisayarda `mysqldump` kurulu olmalıdır.
- **RAR:** RAR arşivini yalnızca WinRAR oluşturabilir. Lisanslı WinRAR varsa `rar` seçeneği görünür.
- **Parola desteği:** Arşiv parolası 7z, zip ve rar için geçerlidir. Diğer formatlarla birlikte seçilirse iş kaydedilmez; böylece şifresiz gönderim yanlışlıkla olmaz.
- **Zamanlama:** Görevler Windows oturumunuz açıkken çalışır.
- **Başka bilgisayara taşıma:** İşler, kurallar ve hedefler taşınır. Parolalar Windows hesabına bağlı olduğundan yeni bilgisayarda yeniden girilir.
- **Bulut oturum bilgileri** `data\rclone.conf` dosyasında durur; `data` klasörünü güvenli tutun.
- **Parolayı unuttuysanız:** `Backup360.exe --reset-login` giriş hesaplarını sıfırlar, işleriniz korunur.

## Google Client ID

Google Drive için **kendi Client ID'niz gereklidir**. rclone'un ortak kimliği kota sınırına takılıyor ve kapatılıyor; ücretsiz ve birkaç dakikalık bir işlemdir:

1. [Google Cloud Console](https://console.cloud.google.com/)'da yeni bir proje açın.
2. **APIs & Services → Library** bölümünden **Google Drive API**'yi etkinleştirin.
3. **OAuth consent screen**'de **External** seçin, uygulamayı **In production** durumuna alın (test modunda oturum 7 günde düşer).
4. **Credentials → Create Credentials → OAuth client ID** ile **Desktop app** oluşturun.
5. Client ID ve Client Secret değerlerini **Yeni hedef tanımla** ekranına yapıştırın.

## Geliştirme

```powershell
dotnet build tests/Backup360.Tests/Backup360.Tests.csproj
powershell -ExecutionPolicy Bypass -File build.ps1
```

Gereksinimler: .NET 10 SDK, 7-Zip, Inno Setup 6.

## Lisans

Ücretsizdir; kişisel ve kurumsal ortamlarda kullanılabilir, para karşılığı satılamaz. Tam metin: **[LICENSE](LICENSE)** · Üçüncü taraf bileşenler: **[THIRD-PARTY.md](THIRD-PARTY.md)**

Yazılım geliştiriciye hiçbir veri göndermez.

---

<div align="center">

**Ömer ÇARNAÇAR**

[omer.carnacar@outlook.com.tr](mailto:omer.carnacar@outlook.com.tr) · [LinkedIn](https://www.linkedin.com/in/omercarnacar/) · [Twitter](https://twitter.com/omer_carnacar) · [GitHub](https://github.com/OmerCarnacar) · [Sürümler](https://github.com/OmerCarnacar/backup360/releases)

<sub>Faydalı bulduysanız ⭐ vermeyi unutmayın.</sub>

</div>
