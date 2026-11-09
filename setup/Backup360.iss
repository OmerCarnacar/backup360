; Backup360 kurulum paketi (Inno Setup). build.ps1 tarafından derlenir.
; Yönetici izni istemez: kullanıcı klasörüne kurulur, böylece data klasörü yazılabilir kalır.
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

[Setup]
AppId={{C1F6B4A2-7D3E-4E36-9A51-360BAC4B3600}
AppName=Backup360
AppVersion={#AppVersion}
AppVerName=Backup360 {#AppVersion}
AppPublisher=Ömer ÇARNAÇAR
AppPublisherURL=https://www.linkedin.com/in/omercarnacar/
AppSupportURL=https://github.com/OmerCarnacar/backup360/issues
AppUpdatesURL=https://github.com/OmerCarnacar/backup360/releases/latest
AppCopyright=Copyright (c) 2026 Ömer ÇARNAÇAR - Ücretsiz sürüm, para ile satılamaz
VersionInfoCompany=Ömer ÇARNAÇAR
VersionInfoDescription=Backup360 kurulum sihirbazı
VersionInfoVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\Backup360
DisableDirPage=yes
DisableProgramGroupPage=yes
DefaultGroupName=Backup360
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutDir}
OutputBaseFilename=Backup360-Setup-{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\assets\Backup360.ico
UninstallDisplayIcon={app}\Backup360.exe
UninstallDisplayName=Backup360
LicenseFile={#Stage}\LICENSE.txt
ShowLanguageDialog=no
CloseApplications=yes

[Languages]
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"

[Tasks]
Name: "desktopicon"; Description: "Masaüstüne kısayol oluştur"; Flags: checkedonce

[Files]
Source: "{#Stage}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{autoprograms}\Backup360"; Filename: "{app}\Backup360.exe"
Name: "{autodesktop}\Backup360"; Filename: "{app}\Backup360.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Backup360.exe"; Description: "Backup360'ı başlat"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Zamanlanmış görevler kaldırılır. data klasörü (veritabanı) kullanıcının verisidir ve silinmez.
Filename: "powershell.exe"; Parameters: "-NoProfile -NonInteractive -Command ""Get-ScheduledTask -TaskName 'Backup360-*' -ErrorAction SilentlyContinue | Unregister-ScheduledTask -Confirm:$false"""; Flags: runhidden; RunOnceId: "RemoveTasks"
