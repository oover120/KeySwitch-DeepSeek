; KeySwitch — установщик для Inno Setup 6 (требуется 6.3+ из-за x64compatible).
;
; Установка per-user: без прав администратора, в %LOCALAPPDATA%\Programs\KeySwitch.
; Автозапуск — необязательная галочка, включается ключом HKCU\...\Run (тот же,
; который использует само приложение и ElevationHelper).
;
; Сборка установщика: Inno Setup 6 → Compile, или
;   "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\KeySwitch.iss
; Перед этим должен быть выполнен publish.ps1 (incoming файлы в artifacts\publish).

#define MyAppName "KeySwitch"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "KeySwitch"
#define MyAppExeName "KeySwitch.exe"
#define MyAppId "{{B7E4C3A1-9F2D-4C6E-8A1B-3D5F7C9E2A41}}"

[Setup]
AppId={#MyAppId}
AppName={cm:AppName}
AppVersion={#MyAppVersion}
AppVerName={cm:AppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\KeySwitch
DefaultGroupName={cm:AppName}
DisableProgramGroupPage=yes
AllowNoIcons=yes
; Установка только для текущего пользователя, повышение прав не требуется.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=..\artifacts\installer
OutputBaseFilename=KeySwitch-{#MyAppVersion}-setup
SetupIconFile=..\src\KeySwitch.App\Resources\keyswitch.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Если приложение запущено — установщик предложит его закрыть.
CloseApplications=yes
RestartApplications=no
UninstallDisplayName={cm:AppName}

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
russian.AppName=KeySwitch — переключение раскладки
english.AppName=KeySwitch — keyboard layout switcher
russian.AutostartTask=Запускать KeySwitch при входе в Windows
english.AutostartTask=Start KeySwitch when Windows starts
russian.DesktopIcon=Создать ярлык на рабочем столе
english.DesktopIcon=Create a desktop shortcut
russian.LaunchApp=Запустить KeySwitch
english.LaunchApp=Launch KeySwitch

[Tasks]
Name: "autostart"; Description: "{cm:AutostartTask}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "desktopicon"; Description: "{cm:DesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Один самодостаточный exe из publish.ps1 (single-file, self-contained).
Source: "..\artifacts\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Comment: "{cm:AppName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Comment: "{cm:AppName}"; Tasks: desktopicon

[Registry]
; Автозапуск через HKCU\...\Run — без планировщика и без прав администратора.
; Значение совпадает с тем, что пишет ElevationHelper.SetRunKeyAutostart.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "KeySwitch"; ValueData: """{app}\{#MyAppExeName}"""; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent

; Пользовательские настройки и журналы (%APPDATA%\KeySwitch) намеренно не трогаем:
; при переустановке они должны остаться.
