; Installer for League Overlay (Inno Setup 6). Built by publish.ps1, which passes:
;   /DAppVersion=1.10.0   version shown in the installer and Apps list
;   /DWithBuilds          include dist\PopularBuilds.lobuilds (merged into the app's builds on first start)
; Installs for the current user only: no admin prompt, into %LOCALAPPDATA%\Programs\League Overlay.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
; Never change AppId: it's how upgrades and the uninstaller recognise the app.
AppId={{46CB00BB-DD69-4B83-800C-40FB8A33BD7C}
AppName=League Overlay
AppVersion={#AppVersion}
AppVerName=League Overlay {#AppVersion}
AppPublisher=League Overlay
DefaultDirName={localappdata}\Programs\League Overlay
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=LeagueOverlay-Setup-v{#AppVersion}
SetupIconFile=..\LeagueOverlay\skibidi_icon.ico
UninstallDisplayIcon={app}\LeagueOverlay.exe
UninstallDisplayName=League Overlay
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
; The app's single-instance mutex: setup and uninstall ask to close a running copy first.
#ifndef NoMutex
AppMutex=Local\LeagueOverlay.SingleInstance
#endif
CloseApplications=yes

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "..\dist\LeagueOverlay.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\dist\README.txt"; DestDir: "{app}"; Flags: ignoreversion
#ifdef WithBuilds
Source: "..\dist\PopularBuilds.lobuilds"; DestDir: "{app}"; Flags: ignoreversion
#endif

[Icons]
Name: "{autoprograms}\League Overlay"; Filename: "{app}\LeagueOverlay.exe"
Name: "{autodesktop}\League Overlay"; Filename: "{app}\LeagueOverlay.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\README.txt"; Description: "Show the quick guide"; Flags: postinstall shellexec skipifsilent unchecked
Filename: "{app}\LeagueOverlay.exe"; Description: "Start League Overlay now"; Flags: nowait postinstall skipifsilent

[Code]
// Settings, saved games and collected builds live in %LOCALAPPDATA%\LeagueOverlay, outside the install folder.
// Keep them by default (so reinstalling keeps everything); offer to remove them on uninstall.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{localappdata}\LeagueOverlay');
    if DirExists(DataDir) and not UninstallSilent() then
      if MsgBox('Also delete your League Overlay settings, saved games and collected builds?' + #13#10 +
                'Choose No to keep them for a later reinstall.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(DataDir, True, True, True);
  end;
end;
