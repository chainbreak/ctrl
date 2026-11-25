#define AppVersion "0.1.2"
[Setup]
AppId=CtrlMessengerDesktop
AppName=Ctrl Messenger
AppVerName=Ctrl Messenger beta 0.1.2
AppVersion={#AppVersion}
AppPublisher=Ctrl Messenger
VersionInfoVersion={#AppVersion}.0
DefaultDirName={localappdata}\Programs\Ctrl Messenger
DefaultGroupName=Ctrl Messenger
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
AllowNoIcons=yes
DisableProgramGroupPage=yes
DisableReadyPage=no
WizardStyle=modern
Compression=lzma2/fast
SolidCompression=yes
DiskSpanning=no
OutputDir=..\..
OutputBaseFilename=Ctrl-Messenger-{#AppVersion}-Setup
SetupIconFile=..\desktop\ctrl.ico
UninstallDisplayIcon={app}\Ctrl Messenger.exe
CloseApplications=no

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Files]
Source: "..\..\Ctrl-{#AppVersion}.exe"; DestDir: "{app}"; DestName: "Ctrl Messenger.exe"; Flags: ignoreversion

[Icons]
Name: "{group}\Ctrl Messenger"; Filename: "{app}\Ctrl Messenger.exe"; WorkingDir: "{app}"

