# Backup360 yayın paketini üretir: dist\Backup360-Setup-<sürüm>.exe, portable ZIP ve SHA256SUMS.txt
# Kullanım: powershell -ExecutionPolicy Bypass -File build.ps1 [-Version 2026.11.09.1200]
param([string]$Version = (Get-Date -Format 'yyyy.MM.dd.HHmm'))

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dist = Join-Path $root 'dist'
$stage = Join-Path $dist 'stage'
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Force (Join-Path $stage 'tools') | Out-Null

Write-Host '== Uygulama derleniyor'
dotnet publish (Join-Path $root 'src\Backup360\Backup360.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -p:BuildStamp=$Version -o (Join-Path $dist 'publish') -v q -nologo
if ($LASTEXITCODE -ne 0) { throw 'Derleme başarısız' }
Copy-Item (Join-Path $dist 'publish\Backup360.exe') $stage

Write-Host '== Araçlar ekleniyor'
$sevenZip = Join-Path $env:ProgramFiles '7-Zip'
if (-not (Test-Path (Join-Path $sevenZip '7z.exe'))) { throw '7-Zip kurulu değil' }
Copy-Item (Join-Path $sevenZip '7z.exe'), (Join-Path $sevenZip '7z.dll') (Join-Path $stage 'tools')
Copy-Item (Join-Path $sevenZip 'License.txt') (Join-Path $stage 'tools\7zip-License.txt')

$rcloneZip = Join-Path $dist 'rclone.zip'
Invoke-WebRequest 'https://downloads.rclone.org/rclone-current-windows-amd64.zip' -OutFile $rcloneZip
$rcloneDir = Join-Path $dist 'rclone'
Expand-Archive $rcloneZip $rcloneDir
$extracted = Get-ChildItem $rcloneDir -Directory | Select-Object -First 1
Copy-Item (Join-Path $extracted.FullName 'rclone.exe') (Join-Path $stage 'tools')
Copy-Item (Join-Path $extracted.FullName 'git-log.txt') (Join-Path $stage 'tools\rclone-git-log.txt') -ErrorAction SilentlyContinue

Copy-Item (Join-Path $root 'LICENSE') (Join-Path $stage 'LICENSE.txt')
Copy-Item (Join-Path $root 'THIRD-PARTY.md') (Join-Path $stage 'THIRD-PARTY.md')

Write-Host '== Kurulum sihirbazı'
$iscc = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'
if (-not (Test-Path $iscc)) { throw 'Inno Setup 6 kurulu değil' }
& $iscc /Q "/DAppVersion=$Version" "/DStage=$stage" "/DOutDir=$dist" (Join-Path $root 'setup\Backup360.iss')
if ($LASTEXITCODE -ne 0) { throw 'Kurulum paketi üretilemedi' }

Write-Host '== Portable ZIP'
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath (Join-Path $dist "Backup360-Portable-$Version.zip") -CompressionLevel Optimal

$files = Get-ChildItem $dist -File | Where-Object { $_.Name -like 'Backup360-*' }
$files | ForEach-Object { "$((Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower())  $($_.Name)" } |
    Set-Content (Join-Path $dist 'SHA256SUMS.txt') -Encoding ascii

Remove-Item $stage, (Join-Path $dist 'publish'), $rcloneDir, $rcloneZip -Recurse -Force
Get-ChildItem $dist | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize
