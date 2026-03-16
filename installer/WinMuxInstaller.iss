#ifndef PublishDir
  #error "PublishDir must be defined."
#endif

#ifndef OutputDir
  #error "OutputDir must be defined."
#endif

#ifndef OutputBaseFilename
  #define OutputBaseFilename "WinMux-win-x64-installer"
#endif

#ifndef RepoRoot
  #error "RepoRoot must be defined."
#endif

#ifndef AppVersion
  #define AppVersion "0.0.0-local"
#endif

#ifndef AppVersionNumeric
  #define AppVersionNumeric "0.0.0.0"
#endif

#define MyAppName "WinMux"
#define MyAppPublisher "WinMux"
#define MyAppExeName "WinMux.exe"
#define MyAppUrl "https://github.com/editnori/WinMux"

[Setup]
AppId={{6BFD0EA0-0C05-4F86-B4FC-0BA1682D7064}
AppName={#MyAppName}
AppVersion={#AppVersion}
AppVerName={#MyAppName} {#AppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppUrl}
AppSupportURL={#MyAppUrl}
AppUpdatesURL={#MyAppUrl}
DefaultDirName={localappdata}\Programs\WinMux
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename={#OutputBaseFilename}
SetupIconFile={#RepoRoot}\Assets\winmux.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
VersionInfoVersion={#AppVersionNumeric}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription=WinMux Installer
VersionInfoProductName={#MyAppName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\WinMux"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\WinMux"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch WinMux"; Flags: nowait postinstall skipifsilent
