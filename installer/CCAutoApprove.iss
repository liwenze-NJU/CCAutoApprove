#define AppName "CCAutoApprove"
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif

[Setup]
AppId={{6B4F8851-B57E-48C8-B922-CEAA7C0AC5AD}
AppName={#AppName}
AppVersion={#AppVersion}
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
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

[CustomMessages]
CloseAppFailed=无法安全停止此安装目录中的 CCAutoApprove。为避免损坏，请关闭应用后重新卸载。
HookCleanupFailed=Hook 清理失败。请打开 %USERPROFILE%\.claude\settings.json，删除引用 CCAutoApprove.Cli.exe hook 的 PermissionRequest 命令，然后保存文件。
StartupCleanupFailed=无法删除 CCAutoApprove 的开机启动项，请稍后在 Windows 启动应用设置中手动删除。
DeleteUserDataPrompt=是否删除 CCAutoApprove 的本地设置、运行状态和日志？
DeleteUserDataFailed=无法删除全部 CCAutoApprove 本地数据，请稍后手动清理 %LOCALAPPDATA%\CCAutoApprove。

[Code]
var
  DeleteUserData: Boolean;
  UninstallPrepared: Boolean;

function EscapePowerShellSingleQuoted(const Value: String): String;
begin
  Result := StringChangeEx(Value, '''', '''''', True);
end;

function StopInstalledApplication: Boolean;
var
  AppPath: String;
  PowerShellCommand: String;
  PowerShellParameters: String;
  ResultCode: Integer;
begin
  AppPath := ExpandConstant('{app}\app\CCAutoApprove.App.exe');
  PowerShellCommand :=
    '$ErrorActionPreference = ''Stop''; ' +
    '$target = [IO.Path]::GetFullPath(''' + EscapePowerShellSingleQuoted(AppPath) + '''); ' +
    '$find = { @(' +
      'Get-CimInstance Win32_Process | Where-Object { ' +
        '$_.Name -eq ''CCAutoApprove.App.exe'' -and $_.ExecutablePath -and ' +
        '[IO.Path]::GetFullPath($_.ExecutablePath).Equals(' +
          '$target, [StringComparison]::OrdinalIgnoreCase)' +
      ' }' +
    ') }; ' +
    '$processes = & $find; ' +
    'foreach ($process in $processes) { ' +
      'Stop-Process -Id $process.ProcessId -Force -ErrorAction Stop' +
    ' }; ' +
    'if ($processes.Count -gt 0) { ' +
      'Wait-Process -Id $processes.ProcessId -Timeout 5 -ErrorAction SilentlyContinue' +
    ' }; ' +
    'if (@(& $find).Count -gt 0) { exit 1 }';
  PowerShellParameters :=
    '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "' +
    PowerShellCommand + '"';

  if not Exec(
    ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    PowerShellParameters,
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode) then
  begin
    Result := False;
    Exit;
  end;
  Result := ResultCode = 0;
end;

function RunHookCleanup: Boolean;
var
  CliPath: String;
  ResultCode: Integer;
begin
  CliPath := ExpandConstant('{app}\cli\CCAutoApprove.Cli.exe');
  if not FileExists(CliPath) then
  begin
    Result := False;
    Exit;
  end;
  if not Exec(CliPath, 'uninstall', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    Result := False;
    Exit;
  end;
  if ResultCode <> 0 then
  begin
    Result := False;
    Exit;
  end;
  Result := True;
end;

procedure PrepareUninstall;
var
  StartupKey: String;
begin
  if UninstallPrepared then
    Exit;

  if not StopInstalledApplication then
  begin
    MsgBox(CustomMessage('CloseAppFailed'), mbError, MB_OK);
    Abort;
  end;

  if not RunHookCleanup then
    MsgBox(CustomMessage('HookCleanupFailed'), mbError, MB_OK);

  StartupKey := 'Software\Microsoft\Windows\CurrentVersion\Run';
  if RegValueExists(HKCU, StartupKey, 'CCAutoApprove') and
     (not RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'CCAutoApprove')) then
    MsgBox(CustomMessage('StartupCleanupFailed'), mbError, MB_OK);

  UninstallPrepared := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    PrepareUninstall
  else if CurUninstallStep = usPostUninstall then
  begin
    if not UninstallSilent then
      DeleteUserData := MsgBox(
        CustomMessage('DeleteUserDataPrompt'),
        mbConfirmation,
        MB_YESNO) = IDYES;

    if DeleteUserData then
    begin
      if not DelTree(ExpandConstant('{localappdata}\CCAutoApprove'), True, True, True) then
        MsgBox(CustomMessage('DeleteUserDataFailed'), mbError, MB_OK);
    end;
  end;
end;
