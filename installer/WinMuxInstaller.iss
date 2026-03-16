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

#ifdef WebView2BootstrapperFile
  #define WebView2BootstrapperName "MicrosoftEdgeWebview2Setup.exe"
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
Compression=lzma2/normal
SolidCompression=no
WizardStyle=modern dynamic
WizardSmallImageFile={#RepoRoot}\Assets\Square150x150Logo.png
WizardSmallImageFileDynamicDark={#RepoRoot}\Assets\Square150x150Logo.png
WizardSmallImageBackColor=#ffffff
WizardSmallImageBackColorDynamicDark=#1f1f1f
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
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs
#ifdef WebView2BootstrapperFile
Source: "{#WebView2BootstrapperFile}"; Flags: dontcopy noencryption
#endif

[Icons]
Name: "{autoprograms}\WinMux"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\WinMux"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch WinMux"; Flags: nowait postinstall skipifsilent unchecked

[Code]
#ifdef WebView2BootstrapperFile
const
  WebView2RuntimeClientGuid = '{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';

function HasInstalledWebView2Version(const Version: String): Boolean;
begin
  Result := (Version <> '') and (Version <> '0.0.0.0');
end;

function QueryWebView2RuntimeVersion(RootKey: Integer; const SubKey: String): String;
begin
  if not RegQueryStringValue(RootKey, SubKey, 'pv', Result) then
    Result := '';
end;

function IsWebView2RuntimeInstalled: Boolean;
var
  Version: String;
begin
  Version := QueryWebView2RuntimeVersion(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\' + WebView2RuntimeClientGuid);

  if not HasInstalledWebView2Version(Version) then
    Version := QueryWebView2RuntimeVersion(HKLM32, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\' + WebView2RuntimeClientGuid);

  if not HasInstalledWebView2Version(Version) then
    Version := QueryWebView2RuntimeVersion(HKLM64, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\' + WebView2RuntimeClientGuid);

  Result := HasInstalledWebView2Version(Version);
end;

function EnsureWebView2RuntimeInstalled: String;
var
  ResultCode: Integer;
  BootstrapperPath: String;
begin
  Result := '';

  if IsWebView2RuntimeInstalled then
  begin
    Log('Detected installed Microsoft Edge WebView2 Runtime.');
    exit;
  end;

  Log('Microsoft Edge WebView2 Runtime not found. Extracting bundled bootstrapper.');
  ExtractTemporaryFile('{#WebView2BootstrapperName}');
  BootstrapperPath := ExpandConstant('{tmp}\{#WebView2BootstrapperName}');

  WizardForm.StatusLabel.Caption := 'Installing Microsoft Edge WebView2 Runtime...';

  if not Exec(BootstrapperPath, '/silent /install', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    Result := 'WinMux requires Microsoft Edge WebView2 Runtime, and the bundled installer could not be started.';
    exit;
  end;

  Log(Format('WebView2 bootstrapper exited with code %d.', [ResultCode]));

  if not IsWebView2RuntimeInstalled then
    Result := 'WinMux requires Microsoft Edge WebView2 Runtime. The bundled runtime installer did not complete successfully. Please rerun setup while connected to the internet.';
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := EnsureWebView2RuntimeInstalled;
end;
#endif
