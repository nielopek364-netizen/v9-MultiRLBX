; v9 MultiRLBX - Inno Setup installer script
; Build with Inno Setup 6+: ISCC.exe installer\MultiRLBX.iss
; Expects a compiled "v9 MultiRLBX.exe" in the project root (run Build.bat first).
; Output: installer\Output\v9-MultiRLBX-Setup.exe (per-user install, no admin required).

#define AppName "v9 MultiRLBX"
#define AppVersion "1.0.0"
#define AppExe "v9 MultiRLBX.exe"
#define AppAuthor "v9pv"
#define AppUrl "https://github.com/nielopek364-netizen/"

[Setup]
AppId={{7E4D2A91-5C38-4F6B-9A21-M9RLBX92V1}}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppAuthor}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}
AppUpdatesURL={#AppUrl}
DefaultDirName={localappdata}\Programs\MultiRLBX
DefaultGroupName=v9 MultiRLBX
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=Output
OutputBaseFilename=v9-MultiRLBX-Setup
SetupIconFile=..\app.ico
UninstallDisplayIcon={app}\app.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
Source: "..\v9 MultiRLBX.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\app.ico";           DestDir: "{app}"; Flags: ignoreversion
Source: "..\ui\app.html";       DestDir: "{app}\ui"; Flags: ignoreversion
Source: "..\README.md";         DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\LICENSE";           DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

[Icons]
Name: "{group}\v9 MultiRLBX"; Filename: "{app}\{#AppExe}"; IconFilename: "{app}\app.ico"
Name: "{userdesktop}\v9 MultiRLBX"; Filename: "{app}\{#AppExe}"; IconFilename: "{app}\app.ico"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Start v9 MultiRLBX"; Flags: nowait postinstall skipifsilent
