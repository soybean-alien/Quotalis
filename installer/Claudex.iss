; Claudex installer (Inno Setup 6).
; Built by tools/build-release.ps1, which passes AppVersion, SourceDir and OutputDir.
; Per-user install: no administrator rights, no Visual Studio, no .NET runtime needed
; (the app is self-contained, including the Windows App SDK).

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish\win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts"
#endif

[Setup]
AppId={{B8374992-AADD-4257-AF57-1D628A3A26B5}
AppName=Claudex
AppVersion={#AppVersion}
AppVerName=Claudex {#AppVersion}
AppPublisher=soybean_alien
AppPublisherURL=https://github.com/soybean-alien/Claudex
AppSupportURL=https://github.com/soybean-alien/Claudex/issues
DefaultDirName={localappdata}\Programs\Claudex
DefaultGroupName=Claudex
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
OutputDir={#OutputDir}
OutputBaseFilename=Claudex-Setup-{#AppVersion}
LicenseFile=..\LICENSE
SetupIconFile=..\src\Claudex\Assets\Claudex.ico
UninstallDisplayIcon={app}\Claudex.exe
UninstallDisplayName=Claudex
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
WizardImageFile=wizard-large.bmp,wizard-large-200.bmp
WizardSmallImageFile=wizard-small.bmp,wizard-small-200.bmp
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
CloseApplications=force
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"
Name: "autostart"; Description: "Start Claudex when I sign in"
Name: "hooks"; Description: "Connect Claude Code and Codex usage (status line and Stop hooks)"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{autoprograms}\Claudex"; Filename: "{app}\Claudex.exe"
Name: "{autoprograms}\Claudex (Demo)"; Filename: "{app}\Claudex.exe"; Parameters: "--demo"
Name: "{autodesktop}\Claudex"; Filename: "{app}\Claudex.exe"; Tasks: desktopicon

[Registry]
; Same entry the app writes for "Start with Windows"; the app adopts it on first launch.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Claudex"; \
    ValueData: """{app}\Claudex.exe"" --autostart"; Flags: uninsdeletevalue; Tasks: autostart

[Run]
; Points the Claude Code status line and Stop hook, and the Codex Stop hook, at this copy (adding them if missing).
Filename: "{app}\Claudex.exe"; Parameters: "--claudex-hooks connect"; Flags: runhidden waituntilterminated; Tasks: hooks
Filename: "{app}\Claudex.exe"; Description: "Open Claudex"; Flags: nowait postinstall skipifsilent
; A silent install is how the in-app updater runs this installer: start the updated Claudex again.
Filename: "{app}\Claudex.exe"; Flags: nowait; Check: WizardSilent

[UninstallRun]
; Removes only the hooks that run this copy, so Claude Code and Codex never call a missing program.
Filename: "{app}\Claudex.exe"; Parameters: "--claudex-hooks disconnect"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveHooks"
Filename: "{sys}\taskkill.exe"; Parameters: "/IM Claudex.exe /F"; Flags: runhidden; RunOnceId: "StopClaudex"
Filename: "{sys}\reg.exe"; Parameters: "delete HKCU\Software\Microsoft\Windows\CurrentVersion\Run /v Claudex /f"; Flags: runhidden; RunOnceId: "RemoveAutostart"

; Usage history and settings in %LOCALAPPDATA%\Claudex are kept on uninstall,
; so a reinstall continues where it left off. Delete that folder to remove them.
