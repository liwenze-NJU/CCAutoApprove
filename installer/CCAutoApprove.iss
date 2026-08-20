#define AppName "CCAutoApprove"
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif

[Setup]
AppId={{6B4F8851-B57E-48C8-B922-CEAA7C0AC5AD}
AppName={#AppName}
AppVersion={#AppVersion}
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\CCAutoApprove
DefaultGroupName=CCAutoApprove
OutputDir=..\artifacts\installer
OutputBaseFilename=CCAutoApprove-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes

[Files]
Source: "..\artifacts\publish\win-x64\app\*"; DestDir: "{app}\app"; Flags: ignoreversion recursesubdirs
Source: "..\artifacts\publish\win-x64\cli\*"; DestDir: "{app}\cli"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{group}\CCAutoApprove"; Filename: "{app}\app\CCAutoApprove.App.exe"

[Run]
Filename: "{app}\app\CCAutoApprove.App.exe"; Description: "启动 CCAutoApprove"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\cli\CCAutoApprove.Cli.exe"; Parameters: "uninstall"; Flags: runhidden; RunOnceId: "RemoveClaudeHook"

[Code]
var
  DeleteUserData: Boolean;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'CCAutoApprove');
    if UninstallSilent then
      DeleteUserData := False
    else
      DeleteUserData := MsgBox(
        '是否删除 CCAutoApprove 的本地设置、运行状态和日志？',
        mbConfirmation,
        MB_YESNO) = IDYES;
  end
  else if CurUninstallStep = usPostUninstall then
  begin
    if DeleteUserData then
      DelTree(ExpandConstant('{localappdata}\CCAutoApprove'), True, True, True);
  end;
end;
