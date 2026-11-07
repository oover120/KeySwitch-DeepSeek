<#
.SYNOPSIS
    Builds KeySwitch DeepSeek into a single self-contained exe and places it in artifacts\publish.

.DESCRIPTION
    Publish settings: Release, win-x64, self-contained, single-file with embedded native
    libraries. The .NET runtime is not required next to the exe.

    Tests run before publishing when the test project exists; skip them with -SkipTests.

    NOTE: this script is intentionally ASCII-only. Windows PowerShell 5.1 reads UTF-8 files
    without a BOM as ANSI, and Cyrillic text then breaks parsing; ASCII keeps it runnable
    in both Windows PowerShell 5.1 and PowerShell 7.

.PARAMETER Configuration
    Build configuration. Default: Release.

.PARAMETER RuntimeIdentifier
    Publish RID. Default: win-x64.

.PARAMETER SkipTests
    Do not run tests before publishing.

.PARAMETER Zip
    Also pack artifacts\publish into a zip archive.

.EXAMPLE
    .\publish.ps1

.EXAMPLE
    .\publish.ps1 -SkipTests -Zip
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$RuntimeIdentifier = 'win-x64',
    [switch]$SkipTests,
    [switch]$Zip
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = $PSScriptRoot
$project = Join-Path $root 'src\KeySwitch.App\KeySwitch.App.csproj'
$testProject = Join-Path $root 'src\KeySwitch.Tests\KeySwitch.Tests.csproj'
$artifacts = Join-Path $root 'artifacts'
$publishDir = Join-Path $artifacts 'publish'
$exeName = 'KeySwitchDeepSeek.exe'

function Get-ProjectVersion {
    param([string]$PropsFile)

    if (Test-Path -LiteralPath $PropsFile) {
        $match = Select-String -LiteralPath $PropsFile -Pattern '<Version>\s*([^<]+)\s*</Version>' | Select-Object -First 1
        if ($match) {
            return $match.Matches[0].Groups[1].Value.Trim()
        }
    }

    return '1.0.0'
}

$version = Get-ProjectVersion -PropsFile (Join-Path $root 'Directory.Build.props')

Write-Host "KeySwitch DeepSeek $version - publishing ($Configuration, $RuntimeIdentifier)" -ForegroundColor Cyan

# Clean the previous publish output so stale files never leak into the single-file bundle.
if (Test-Path -LiteralPath $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}

New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

if (-not $SkipTests -and (Test-Path -LiteralPath $testProject)) {
    Write-Host 'Running tests...' -ForegroundColor Cyan
    dotnet test $testProject -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "Tests failed with exit code $LASTEXITCODE - publish aborted."
    }
}

Write-Host 'dotnet publish...' -ForegroundColor Cyan
dotnet publish $project `
    -c $Configuration `
    -r $RuntimeIdentifier `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=false `
    -p:DebugType=embedded `
    --nologo `
    -o $publishDir

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$exePath = Join-Path $publishDir $exeName
if (-not (Test-Path -LiteralPath $exePath)) {
    throw "Publish output does not contain $exeName in $publishDir."
}

$exe = Get-Item -LiteralPath $exePath
Write-Host ("Done: {0} ({1:N1} MB)" -f $exe.FullName, ($exe.Length / 1MB)) -ForegroundColor Green

Get-ChildItem -LiteralPath $publishDir -File | ForEach-Object {
    Write-Host ("  {0,-40} {1,10:N0} bytes" -f $_.Name, $_.Length)
}

if ($Zip) {
    $zipPath = Join-Path $artifacts ("KeySwitchDeepSeek-{0}-{1}.zip" -f $version, $RuntimeIdentifier)
    if (Test-Path -LiteralPath $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }

    Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath -CompressionLevel Optimal
    Write-Host "Archive: $zipPath" -ForegroundColor Green
}

Write-Host 'Installer: compile installer\KeySwitchDeepSeek.iss with Inno Setup 6 (Compile).' -ForegroundColor DarkGray
