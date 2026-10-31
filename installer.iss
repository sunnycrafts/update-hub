; UpdateHub installer.
;
; Wraps the same self-contained single-file publish output used for the portable build
; (see publish/), so the installer and the portable EXE are always built from one binary
; rather than two separately-configured outputs that could drift apart.
;
; Build: run "dotnet publish -c Release -o publish" first, then compile this with ISCC.

#define AppName "UpdateHub"
#define AppVersion "0.9.0"
#define AppPublisher "UpdateHub"
#define AppExeName "UpdateHub.exe"
#define SourceDir "publish"

[Setup]
AppId={{9F1E9C2E-6B7F-4B7B-9C7A-3B7C6C3E9A4B}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}
; Per-user by default; no elevation needed to install (winget/msiexec calls the app makes
; at runtime elevate individually via UAC, same as the app already does for updates).
PrivilegesRequired=lowest
DisableProgramGroupPage=yes
OutputDir=installer-output
OutputBaseFilename=UpdateHub-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=Assets\UpdateHub.ico
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"

[Files]
Source: "{#SourceDir}\{#AppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Cached release history, provider responses and settings are rebuildable state, not user
; data proper; leave them on disk by default so a reinstall doesn't have to refetch through
; the GitHub rate limit. Nothing here deletes them — this section intentionally left empty.
