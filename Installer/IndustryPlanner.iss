; Inno Setup script for IndustryPlanner (https://jrsoftware.org/isinfo.php)
; 1. Build the game in Unity to Build\Windows (File > Build Profiles > Windows > Build).
; 2. Open this file in Inno Setup and press Compile (or run: iscc Installer\IndustryPlanner.iss).
; 3. The installer appears in Installer\Output\.

#define AppName "IndustryPlanner"
#define AppVersion "0.0.1"
#define AppPublisher "Benji"
#define AppExe "IndustryPlanner.exe"          ; Must match Product Name in Player Settings
#define BuildDir "..\My project\Build\Windows"

[Setup]
AppId={{D053D840-760F-42E0-961E-6F95391A4CEB}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}
OutputDir=Output
OutputBaseFilename={#AppName}-{#AppVersion}-Setup
SetupIconFile=AppIcon.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequiredOverridesAllowed=dialog

[Languages]
Name: "french"; MessagesFile: "compiler:Languages\French.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
; Everything Unity produced: the exe, UnityPlayer.dll, the _Data folder, MonoBleedingEdge...
Source: "{#BuildDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*_BurstDebugInformation_DoNotShip\*,*_BackUpThisFolder_ButDontShipItWithYourGame\*"

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
