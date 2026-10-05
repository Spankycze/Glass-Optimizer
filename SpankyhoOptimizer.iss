#define MyAppName "Spankyho Optimizer"
#define MyAppVersion "15.0.0"
#define MyAppExeName "SpankyhoOptimizer.exe"
[Setup]
AppId={{7E63BD2A-5F38-4DC0-A7A9-1CB7A8309C42}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
DefaultDirName={autopf}\Spankyho Optimizer
DefaultGroupName={#MyAppName}
OutputDir=dist-installer
OutputBaseFilename=Spankyho Optimizer Setup
SetupIconFile=src\SpankyhoOptimizer\Assets\App.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no
[Tasks]
Name: "desktopicon"; Description: "Vytvořit zástupce na ploše"; GroupDescription: "Další možnosti:"; Flags: unchecked
[Files]
Source: "publish-installer\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Icons]
Name: "{group}\Spankyho Optimizer"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\Spankyho Optimizer"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Spustit Spankyho Optimizer"; Flags: nowait postinstall skipifsilent
