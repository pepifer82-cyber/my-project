#define MyAppName "A&T Printer Agent"
#define MyAppVersion "0.3.8"
#define MyPublisher "A&T Informatica"
#define MyAppExeName "ATPrinterAgent.exe"

[Setup]
AppId={{7E7D49A0-2DE5-4B88-B9DA-026A7F1C0A26}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyPublisher}
DefaultDirName={autopf}\AT Printer Agent
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x86 x64
ArchitecturesInstallIn64BitMode=x64
OutputDir=.
OutputBaseFilename=Instalar_ATPrinterAgent_v0.3.8
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=src\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
CloseApplications=yes
RestartApplications=no
Uninstallable=yes

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Files]
Source: "publish-x86\ATPrinterAgent.exe"; DestDir: "{app}"; DestName: "{#MyAppExeName}"; Check: not Is64BitInstallMode; Flags: ignoreversion
Source: "publish-x64\ATPrinterAgent.exe"; DestDir: "{app}"; DestName: "{#MyAppExeName}"; Check: Is64BitInstallMode; Flags: ignoreversion
Source: "README.txt"; DestDir: "{app}"; Flags: ignoreversion

[Run]
Filename: "{sys}\taskkill.exe"; Parameters: "/IM ATPrinterAgent.exe /T /F"; Flags: runhidden waituntilterminated
Filename: "{app}\{#MyAppExeName}"; Parameters: "--background"; Flags: nowait runhidden; Description: "Iniciar A&T Printer Agent en segundo plano"

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/IM ATPrinterAgent.exe /T /F"; Flags: runhidden waituntilterminated
Filename: "{sys}\reg.exe"; Parameters: "delete ""HKCU\Software\Microsoft\Windows\CurrentVersion\Run"" /v ATPrinterAgent /f"; Flags: runhidden waituntilterminated

[UninstallDelete]
Type: files; Name: "{app}\ATPrinterAgent.exe"
Type: files; Name: "{app}\README.txt"
Type: dirifempty; Name: "{app}"

[Icons]
Name: "{group}\A&T Printer Agent"; Filename: "{app}\ATPrinterAgent.exe"; IconFilename: "{app}\ATPrinterAgent.exe"

Name: "{group}\Desinstalar A&T Printer Agent"; Filename: "{uninstallexe}"

; Per-user configuration/serial/IP list remains in %LOCALAPPDATA%\ATPrinterAgent on update/uninstall.


[Code]
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
  begin
    { Remove the old v0.2.5 desktop link and old per-user uninstall entry, but keep config.json. }
    DeleteFile(ExpandConstant('{userdesktop}\ATPrinterAgent.lnk'));
    DeleteFile(ExpandConstant('{userdesktop}\A&T Printer Agent.lnk'));
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\ATPrinterAgent');
  end;
end;
