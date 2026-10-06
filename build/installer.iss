; Installer for Master Control Dock. Built on the server, not on a developer
; machine - see .github/workflows/release.yml.
;
; Per user, never per machine: the program needs nothing from administrator
; rights (the one thing that does, the sensor service, is put in by its own
; button with its own prompt), and asking for them here would put a consent
; dialog in front of a strip of numbers.

#define AppName "Master Control Dock"
#define AppExe "MasterControlDock.exe"
#define AppPublisher "electronic-mars"
#define AppUrl "https://github.com/electronic-mars/MCD"
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\build\app"
#endif

[Setup]
; Never change this: it is how Windows recognises an upgrade of the same program.
AppId={{B3D5A6E1-4C27-4F9A-8E52-6A1C0D7F93B8}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
DefaultDirName={autopf}\MasterControlDock
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
; Per user unless the person asks for more: the first page offers "for all
; users" and, only then, asks for administrator rights. {autopf} is the user's
; own Programs folder in the first case and Program Files in the second, so a
; folder under Program Files no longer fails with "access denied".
PrivilegesRequiredOverridesAllowed=dialog
OutputBaseFilename=MasterControlDock-{#AppVersion}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#AppExe}
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
; Not CloseApplications: that ends the process, and a bar that is ended instead
; of asked to stop leaves its strip of the desktop reserved until the next
; sign-out. The program has a proper way to be told to stop - started with
; --exit it signals the running copy - and PrepareToInstall below uses it.
CloseApplications=no
RestartApplications=no
SetupMutex=MasterControlDockSetup

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"

[CustomMessages]
en.Autostart=Start with Windows
ru.Autostart=Запускать с Windows
en.KeepSettings=Keep my settings
ru.KeepSettings=Сохранить мои настройки

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked
Name: "autostart"; Description: "{cm:Autostart}"

[Files]
; The sensor service lives in a folder of its own and, once installed, runs
; as the system with its files held open. A per-user installer cannot stop it,
; so its files are left alone while it runs; the program says on the Readings
; page when the service is older than the program.
Source: "{#SourceDir}\*"; Excludes: "SensorHost\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SourceDir}\SensorHost\*"; DestDir: "{app}\SensorHost"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist; Check: not ServiceRunning

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; The same value the program writes itself, so the two never disagree.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
    ValueType: string; ValueName: "MasterControlDock"; \
    ValueData: """{app}\{#AppExe}"""; \
    Flags: uninsdeletevalue; Tasks: autostart
; Removed on uninstall even if it was switched on from inside the program later.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
    ValueType: none; ValueName: "MasterControlDock"; Flags: uninsdeletevalue

[Run]
; runasoriginaluser: an install "for all users" runs Setup elevated, and a program
; started from it would inherit that. An elevated WinUI program cannot take part in
; drag and drop, so nothing could be dragged onto the bar.
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; \
    Flags: nowait postinstall skipifsilent runasoriginaluser
; The program updating itself runs this installer silently and then stops, so its
; own files can be replaced. Silent skips the tick box above, so without this
; line the update would end with the program simply gone. The flag is ours and
; is passed only on that path.
Filename: "{app}\{#AppExe}"; Flags: nowait runasoriginaluser; Check: WasStartedByTheProgram

[Code]
function ServiceRunning: Boolean;
var
  Code: Integer;
begin
  Result := Exec(ExpandConstant('{cmd}'),
    '/c sc query MasterControlDockSensors | find "RUNNING"',
    '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0);
end;

function WasStartedByTheProgram: Boolean;
begin
  Result := ExpandConstant('{param:RELAUNCH|0}') = '1';
end;

{ Asks a running copy to stop, the way the program itself understands, and
  gives it a few seconds to give the desktop back. }
procedure StopRunningCopy;
var
  Exe: String;
  Code: Integer;
begin
  Exe := ExpandConstant('{app}\{#AppExe}');
  if FileExists(Exe) then
  begin
    Exec(Exe, '--exit', '', SW_HIDE, ewWaitUntilTerminated, Code);
    Sleep(4000);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopRunningCopy;
  Result := '';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Data: String;
begin
  if CurUninstallStep = usUninstall then
    StopRunningCopy;

  { Settings live outside the program folder so that an update does not lose
    them; that also means an uninstall leaves them behind unless we ask. }
  if CurUninstallStep = usPostUninstall then
  begin
    Data := ExpandConstant('{localappdata}\MCD');
    if DirExists(Data) then
      if SuppressibleMsgBox(ExpandConstant('{cm:KeepSettings}') + #13#10 + Data,
                            mbConfirmation, MB_YESNO, IDYES) = IDNO then
        DelTree(Data, True, True, True);
  end;
end;
