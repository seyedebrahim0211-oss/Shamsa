#ifndef MyAppVersion
#define MyAppVersion "2.9.2"
#endif
#define AppName "Shamsa"

[Setup]
AppId={{E6C13DD9-67A1-4464-861A-F105DA49D2EF}
AppName={#AppName}
AppVerName=شمسا (Shamsa) نسخه {#MyAppVersion}
AppVersion={#MyAppVersion}
AppPublisher=Shamsa Contributors
AppPublisherURL=https://github.com/seyedebrahim0211-oss/Shamsa
AppSupportURL=https://github.com/seyedebrahim0211-oss/Shamsa/issues
AppUpdatesURL=https://github.com/seyedebrahim0211-oss/Shamsa/releases
DefaultDirName={autopf}\Shamsa
DefaultGroupName=شمسا
UninstallDisplayIcon={app}\Shamsa.exe
OutputDir=..\dist
OutputBaseFilename=Shamsa-Setup-v{#MyAppVersion}-win-x64
SetupIconFile=..\Assets\Shamsa.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
DisableProgramGroupPage=yes
CloseApplications=yes
RestartApplications=no
UninstallDisplayName=شمسا (Shamsa)
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "ساخت میانبر روی دسکتاپ"; GroupDescription: "میانبرها:"; Flags: unchecked

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\شمسا"; Filename: "{app}\Shamsa.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\شمسا"; Filename: "{app}\Shamsa.exe"; Tasks: desktopicon; WorkingDir: "{app}"

[Run]
Filename: "{app}\Shamsa.exe"; Description: "اجرای شمسا"; Flags: nowait postinstall skipifsilent
