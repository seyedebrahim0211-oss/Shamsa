#ifndef MyAppVersion
  #define MyAppVersion "3.0.0"
#endif

#define AppName "Shamsa"
#define AppPublisher "Honar Mardomi"
#define AppURL "https://github.com/seyedebrahim0211-oss/Shamsa"

[Setup]
AppId={{E6C13DD9-67A1-4464-861A-F105DA49D2EF}
AppName={#AppName}
AppVersion={#MyAppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
DefaultDirName={autopf}\{#AppName}
OutputDir=..\dist
OutputBaseFilename=Shamsa-Setup-v{#MyAppVersion}-win-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
DisableProgramGroupPage=yes
CloseApplications=yes
RestartApplications=yes
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "autostart"; Description: "Run Shamsa automatically with Windows"; GroupDescription: "Startup"; Flags: unchecked

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\Shamsa.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\Shamsa.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Shamsa"; ValueData: """{app}\Shamsa.exe"" --tray"; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\Shamsa.exe"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent
