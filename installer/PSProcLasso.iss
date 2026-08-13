#define MyAppName "PSProcLasso"
#define MyAppVersion "1.1.0"
#define MyAppPublisher "PSProcLasso"
#define MyAppExeName "PSProcLassoGUI.exe"

[Setup]
AppId={{A24BD745-D9A5-49A2-9138-9B42117BF6A9}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=PSProcLassoSetup-{#MyAppVersion}
SetupIconFile=..\PSProcLassoGUI.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=no
RestartApplications=no
AllowNoIcons=yes
MinVersion=10.0
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
VersionInfoVersion={#MyAppVersion}.0
VersionInfoProductName={#MyAppName}
VersionInfoDescription=PSProcLasso installer
VersionInfoCompany={#MyAppPublisher}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "durableoptimization"; Description: "Keep safe optimization active after Windows sign-in"; GroupDescription: "Background behavior:"; Flags: checkedonce
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "..\PSProcLassoGUI.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\PSProcLassoGUI.ico"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\PSProcLasso"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\PSProcLasso"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Parameters: "--enable-durable-optimization"; WorkingDir: "{app}"; StatusMsg: "Enabling popup-free optimization after sign-in..."; Flags: runhidden waituntilterminated; Tasks: durableoptimization

[UninstallRun]
Filename: "{app}\{#MyAppExeName}"; Parameters: "--disable-durable-optimization"; WorkingDir: "{app}"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveDurableOptimization"
