# Yayıncı için bir kerelik adım: Google'dan indirilen kimlik dosyasını sürüme gömülecek biçime çevirir.
# Sonraki build.ps1 çıktısında kullanıcılar Client ID girmeden "Kaydet ve giriş yap" ile bağlanır.
# Kullanım: powershell -ExecutionPolicy Bypass -File set-google-client.ps1 -Json "C:\...\client_secret_....json"
# Kimlik ekrana yazılmaz; hedef dosya .gitignore içindedir, depoya girmez.
param([Parameter(Mandatory)][string]$Json)

$ErrorActionPreference = 'Stop'
$data = Get-Content -LiteralPath $Json -Raw -Encoding UTF8 | ConvertFrom-Json
if (-not $data.installed) { throw 'Bu dosya "Masaüstü uygulaması" türünde bir kimlik değil.' }
if (-not $data.installed.client_id -or -not $data.installed.client_secret) { throw 'Dosyada client_id veya client_secret yok.' }

$target = Join-Path $PSScriptRoot 'src\Backup360\google-client.txt'
[IO.File]::WriteAllText($target, "$($data.installed.client_id)`n$($data.installed.client_secret)`n", (New-Object Text.UTF8Encoding $false))
$ignored = git -C $PSScriptRoot check-ignore 'src/Backup360/google-client.txt'
if (-not $ignored) { throw 'google-client.txt .gitignore içinde değil; depoya girmemeli.' }
Write-Host 'Kimlik gömülmeye hazır. Şimdi build.ps1 çalıştırın.'
