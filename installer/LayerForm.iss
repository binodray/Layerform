; Layer Form installer (Inno Setup 6).
; Build with scripts\build-installer.ps1, which publishes the app first and passes the version in.
;
; Installs per user under %LOCALAPPDATA%\Programs\Layer Form, so neither installing nor
; auto-updating needs administrator rights. The in-app updater runs this installer with
; /SILENT /UPDATE=1; in that mode Layer Form is reopened once the update is in place.

#ifndef AppVersion
  #define AppVersion "1.1.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish\win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\installer"
#endif

#define AppName "Layer Form"
#define AppExe "LayerForm.exe"
#define Website "https://hastamev.com/layerform"
#define Repository "https://github.com/binodray/Layerform"

[Setup]
; Never change AppId: it's how updates find and replace an existing installation.
AppId={{E77A81DF-74A6-4728-AFCC-839EAE00F493}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Binod Ray
AppPublisherURL={#Website}
AppSupportURL={#Repository}/issues
AppUpdatesURL={#Website}
AppCopyright=Copyright (c) 2026 Binod Ray and Layer Form contributors; portions Wonder Assembly LLC
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} Setup

DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
DisableReadyPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763

OutputDir={#OutputDir}
OutputBaseFilename=LayerForm-Setup-{#AppVersion}
SetupIconFile=..\src\Compositor.App\Assets\Compositor.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
LicenseFile=..\LICENSE
WizardStyle=modern
WizardSizePercent=110

Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes

; Close a running Layer Form before replacing its files; the updater has already asked to save.
CloseApplications=force
CloseApplicationsFilter=*.exe,*.dll
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; Comment: "Edit images with layers, masks and adjustments"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
; After an in-app update, reopen Layer Form as the user who started it.
Filename: "{app}\{#AppExe}"; Flags: nowait runasoriginaluser; Check: IsAppUpdate

[Code]
function IsAppUpdate: Boolean;
begin
  Result := WizardSilent and (ExpandConstant('{param:UPDATE|0}') = '1');
end;
