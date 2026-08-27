[CmdletBinding()]
param(
    [Parameter()]
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$temporaryRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())

function Test-IsWithin([string]$candidate, [string]$root) {
    $normalizedRoot = $root.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $prefix = $normalizedRoot + [System.IO.Path]::DirectorySeparatorChar
    return $candidate.Equals($normalizedRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
        $candidate.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)
}

function Assert-NoRedirectedAncestor([string]$candidate) {
    $current = [System.IO.Path]::GetDirectoryName($candidate)
    while (-not [string]::IsNullOrWhiteSpace($current)) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing to write through a redirected directory: $current"
            }
        }

        if ((Test-IsWithin $current $repositoryRoot) -and
            $current.Equals($repositoryRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
            break
        }
        if ((Test-IsWithin $current $temporaryRoot) -and
            $current.Equals(
                $temporaryRoot.TrimEnd(
                    [System.IO.Path]::DirectorySeparatorChar,
                    [System.IO.Path]::AltDirectorySeparatorChar),
                [System.StringComparison]::OrdinalIgnoreCase)) {
            break
        }

        $parent = [System.IO.Path]::GetDirectoryName($current)
        if ([string]::IsNullOrWhiteSpace($parent) -or $parent -eq $current) {
            break
        }
        $current = $parent
    }
}

Write-Host 'CCAutoApprove 真实 Claude 十步人工验收'
Write-Host '此脚本不会启动或控制 Claude，不会安装/卸载 Hook，不会写真实 Claude 设置或注册表，不会删除用户数据，也不会发送终端输入。'
Write-Host '请你在自己的 Claude 窗口中完成每步动作，然后回到此窗口输入 y 或 n。当前仓库没有预填任何答案。'

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $suggested = Join-Path $repositoryRoot (
        'artifacts\manual-acceptance\acceptance-{0:yyyyMMdd-HHmmss}.json' -f (Get-Date))
    $entered = Read-Host "验收记录路径（必须位于仓库或临时目录；直接回车使用 $suggested）"
    $OutputPath = if ([string]::IsNullOrWhiteSpace($entered)) { $suggested } else { $entered }
}

$pathToResolve = if ([System.IO.Path]::IsPathRooted($OutputPath)) {
    $OutputPath
} else {
    Join-Path $repositoryRoot $OutputPath
}
$resolvedOutput = [System.IO.Path]::GetFullPath($pathToResolve)
if (-not $resolvedOutput.EndsWith('.json', [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'Acceptance output must use a .json extension.'
}
if (-not ((Test-IsWithin $resolvedOutput $repositoryRoot) -or
          (Test-IsWithin $resolvedOutput $temporaryRoot))) {
    throw 'Acceptance output must be inside the repository or the system temporary directory.'
}
Assert-NoRedirectedAncestor $resolvedOutput
if (Test-Path -LiteralPath $resolvedOutput) {
    throw 'Acceptance output must be a new file; choose a path that does not already exist.'
}

$steps = @(
    '1. Hook absent → Claude prompts normally.',
    '2. Hook installed, App stopped → Claude prompts normally.',
    '3. App running, disabled → Claude prompts normally.',
    '4. App enabled for another directory → Claude prompts normally.',
    '5. App enabled for selected directory → harmless permission request is allowed.',
    '6. Pause → next request prompts normally.',
    '7. Force-close App, wait 10 seconds → next request prompts normally.',
    '8. /clear → selected-directory approval still works while enabled.',
    '9. Two Claude sessions in selected directory → both work.',
    '10. Uninstall Hook → normal prompting is restored.'
)

$results = [System.Collections.Generic.List[object]]::new()
for ($index = 0; $index -lt $steps.Count; $index++) {
    $step = $steps[$index]
    Write-Host ''
    Write-Host $step
    Write-Host '请人工准备所述状态，并在真实 Claude 中触发一个无害权限请求。'
    do {
        $answer = (Read-Host '实际结果符合描述吗？[y/n]').Trim().ToLowerInvariant()
    } while ($answer -notin @('y', 'n'))

    $results.Add([pscustomobject]@{
        StepNumber = $index + 1
        Text = $step
        Passed = $answer -eq 'y'
    })
}

$record = [pscustomobject]@{
    SchemaVersion = 1
    RecordedAtUtc = [DateTimeOffset]::UtcNow
    PassedCount = @($results | Where-Object Passed).Count
    TotalCount = $results.Count
    Results = $results
}

$parentDirectory = [System.IO.Path]::GetDirectoryName($resolvedOutput)
if (-not (Test-Path -LiteralPath $parentDirectory)) {
    New-Item -ItemType Directory -Path $parentDirectory | Out-Null
}
$record | ConvertTo-Json -Depth 5 |
    Out-File -LiteralPath $resolvedOutput -Encoding utf8 -NoClobber

Write-Host ''
Write-Host "已记录 $($record.PassedCount)/$($record.TotalCount) 项为通过：$resolvedOutput"
if ($record.PassedCount -ne $record.TotalCount) {
    Write-Warning '并非全部检查通过；请保留记录并先排查失败项。'
    exit 1
}
