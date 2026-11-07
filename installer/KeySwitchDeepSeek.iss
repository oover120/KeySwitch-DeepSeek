; KeySwitch DeepSeek — установщик для Inno Setup 6 (требуется 6.3+ из-за x64compatible).
;
; Установка per-user: без прав администратора, в %LOCALAPPDATA%\Programs\KeySwitchDeepSeek.
; Автозапуск — необязательная галочка, включается ключом HKCU\...\Run (тот же,
; который использует само приложение и ElevationHelper).
;
; Сборка установщика: Inno Setup 6 → Compile, или
;   "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\KeySwitchDeepSeek.iss
; Перед этим должен быть выполнен publish.ps1 (incoming файлы в artifacts\publish).

#define MyAppName "KeySwitch DeepSeek"
#define MyAppVersion "1.1.0"
#define MyAppPublisher "oover120"
#define MyAppExeName "KeySwitchDeepSeek.exe"
#define MyAppId "{{3C9F1A72-5B4D-4E88-9A17-6D2B8E4F1C93}}"

[Setup]
AppId={#MyAppId}
AppName={cm:AppName}
AppVersion={#MyAppVersion}
AppVerName={cm:AppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\KeySwitchDeepSeek
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
OutputBaseFilename=KeySwitchDeepSeek-{#MyAppVersion}-setup
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
russian.AppName=KeySwitch DeepSeek — переключение раскладки
english.AppName=KeySwitch DeepSeek — keyboard layout switcher
russian.AutostartTask=Запускать KeySwitch DeepSeek при входе в Windows
english.AutostartTask=Start KeySwitch DeepSeek when Windows starts
russian.DesktopIcon=Создать ярлык на рабочем столе
english.DesktopIcon=Create a desktop shortcut
russian.LaunchApp=Запустить KeySwitch DeepSeek
english.LaunchApp=Launch KeySwitch DeepSeek

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
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "KeySwitchDeepSeek"; ValueData: """{app}\{#MyAppExeName}"""; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent

; Пользовательские настройки и журналы (%APPDATA%\KeySwitchDeepSeek) намеренно не трогаем:
; при переустановке они должны остаться.
