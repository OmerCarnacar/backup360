# Üçüncü taraf bileşenler

Backup360 aşağıdaki bileşenleri kullanır. Bu bileşenler Backup360 lisansına değil, kendi lisanslarına tabidir.

| Bileşen | Kullanım | Lisans | Kaynak |
|---|---|---|---|
| 7-Zip (`7z.exe`, `7z.dll`) | Sıkıştırma | GNU LGPL + unRAR kısıtı (`tools/7zip-License.txt`) | https://www.7-zip.org |
| rclone | Bulut aktarımı | MIT | https://rclone.org |
| SQLite, SQLitePCLRaw | Veritabanı | Public domain, Apache-2.0 | https://sqlite.org |
| Microsoft.Data.Sqlite, Microsoft.Data.SqlClient, .NET | Çalışma ortamı, SQL Server bağlantısı | MIT | https://github.com/dotnet |
| ZstdSharp | Zstandard sıkıştırma | MIT | https://github.com/oleg-st/ZstdSharp |
| MailKit, MimeKit | E-posta bildirimi | MIT | https://github.com/jstedfast/MailKit |

WinRAR ve MySQL istemci araçları Backup360 ile dağıtılmaz. `rar` formatı için lisanslı WinRAR, MySQL/MariaDB yedeği için bilgisayarda `mysqldump` gerekir.
