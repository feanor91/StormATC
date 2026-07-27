#ifndef AppVersion
  #error AppVersion must be supplied by New-StormATCInstaller.ps1
#endif

#ifndef NumericVersion
  #error NumericVersion must be supplied by New-StormATCInstaller.ps1
#endif

#ifndef SourceDir
  #error SourceDir must be supplied by New-StormATCInstaller.ps1
#endif

#ifndef OutputDir
  #error OutputDir must be supplied by New-StormATCInstaller.ps1
#endif

#define AppName "StormATC"
#define AppPublisher "StormATC Contributors"
#define AppExeName "StormATC.exe"

[Setup]
AppId={{895FCE25-4782-4E30-9EED-D8566DB6129C}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#NumericVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription=Flight simulation ATC companion
VersionInfoProductName={#AppName}
VersionInfoCopyright=Copyright (c) StormATC Contributors
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename=StormATC-Setup-v{#AppVersion}-win-x64
SetupIconFile=..\assets\branding\StormATC.ico
LicenseFile=..\LICENSE
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
UsePreviousAppDir=yes
Uninstallable=yes
UninstallDisplayIcon={app}\{#AppExeName}
ChangesAssociations=no
ChangesEnvironment=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\StormATC"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\StormATC"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,StormATC}"; Flags: nowait postinstall skipifsilent
