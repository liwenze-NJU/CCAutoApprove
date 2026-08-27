[CmdletBinding()]
param(
    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]$Version = '0.1.0'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$expectedPublishRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $repositoryRoot 'artifacts\publish\win-x64'))
$allowedPublishParent = [System.IO.Path]::GetFullPath(
    (Join-Path $repositoryRoot 'artifacts\publish'))
$allowedPrefix = $allowedPublishParent.TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar

if (-not $expectedPublishRoot.StartsWith(
        $allowedPrefix,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to clean publish output outside the repository: $expectedPublishRoot"
}

$publishLeaf = Split-Path -Leaf $expectedPublishRoot
if ($publishLeaf -ne 'win-x64') {
    throw "Refusing to clean an unexpected publish directory: $expectedPublishRoot"
}

$repositoryPrefix = $repositoryRoot.TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
$pathToInspect = $expectedPublishRoot
while (-not $pathToInspect.Equals(
        $repositoryRoot,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    if (-not $pathToInspect.StartsWith(
            $repositoryPrefix,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to inspect a publish path outside the repository: $pathToInspect"
    }

    if (Test-Path -LiteralPath $pathToInspect) {
        $item = Get-Item -LiteralPath $pathToInspect -Force
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing to clean a redirected publish path: $pathToInspect"
        }
    }

    $parent = [System.IO.Path]::GetDirectoryName($pathToInspect)
    if ([string]::IsNullOrWhiteSpace($parent) -or $parent -eq $pathToInspect) {
        throw "Could not safely walk the publish path: $pathToInspect"
    }

    $pathToInspect = [System.IO.Path]::GetFullPath($parent)
}

$dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop |
    Select-Object -First 1).Source

Push-Location $repositoryRoot
try {
    if (Test-Path -LiteralPath $expectedPublishRoot) {
        $resolvedPublishRoot = (Resolve-Path -LiteralPath $expectedPublishRoot).Path
        if (-not [System.IO.Path]::GetFullPath($resolvedPublishRoot).Equals(
                $expectedPublishRoot,
                [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clean a redirected publish directory: $resolvedPublishRoot"
        }

        Remove-Item -LiteralPath $resolvedPublishRoot -Recurse -Force
    }

    & $dotnet publish 'src/CCAutoApprove.App/CCAutoApprove.App.csproj' `
        -c Release -r win-x64 "-p:Version=$Version" `
        --self-contained true `
        -o 'artifacts/publish/win-x64/app'
    if ($LASTEXITCODE -ne 0) {
        throw "App publish failed with exit code $LASTEXITCODE."
    }

    & $dotnet publish 'src/CCAutoApprove.Cli/CCAutoApprove.Cli.csproj' `
        -c Release -r win-x64 "-p:Version=$Version" `
        --self-contained true `
        -o 'artifacts/publish/win-x64/cli'
    if ($LASTEXITCODE -ne 0) {
        throw "CLI publish failed with exit code $LASTEXITCODE."
    }

    $env:CCAA_PUBLISH_ROOT = $expectedPublishRoot
    $env:CCAA_PUBLISH_VERSION = $Version
    & $dotnet test `
        'tests/CCAutoApprove.Infrastructure.Tests/CCAutoApprove.Infrastructure.Tests.csproj' `
        --no-restore --filter 'FullyQualifiedName~PublishedLayoutTests' -v minimal
    if ($LASTEXITCODE -ne 0) {
        throw "Published layout tests failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}
