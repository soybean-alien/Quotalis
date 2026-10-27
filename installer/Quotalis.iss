; Quotalis installer (Inno Setup 6).
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
; The AppId of Claudex, this app's name before 0.7.0: installing over Claudex is an upgrade, not a second app.
AppId={{B8374992-AADD-4257-AF57-1D628A3A26B5}
AppName=Quotalis
AppVersion={#AppVersion}
AppVerName=Quotalis {#AppVersion}
AppPublisher=soybean_alien
AppPublisherURL=https://github.com/soybean-alien/Quotalis
AppSupportURL=https://github.com/soybean-alien/Quotalis/issues
DefaultDirName={localappdata}\Programs\Quotalis
; Not Claudex's Programs\Claudex: an upgrade moves to the new folder ([InstallDelete] removes the old one).
UsePreviousAppDir=no
DefaultGroupName=Quotalis
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
OutputDir={#OutputDir}
OutputBaseFilename=Quotalis-Setup-{#AppVersion}
LicenseFile=..\LICENSE
SetupIconFile=..\src\Quotalis\Assets\Quotalis.ico
UninstallDisplayIcon={app}\Quotalis.exe
UninstallDisplayName=Quotalis
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
Name: "autostart"; Description: "Start Quotalis when I sign in"
Name: "hooks"; Description: "Connect Claude Code and Codex usage (status line and Codex Stop hook)"

[InstallDelete]
; Claudex (before 0.7.0): its program folder and shortcuts. Its data folders move over when Quotalis first starts.
Type: filesandordirs; Name: "{localappdata}\Programs\Claudex"
Type: files; Name: "{autoprograms}\Claudex.lnk"
Type: files; Name: "{autoprograms}\Claudex (Demo).lnk"
Type: files; Name: "{autodesktop}\Claudex.lnk"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{autoprograms}\Quotalis"; Filename: "{app}\Quotalis.exe"
Name: "{autoprograms}\Quotalis (Demo)"; Filename: "{app}\Quotalis.exe"; Parameters: "--demo"
Name: "{autodesktop}\Quotalis"; Filename: "{app}\Quotalis.exe"; Tasks: desktopicon

[Registry]
; Same entry the app writes for "Start with Windows"; the app adopts it on first launch.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Quotalis"; \
    ValueData: """{app}\Quotalis.exe"" --autostart"; Flags: uninsdeletevalue; Tasks: autostart
; Claudex's entry would start a program that is gone.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "Claudex"; Flags: deletevalue

[Run]
; Points the Claude Code status line and the Codex Stop hook at this copy (adding them if missing), and removes the Claude Stop hook of versions before 0.5.14.
Filename: "{app}\Quotalis.exe"; Parameters: "--quotalis-hooks connect"; Flags: runhidden waituntilterminated; Tasks: hooks
; Without that task: still points entries that already exist (also Claudex's) at this copy, adding none.
Filename: "{app}\Quotalis.exe"; Parameters: "--quotalis-hooks repoint"; Flags: runhidden waituntilterminated; Tasks: not hooks
Filename: "{app}\Quotalis.exe"; Description: "Open Quotalis"; Flags: nowait postinstall skipifsilent
; A silent install is how the in-app updater runs this installer: start the updated Quotalis again.
Filename: "{app}\Quotalis.exe"; Flags: nowait; Check: WizardSilent

[UninstallRun]
; Removes only the hooks that run this copy, so Claude Code and Codex never call a missing program.
Filename: "{app}\Quotalis.exe"; Parameters: "--quotalis-hooks disconnect"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveHooks"
Filename: "{sys}\taskkill.exe"; Parameters: "/IM Quotalis.exe /F"; Flags: runhidden; RunOnceId: "StopQuotalis"
Filename: "{sys}\reg.exe"; Parameters: "delete HKCU\Software\Microsoft\Windows\CurrentVersion\Run /v Quotalis /f"; Flags: runhidden; RunOnceId: "RemoveAutostart"

; Usage history and settings in %LOCALAPPDATA%\Quotalis are kept on uninstall,
; so a reinstall continues where it left off. Delete that folder to remove them.

[Code]
// Claudex (before 0.7.0) runs from the folder [InstallDelete] removes. The in-app updater has already quit it; a
// manual install over a running Claudex asks it to close first, and forces it only if it is still running.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM Claudex.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
  if Code = 0 then
  begin
    Sleep(3000);
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM Claudex.exe /F', '', SW_HIDE, ewWaitUntilTerminated, Code);
  end;
  Result := '';
end;
