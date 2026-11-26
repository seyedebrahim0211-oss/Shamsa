#ifndef MyAppVersion
#define MyAppVersion "3.2.0"
#endif
#define AppName "Negarkhaneh"

[Setup]
AppId={{E6C13DD9-67A1-4464-861A-F105DA49D2EF}
AppName={#AppName}
AppVerName=نگارخانه (Negarkhaneh) نسخه {#MyAppVersion}
AppVersion={#MyAppVersion}
VersionInfoVersion={#MyAppVersion}
AppPublisher=Negarkhaneh Contributors
AppPublisherURL=https://github.com/seyedebrahim0211-oss/Shamsa
AppSupportURL=https://github.com/seyedebrahim0211-oss/Shamsa/issues
AppUpdatesURL=https://github.com/seyedebrahim0211-oss/Shamsa/releases
DefaultDirName={autopf}\Shamsa
DefaultGroupName=نگارخانه
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
; In-app updates pass /CLOSEAPPLICATIONS; the app also exits on its own before the installer copies files.
CloseApplications=yes
RestartApplications=no
UninstallDisplayName=نگارخانه (Negarkhaneh)
SetupLogging=yes
; Updating in place keeps %LocalAppData%\Shamsa (settings, icons, recordings list) untouched.
UsePreviousAppDir=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "ساخت میانبر روی دسکتاپ"; GroupDescription: "میانبرها:"; Flags: unchecked

[InstallDelete]
; Shortcuts from before the rename to Negarkhaneh (the exe and data folder keep their names for in-place updates).
Type: files; Name: "{autoprograms}\شمسا.lnk"
Type: files; Name: "{autodesktop}\شمسا.lnk"

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\نگارخانه"; Filename: "{app}\Shamsa.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\نگارخانه"; Filename: "{app}\Shamsa.exe"; Tasks: desktopicon; WorkingDir: "{app}"

[Registry]
; Explorer integrations are created from inside the app (opt-in) under HKCU; remove them on uninstall only.
Root: HKCU; Subkey: "Software\Classes\Directory\shell\Shamsa.FolderStudio"; Flags: uninsdeletekey dontcreatekey
Root: HKCU; Subkey: "Software\Classes\Directory\shell\Shamsa.FolderLock"; Flags: uninsdeletekey dontcreatekey
Root: HKCU; Subkey: "Software\Classes\Shamsa.LockedFolder"; Flags: uninsdeletekey dontcreatekey
Root: HKCU; Subkey: "Software\Classes\.shamsalock"; Flags: uninsdeletekey dontcreatekey

[Run]
Filename: "{app}\Shamsa.exe"; Description: "اجرای نگارخانه"; Flags: nowait postinstall skipifsilent
; Silent in-app update: relaunch automatically so the user does not have to restart the app.
Filename: "{app}\Shamsa.exe"; Parameters: "--updated --tray"; Flags: nowait; Check: WizardSilent
