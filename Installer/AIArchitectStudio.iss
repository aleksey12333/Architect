; Architect Studio Pro 1.0 — инсталлятор Inno Setup 6
; Упаковывает self-contained Release-публикацию (publish\win-x64: .NET 8 и Windows App SDK внутри — на машине пользователя
; ничего доустанавливать не нужно) и демонстрационный комплект DemoKit (прайс поставщиков, библиотека .aialib.json,
; растровый план, шпаргалка) с ярлыками в меню «Пуск». Локальные модели .gguf (LocalModels) в инсталлятор не входят:
; основной локальный движок — сервер Ollama, его модели пользователь ставит сам.
;
; Сборка: powershell -File Installer\build-installer.ps1   (publish → ISCC → Installer\Output)

#define AppName "Architect Studio Pro"
#define AppVersion "1.0.0"
#define AppPublisher "AI Architect Studio"
#define AppExeName "AIArchitectStudio.exe"
#define PublishDir "..\publish\win-x64"

#if !FileExists(AddBackslash(SourcePath) + PublishDir + "\" + AppExeName)
  #error "Нет Release-публикации: сначала dotnet publish (см. Installer\build-installer.ps1)"
#endif
#if !FileExists(AddBackslash(SourcePath) + PublishDir + "\DemoKit\README_Демонстрация.txt")
  #error "В публикации нет папки DemoKit"
#endif

[Setup]
AppId={{6B0E4F2A-3C1D-4E8B-9A57-2D41C8F0A1B3}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion} Developer Edition
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} — платформа BIM-проектирования
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=Output
OutputBaseFilename=ArchitectStudioPro-{#AppVersion}-DeveloperEdition-Setup
Compression=lzma2/ultra64
SolidCompression=yes
LZMANumBlockThreads=4
; Сжатие в отдельном 64-битном процессе: 32-битному ISCC не хватает адресного пространства для ultra64 × 4 потока.
LZMAUseSeparateProcess=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName} {#AppVersion}
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
russian.DemoKitGroup=Демонстрационный комплект
english.DemoKitGroup=Demo kit
russian.DemoKitReadme=Шпаргалка для показа
english.DemoKitReadme=Demo cheat sheet
russian.OpenReadme=Открыть шпаргалку демонстрации
english.OpenReadme=Open the demo cheat sheet

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "demokitdesktop"; Description: "{cm:DemoKitGroup} — {cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Вся публикация (exe, .NET 8, Windows App SDK, HelixToolkit, LLamaSharp, шаблоны, DemoKit) — с подпапками.
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb"

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\{cm:DemoKitGroup}"; Filename: "{app}\DemoKit"
Name: "{group}\{cm:DemoKitReadme}"; Filename: "{app}\DemoKit\README_Демонстрация.txt"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon
Name: "{autodesktop}\{#AppName} — {cm:DemoKitGroup}"; Filename: "{app}\DemoKit"; Tasks: demokitdesktop

[Run]
Filename: "{app}\DemoKit\README_Демонстрация.txt"; Description: "{cm:OpenReadme}"; Flags: postinstall shellexec skipifsilent unchecked
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
