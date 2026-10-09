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
ShowLanguageDialog=auto

; Shown in the language of Windows when there is one of ours, and in a list to
; pick from when there is not. The program itself starts in the language of
; Windows as well.
[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "uk"; MessagesFile: "compiler:Languages\Ukrainian.isl"
Name: "de"; MessagesFile: "compiler:Languages\German.isl"
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "fr"; MessagesFile: "compiler:Languages\French.isl"
Name: "it"; MessagesFile: "compiler:Languages\Italian.isl"
Name: "pt"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "pl"; MessagesFile: "compiler:Languages\Polish.isl"
Name: "cs"; MessagesFile: "compiler:Languages\Czech.isl"
Name: "nl"; MessagesFile: "compiler:Languages\Dutch.isl"
Name: "tr"; MessagesFile: "compiler:Languages\Turkish.isl"
Name: "ja"; MessagesFile: "compiler:Languages\Japanese.isl"
Name: "ko"; MessagesFile: "compiler:Languages\Korean.isl"
; Not shipped with the compiler; the file is Inno Setup's own, kept beside this script.
Name: "zh"; MessagesFile: "languages\ChineseSimplified.isl"

[CustomMessages]
en.Autostart=Start with Windows
en.KeepSettings=Keep my settings
ru.Autostart=Запускать при входе в Windows
ru.KeepSettings=Сохранить мои настройки
uk.Autostart=Запускати з Windows
uk.KeepSettings=Зберегти мої налаштування
de.Autostart=Mit Windows starten
de.KeepSettings=Meine Einstellungen behalten
es.Autostart=Iniciar con Windows
es.KeepSettings=Conservar mi configuración
fr.Autostart=Démarrer avec Windows
fr.KeepSettings=Conserver mes paramètres
it.Autostart=Avvia con Windows
it.KeepSettings=Mantieni le mie impostazioni
pt.Autostart=Iniciar com o Windows
pt.KeepSettings=Manter minhas configurações
pl.Autostart=Uruchamiaj z systemem Windows
pl.KeepSettings=Zachowaj moje ustawienia
cs.Autostart=Spustit s Windows
cs.KeepSettings=Zachovat moje nastavení
nl.Autostart=Starten met Windows
nl.KeepSettings=Mijn instellingen behouden
tr.Autostart=Windows ile başlat
tr.KeepSettings=Ayarlarımı koru
ja.Autostart=Windowsとともに起動
ja.KeepSettings=設定を保持する
ko.Autostart=Windows 시작 시 실행
ko.KeepSettings=내 설정 유지
zh.Autostart=随 Windows 启动
zh.KeepSettings=保留我的设置

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
; The name and icon its notifications carry, which the program registers when
; it starts.
Root: HKCU; Subkey: "Software\Classes\AppUserModelId\ElectronicMars.MasterControlDock"; \
    Flags: uninsdeletekey dontcreatekey

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
