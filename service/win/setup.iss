; Inno Setup installer for TicTack File Sync Service
; Requires Inno Setup 6+ (https://jrsoftware.org/isdl.php)
; Build: iscc setup.iss

#define MyAppName "TicTack File Sync"
#define MyAppVersion "2.0.0"
#define MyAppPublisher "TicTack"
#define MyAppExeName "TicTackSv.exe"

[Setup]
AppId={{B8F4C3A1-2D5E-4F7A-9B6C-1D3E5F7A9B0C}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={pf64}\TicTack
DefaultGroupName=TicTack
UninstallDisplayIcon={app}\TicTackSv.exe
Compression=lzma2
SolidCompression=yes
OutputDir=.
OutputBaseFilename=TicTackSetup-{#MyAppVersion}
PrivilegesRequired=admin
AllowNoIcons=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "TicTackSv.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "config.yaml"; DestDir: "{app}"; Flags: ignoreversion
Source: "*.dll"; DestDir: "{app}"; Flags: ignoreversion

[Run]
; Install and start the service
Filename: "{sys}\sc.exe"; Parameters: "create TicTackSv binPath= ""{app}\TicTackSv.exe"" start= auto obj= LocalSystem DisplayName= ""TicTack File Sync"""; Flags: runhidden
Filename: "{sys}\sc.exe"; Parameters: "description TicTackSv ""Watches folders and syncs files to destinations.""";
Filename: "{sys}\sc.exe"; Parameters: "failure TicTackSv reset= 60 actions= restart/5000/restart/10000/restart/30000"; Flags: runhidden
Filename: "{sys}\sc.exe"; Parameters: "start TicTackSv"; Flags: runhidden

[UninstallRun]
Filename: "{sys}\sc.exe"; Parameters: "stop TicTackSv"; Flags: runhidden; RunOnceId: "StopTicTack"
Filename: "{sys}\sc.exe"; Parameters: "delete TicTackSv"; Flags: runhidden; RunOnceId: "DeleteTicTack"

[Icons]
Name: "{group}\Start TicTack Service"; Filename: "{sys}\sc.exe"; Parameters: "start TicTackSv"
Name: "{group}\Stop TicTack Service"; Filename: "{sys}\sc.exe"; Parameters: "stop TicTackSv"
Name: "{group}\Uninstall TicTack"; Filename: "{uninstallexe}"
