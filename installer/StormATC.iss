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
; Setup enables Windows' RedirectionGuard by default, which blocks NTFS junctions. MSFS from the
; Microsoft Store keeps its Community folder behind one (LocalCache is a junction to the drive the
; game is installed on), so with the guard Setup could not install the toolbar panel and the module
; there, and StormATC started by Setup inherited the restriction and could not find the folder either.
; Setup runs without administrator rights and only writes to the user's own folders.
RedirectionGuard=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "french"; MessagesFile: "compiler:Languages\French.isl"

[CustomMessages]
english.TaskMsfsPanel=Install the StormATC panel in the MSFS 2024 toolbar
english.TaskMsfsPanelGroup=Microsoft Flight Simulator
english.TaskLocalVoice=Download local speech recognition and voices (Whisper, Piper and 15 voices, about 1 GB)
english.TaskLocalVoiceGroup=Voice (works offline, nothing is sent anywhere)
english.DownloadFailed=The download failed:%n%n%1%n%nContinue without the local voice? You can download it later from StormATC's settings.
english.ExtractFailed=The downloaded files could not be unpacked. The local voice was not installed; you can download it later from StormATC's settings.
french.TaskMsfsPanel=Installer le panneau StormATC dans la barre d'outils de MSFS 2024
french.TaskMsfsPanelGroup=Microsoft Flight Simulator
french.TaskLocalVoice=Télécharger la reconnaissance vocale et les voix locales (Whisper, Piper et 15 voix, environ 1 Go)
french.TaskLocalVoiceGroup=Voix (fonctionne hors ligne, rien n'est envoyé)
french.DownloadFailed=Le téléchargement a échoué :%n%n%1%n%nContinuer sans la voix locale ? Vous pourrez la télécharger plus tard depuis les paramètres de StormATC.
french.ExtractFailed=Les fichiers téléchargés n'ont pas pu être décompressés. La voix locale n'est pas installée ; vous pourrez la télécharger plus tard depuis les paramètres de StormATC.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "msfspanel"; Description: "{cm:TaskMsfsPanel}"; GroupDescription: "{cm:TaskMsfsPanelGroup}"; Check: FoundAnyCommunityFolder
Name: "localvoice"; Description: "{cm:TaskLocalVoice}"; GroupDescription: "{cm:TaskLocalVoiceGroup}"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

; The MSFS toolbar panel goes into the Community folder of each MSFS 2024 installation found
; (Microsoft Store and Steam). Setup removes it again on uninstall.
Source: "{#SourceDir}\msfs-panel\stormatc-ingamepanel\*"; DestDir: "{code:StoreCommunityFolder}\stormatc-ingamepanel"; Tasks: msfspanel; Check: HasStoreCommunityFolder; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SourceDir}\msfs-panel\stormatc-ingamepanel\*"; DestDir: "{code:SteamCommunityFolder}\stormatc-ingamepanel"; Tasks: msfspanel; Check: HasSteamCommunityFolder; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\StormATC"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\StormATC"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,StormATC}"; Flags: nowait postinstall skipifsilent

#include "StormATC.code.iss"
