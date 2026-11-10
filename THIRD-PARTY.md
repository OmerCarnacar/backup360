# Üçüncü taraf bileşenler

Backup360 aşağıdaki bileşenleri kullanır. Bu bileşenler Backup360 lisansına değil, kendi lisanslarına tabidir.

| Bileşen | Kullanım | Lisans | Kaynak |
|---|---|---|---|
| 7-Zip (`7z.exe`, `7z.dll`) | Sıkıştırma | GNU LGPL + unRAR kısıtı (`tools/7zip-License.txt`) | https://www.7-zip.org |
| AWS SDK for .NET (S3) | S3 uyumlu depolama | Apache-2.0 | https://github.com/aws/aws-sdk-net |
| SSH.NET | SFTP | MIT | https://github.com/sshnet/SSH.NET |
| FluentFTP | FTP ve FTPS | MIT | https://github.com/robinrodricks/FluentFTP |
| SQLite, SQLitePCLRaw | Veritabanı | Public domain, Apache-2.0 | https://sqlite.org |
| Microsoft.Data.Sqlite, Microsoft.Data.SqlClient, .NET | Çalışma ortamı, SQL Server bağlantısı | MIT | https://github.com/dotnet |
| ZstdSharp | Zstandard sıkıştırma | MIT | https://github.com/oleg-st/ZstdSharp |
| MailKit, MimeKit | E-posta bildirimi | MIT | https://github.com/jstedfast/MailKit |
| WPF-UI | Fluent arayüz bileşenleri | MIT | https://github.com/lepoco/wpfui |
| LiveCharts2, SkiaSharp | Grafikler | MIT | https://github.com/beto-rodriguez/LiveCharts2 |
| Simple Icons | Veritabanı logoları | CC0 | https://simpleicons.org |

Google Drive, OneDrive ve Dropbox'a bu hizmetlerin kendi API'leri ile doğrudan bağlanılır. WinRAR ve MySQL, PostgreSQL, Firebird istemci araçları Backup360 ile dağıtılmaz. Ürün logoları ilgili şirketlerin ticari markalarıdır ve yalnızca hizmeti tanıtmak için kullanılır. `rar` formatı için lisanslı WinRAR, MySQL/MariaDB yedeği için bilgisayarda `mysqldump` gerekir.
