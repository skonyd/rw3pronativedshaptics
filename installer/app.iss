; RW3 Pro Native DS Haptics — per-user installer.
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\publish"
#endif

[Setup]
AppId={{2FA6D4CA-C13B-4565-9ABB-8078D74ED1E6}
AppName=RW3 Pro Native DS Haptics
AppVersion={#AppVersion}
AppPublisher=rw3pronativedshaptics
AppPublisherURL=https://github.com/skonyd/rw3pronativedshaptics
DefaultDirName={localappdata}\Programs\RW3ProNativeDSHaptics
DefaultGroupName=RW3 Pro Native DS Haptics
OutputDir=..\dist
OutputBaseFilename=RW3ProNativeDSHaptics-Setup-win-x64
Compression=lzma2
SolidCompression=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\RW3ProNativeDSHaptics.exe
DisableProgramGroupPage=yes
WizardStyle=modern
CloseApplications=yes

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\RW3 Pro Native DS Haptics"; Filename: "{app}\RW3ProNativeDSHaptics.exe"
Name: "{autodesktop}\RW3 Pro Native DS Haptics"; Filename: "{app}\RW3ProNativeDSHaptics.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Masaüstü kısayolu oluştur"; GroupDescription: "Ek seçenekler:"

[Run]
Filename: "{app}\RW3ProNativeDSHaptics.exe"; Description: "RW3 Pro Haptics'i başlat"; Flags: postinstall nowait skipifsilent
