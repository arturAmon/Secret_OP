; Installer for Secret OP (Inno Setup 6), formerly League Overlay. Built by publish.ps1, which passes:
;   /DAppVersion=1.10.0   version shown in the installer and Apps list
;   /DWithBuilds          include dist\PopularBuilds.lobuilds (merged into the app's builds on first start)
; Installs for the current user only: no admin prompt, into %LOCALAPPDATA%\Programs\Secret OP.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
; Never change AppId: it's how upgrades and the uninstaller recognise the app.
AppId={{46CB00BB-DD69-4B83-800C-40FB8A33BD7C}
AppName=Secret OP
AppVersion={#AppVersion}
AppVerName=Secret OP {#AppVersion}
AppPublisher=Secret OP
DefaultDirName={localappdata}\Programs\Secret OP
; Install into the new folder even when upgrading from "League Overlay" (the old folder is removed below).
UsePreviousAppDir=no
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=SecretOP-Setup-v{#AppVersion}
SetupIconFile=..\LeagueOverlay\skibidi_icon.ico
UninstallDisplayIcon={app}\SecretOP.exe
UninstallDisplayName=Secret OP
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
Source: "..\dist\SecretOP.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\dist\README.txt"; DestDir: "{app}"; Flags: ignoreversion
#ifdef WithBuilds
Source: "..\dist\PopularBuilds.lobuilds"; DestDir: "{app}"; Flags: ignoreversion
#endif

[InstallDelete]
; Left over from before the rename (League Overlay): the old install folder and its shortcuts.
Type: filesandordirs; Name: "{localappdata}\Programs\League Overlay"
Type: files; Name: "{autoprograms}\League Overlay.lnk"
Type: files; Name: "{autodesktop}\League Overlay.lnk"

[Icons]
Name: "{autoprograms}\Secret OP"; Filename: "{app}\SecretOP.exe"
Name: "{autodesktop}\Secret OP"; Filename: "{app}\SecretOP.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\README.txt"; Description: "Show the quick guide"; Flags: postinstall shellexec skipifsilent unchecked
Filename: "{app}\SecretOP.exe"; Description: "Start Secret OP now"; Flags: nowait postinstall skipifsilent
; In-app updates run this installer silently: start the app again afterwards.
Filename: "{app}\SecretOP.exe"; Flags: nowait; Check: WizardSilent

[Code]
// Settings, saved games and collected builds live in %LOCALAPPDATA%\LeagueOverlay (the app's name before it was
// renamed, kept so nothing is lost), outside the install folder.
// Keep them by default (so reinstalling keeps everything); offer to remove them on uninstall.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{localappdata}\LeagueOverlay');
    if DirExists(DataDir) and not UninstallSilent() then
      if MsgBox('Also delete your Secret OP settings, saved games and collected builds?' + #13#10 +
                'Choose No to keep them for a later reinstall.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(DataDir, True, True, True);
  end;
end;
