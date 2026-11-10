# Backup360 Gizlilik Politikası

Son güncelleme: 10 Kasım 2026

Backup360, dosya ve veritabanlarınızı kendi seçtiğiniz hedeflere yedekleyen ücretsiz bir Windows uygulamasıdır. Bu sayfa uygulamanın verilerinizle ne yaptığını açıklar.

## Özet

- Backup360 geliştiriciye veya üçüncü bir tarafa **hiçbir veri göndermez**. Kullanım istatistiği, hata raporu veya reklam yoktur.
- Uygulamanın bir sunucusu yoktur. Tüm işlemler sizin bilgisayarınızda yapılır; dosyalarınız bilgisayarınızdan doğrudan sizin hesabınıza gider.
- Hesap bilgileriniz yalnızca kendi bilgisayarınızda, Windows hesabınıza bağlı olarak şifreli saklanır.

## Google hesabınıza erişim

Google Drive hedefi tanımladığınızda Google'ın kendi oturum açma sayfasında izin verirsiniz. Backup360 parolanızı görmez.

**İstenen izin.** Uygulama varsayılan olarak yalnızca `drive.file` iznini ister: yalnızca Backup360'ın kendi oluşturduğu dosya ve klasörleri görebilir. Drive'ınızdaki diğer dosyalara erişemez.

**Erişilen veri ve kullanım amacı.** Google kullanıcı verileri yalnızca sizin başlattığınız şu işlemler için kullanılır:

- Yedek dosyalarını Drive'ınıza yüklemek
- Yedekleri listelemek, doğrulamak, indirmek ve geri yüklemek
- Belirlediğiniz saklama kuralına göre eski yedekleri Drive çöp kutusuna taşımak

**Saklama.** Google'ın verdiği oturum anahtarları (token) yalnızca bilgisayarınızdaki uygulama veritabanında, Windows DPAPI ile şifreli tutulur. Geliştiriciye veya başka bir yere iletilmez.

**Paylaşım.** Google kullanıcı verileri hiç kimseyle paylaşılmaz, satılmaz, reklam veya yapay zekâ eğitimi için kullanılmaz. İnsanlar tarafından okunmaz; uygulamanın geliştiricisinin bu verilere erişimi yoktur.

Backup360'ın Google API'lerinden aldığı bilgileri kullanması ve aktarması, Sınırlı Kullanım (Limited Use) koşulları dahil [Google API Hizmetleri Kullanıcı Verileri Politikası](https://developers.google.com/terms/api-services-user-data-policy)'na uygundur.

## Diğer hedefler

OneDrive, Dropbox, S3, SFTP, FTP, WebDAV ve ağ klasörü hedeflerinde de aynı ilke geçerlidir: bağlantı bilgileri yalnızca bilgisayarınızda şifreli saklanır, dosyalar doğrudan sizin hedefinize gider.

## E-posta bildirimleri

E-posta bildirimini açarsanız iletiler, sizin girdiğiniz SMTP sunucusu üzerinden sizin belirlediğiniz adreslere gönderilir. İletiler iş adı, sonuç ve hata özetini içerir; dosya içeriği içermez.

## Erişimi kaldırma ve verileri silme

- Uygulamada **Hedefler** sayfasından hedefi kaldırdığınızda saklanan oturum anahtarı silinir.
- Google hesabınızdan erişimi istediğiniz an [myaccount.google.com/permissions](https://myaccount.google.com/permissions) sayfasından kaldırabilirsiniz.
- Uygulamayı kaldırıp `data` klasörünü sildiğinizde bilgisayarınızda uygulamaya ait hiçbir veri kalmaz. Hedefteki yedekleriniz sizindir ve siz silmedikçe yerinde kalır.

## İletişim

Ömer ÇARNAÇAR · [omer.carnacar@outlook.com.tr](mailto:omer.carnacar@outlook.com.tr)

---

# Backup360 Privacy Policy

Last updated: November 10, 2026

Backup360 is a free Windows application that backs up your files and databases to destinations you choose.

- Backup360 **sends no data** to its developer or to any third party. There is no telemetry, analytics or advertising, and the application has no server: everything runs on your computer and your files travel directly from your computer to your own account.
- **Google user data.** When you add a Google Drive destination you grant access on Google's own sign-in page. By default the app requests only the `drive.file` scope, so it can see only the files and folders it created itself. This access is used solely to upload your backups, list, verify, download and restore them, and move old backups to the Drive trash according to the retention rule you set.
- **Storage.** OAuth tokens are stored only in the local application database on your computer, encrypted with Windows DPAPI. They are never transmitted to the developer.
- **Sharing.** Google user data is not shared, sold, used for advertising or used to train AI models, and is not read by humans. The developer has no access to it.
- Backup360's use and transfer of information received from Google APIs adheres to the [Google API Services User Data Policy](https://developers.google.com/terms/api-services-user-data-policy), including the Limited Use requirements.
- **Revoking access.** Remove the destination in the app to delete the stored token, or revoke access at any time at [myaccount.google.com/permissions](https://myaccount.google.com/permissions). Deleting the app's `data` folder removes all locally stored data.

Contact: Ömer ÇARNAÇAR · [omer.carnacar@outlook.com.tr](mailto:omer.carnacar@outlook.com.tr)
