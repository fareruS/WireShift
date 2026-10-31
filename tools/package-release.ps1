<#
.SYNOPSIS
    Builds the two WireShift release editions and packages each as a ZIP.

.DESCRIPTION
    Portable   - framework-dependent. Small download, requires the .NET 8 Desktop Runtime.
    Standalone - self-contained single-file. No runtime needed, much larger download.

    Each ZIP contains one folder holding both executables side by side, so the UI finds
    NetBinder.Service.exe on its own and starts it automatically.

.PARAMETER Dotnet
    Path to the dotnet executable. Defaults to the one on PATH.

.PARAMETER Configuration
    Build configuration. Defaults to Release.

.EXAMPLE
    ./tools/package-release.ps1

.EXAMPLE
    ./tools/package-release.ps1 -Dotnet 'C:\path\to\dotnet.exe'
#>
[CmdletBinding()]
param(
    [string] $Dotnet = 'dotnet',
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$version = ([xml](Get-Content -Raw (Join-Path $repoRoot 'Directory.Build.props'))).Project.PropertyGroup.Version
if (-not $version) { throw 'Version not found in Directory.Build.props' }

$artifacts = Join-Path $repoRoot 'artifacts'
$work = Join-Path $artifacts 'work'
Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $work, $artifacts | Out-Null

function Invoke-Publish {
    param(
        [string] $Project,
        [string] $Output,
        [bool] $SelfContained
    )

    # NOTE: PublishSingleFile on its own implies SelfContained=true, which silently turns
    # the portable edition into a second copy of the standalone build. Always state the
    # RID and SelfContained explicitly.
    $selfContainedFlag = if ($SelfContained) { 'true' } else { 'false' }

    $arguments = @(
        'publish', $Project,
        '-c', $Configuration,
        '--nologo',
        '-r', 'win-x64',
        '--self-contained', $selfContainedFlag,
        '-p:PublishSingleFile=true',
        # Trimming is off: the WPF UI and the reflection-based JSON protocol need it that way.
        '-p:PublishTrimmed=false',
        '-o', $Output
    )

    & $Dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $Project" }
}

function New-Edition {
    param(
        [string] $Name,
        [bool] $SelfContained,
        [string] $RuntimeNote
    )

    $folderName = "WireShift-$version-$Name-win-x64"
    $staging = Join-Path $work $folderName
    New-Item -ItemType Directory -Force -Path $staging | Out-Null

    Invoke-Publish -Project (Join-Path $repoRoot 'src/NetBinder.Service/NetBinder.Service.csproj') `
        -Output (Join-Path $work "$Name-service") -SelfContained $SelfContained
    Invoke-Publish -Project (Join-Path $repoRoot 'src/NetBinder.UI/NetBinder.UI.csproj') `
        -Output (Join-Path $work "$Name-ui") -SelfContained $SelfContained

    # One folder for both executables: the UI locates the service beside itself.
    Copy-Item (Join-Path $work "$Name-service/*") $staging -Recurse -Force
    Copy-Item (Join-Path $work "$Name-ui/*") $staging -Recurse -Force

    # Debug symbols are not shipped.
    Get-ChildItem $staging -Filter *.pdb | Remove-Item -Force

    Copy-Item (Join-Path $repoRoot 'LICENSE') $staging -Force

    $launcher = @'
@echo off
rem Starts WireShift. NetBinder.UI.exe requests Administrator elevation via its manifest
rem and launches NetBinder.Service.exe automatically.
start "WireShift" "%~dp0NetBinder.UI.exe"
'@
    Set-Content -LiteralPath (Join-Path $staging 'WireShift.bat') -Value $launcher -Encoding ascii

    $readme = @"
WireShift $version - $Name edition
========================================

How to run
----------
1. Keep this folder together; NetBinder.UI.exe looks for NetBinder.Service.exe next to itself.
2. Double-click WireShift.bat (or NetBinder.UI.exe) and accept the Administrator prompt.
3. The UI starts the background service automatically. Wait for "Connected to NetBinder Service".

$RuntimeNote

Notes
-----
- Both executables must run as Administrator: WinDivert and WFP require it.
- Closing the UI leaves the service running so active bindings keep working.
- Configuration is stored in %APPDATA%\NetBinder\config.json.
- Logs from the service are written to its console window; run NetBinder.Service.exe directly
  from a terminal when you need to see them.

License
-------
MIT - see LICENSE. Bundles WinDivert (LGPLv3) by basil00: https://github.com/basil00/Divert
"@
    Set-Content -LiteralPath (Join-Path $staging 'QUICKSTART.txt') -Value $readme -Encoding utf8

    $zipPath = Join-Path $artifacts "WireShift-$version-$Name-win-x64.zip"
    Remove-Item -Force $zipPath -ErrorAction SilentlyContinue
    Compress-Archive -Path $staging -DestinationPath $zipPath -CompressionLevel Optimal

    [pscustomobject]@{
        Edition = $Name
        Zip     = $zipPath
        SizeMB  = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
        Files   = (Get-ChildItem $staging -Recurse -File).Count
    }
}

$results = @(
    New-Edition -Name 'portable' -SelfContained $false `
        -RuntimeNote 'Requires the .NET 8 Desktop Runtime (x64) to be installed.'
    New-Edition -Name 'standalone' -SelfContained $true `
        -RuntimeNote 'No .NET installation required; the runtime is bundled.'
)

$results | Format-Table -AutoSize
Write-Host "Artifacts: $artifacts"
