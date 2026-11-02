; ============================================================================
;  RitschyMirror - Inno Setup Skript (v1.0)
;  Screen Mirror Tool for capture cards (HDR -> SDR tonemapping).
;
;  Per-user installation (NO administrator / UAC needed) - installs to
;  %LOCALAPPDATA%\Programs\RitschyMirror. Exe + data live together in one
;  writable folder (the app writes config\*.json and log\*.log below it).
;  Uninstalling removes the folder completely.
;
;  Build:  "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" RitschyMirror.iss
; ============================================================================

#define MyAppName "RitschyMirror"
#define MyAppVersion "1.4.0"
#define MyAppPublisher "RitschyRigz"
#define MyAppURL "https://github.com/gloomycoder/ritschy-mirror"
#define MyAppExeName "RitschyMirror.exe"
; Project root relative to the .iss (located in installer\) — portable, no absolute build path.
#define SrcDir SourcePath + ".."

[Setup]
; Unique app ID (fixed - do NOT change, otherwise an update will not recognize the old installation).
AppId={{8F2C6A14-9B3D-4E7A-AC51-1D9E2F6B0C77}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoProductName={#MyAppName}
VersionInfoDescription={#MyAppName} Setup

DefaultDirName={localappdata}\Programs\{#MyAppName}
DisableProgramGroupPage=yes
DefaultGroupName={#MyAppName}
AllowNoIcons=yes

; Per-user: no admin rights, no UAC prompt.
PrivilegesRequired=lowest

; 64-bit only (self-contained win-x64 Build).
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; Close the running app via Restart Manager before (un)installation.
CloseApplications=yes
RestartApplications=no

OutputDir={#SrcDir}\installer\out
OutputBaseFilename=RitschyMirror-Setup-{#MyAppVersion}
SetupIconFile={#SrcDir}\assets\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}

WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes

[Languages]
Name: "german"; MessagesFile: "compiler:Languages\German.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "autostart"; Description: "{cm:AutoStartProgram,{#MyAppName}}"; GroupDescription: "{cm:AutoStartProgramGroupDescription}"

[Files]
Source: "{#SrcDir}\publish\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
; Only create the default config if none exists yet (an update does NOT overwrite user settings).
; An older install's {app}\mirror_config.json is moved into config\ by the app itself on first start.
Source: "{#SrcDir}\installer\default_config\mirror_config.json"; DestDir: "{app}\config"; Flags: onlyifdoesntexist

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Comment: "Screen Mirror Tool"
Name: "{group}\{#MyAppName} Einstellungen"; Filename: "{app}\{#MyAppExeName}"; Parameters: "--settings"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; Autostart via the SAME HKCU\Run entry that the settings checkbox manages —
; so there is only ONE mechanism (no double autostart) and the checkbox shows the truth.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
  ValueName: "{#MyAppName}"; ValueData: """{app}\{#MyAppExeName}"""; Tasks: autostart; \
  Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Remove files created by the program (config/settings/log) + folder completely.
Type: filesandordirs; Name: "{app}"
