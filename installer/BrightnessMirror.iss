; Brightness Mirror installer, built with Inno Setup 6 by build.ps1.
;
; - Installs to Program Files by default; the folder page lets you choose another one, and the
;   first page offers "Install for me only", which needs no admin rights (installs to
;   %LOCALAPPDATA%\Programs instead).
; - Adds a Start menu entry and an uninstaller (Settings > Apps > Installed apps).
; - "Start with Windows" is handed to the app itself (BrightnessMirror.exe --startup on|off), run as
;   the signed-in user, so it becomes an ordinary per-user startup app that can be switched off in
;   Task Manager > Startup apps or in the app's tray menu.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#define AppName "Brightness Mirror"
#define AppExe "BrightnessMirror.exe"
#define AppUrl "https://github.com/kstawi/brightness-mirror"

[Setup]
AppId={{6E1D2B7C-4A9F-4C3E-8B05-2F7A91D3C6E4}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=kstawi
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
DefaultDirName={autopf}\{#AppName}
DisableDirPage=no
DisableProgramGroupPage=yes
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog commandline
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputBaseFilename=BrightnessMirror-Setup-{#AppVersion}
SetupIconFile=..\assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
CloseApplications=no
UsedUserAreasWarning=no
VersionInfoVersion={#AppVersion}
VersionInfoCompany=kstawi
VersionInfoDescription={#AppName} Setup
VersionInfoProductName={#AppName}

[Tasks]
Name: "startup"; Description: "Start {#AppName} when I sign in to Windows"

[Files]
Source: "..\build\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; Comment: "Make the Windows brightness slider control your external monitor too"

[Run]
; Runs as the signed-in user (not the elevated installer), so the entry lands in their startup apps.
Filename: "{app}\{#AppExe}"; Parameters: "--startup on"; Tasks: startup; Flags: runasoriginaluser waituntilterminated
Filename: "{app}\{#AppExe}"; Parameters: "--startup off"; Tasks: not startup; Flags: runasoriginaluser waituntilterminated
Filename: "{app}\{#AppExe}"; Description: "Start {#AppName} now"; Flags: postinstall nowait skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM {#AppExe}"; Flags: runhidden; RunOnceId: "StopApp"

[Registry]
; Nothing is created here; these only remove the app's per-user entries on uninstall.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "BrightnessMirror"; Flags: uninsdeletevalue dontcreatekey
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"; ValueType: none; ValueName: "BrightnessMirror"; Flags: uninsdeletevalue dontcreatekey
Root: HKCU; Subkey: "Software\BrightnessMirror"; ValueType: none; Flags: uninsdeletekey dontcreatekey

[UninstallDelete]
; Settings and log.
Type: filesandordirs; Name: "{localappdata}\BrightnessMirror"

[Code]
// Stop a running copy so its exe can be replaced.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := '';
end;
