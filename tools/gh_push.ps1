# Push to GitHub without the interactive GCM.
# The token is never stored in any file: the askpass wrapper reads it from Credential Manager.
# ASCII-only on purpose: Windows PowerShell 5.1 reads UTF-8 without BOM as ANSI.
$ErrorActionPreference = 'Stop'
$askpass = Join-Path $env:TEMP ('ks_askpass_' + [guid]::NewGuid().ToString('N') + '.cmd')
$helper = Join-Path $PSScriptRoot 'gh_askpass.ps1'
try {
    $content = "@echo off`r`npowershell -NoProfile -ExecutionPolicy Bypass -File `"$helper`" %*`r`n"
    [IO.File]::WriteAllText($askpass, $content, [Text.Encoding]::ASCII)
    $env:GIT_ASKPASS = $askpass
    $env:GIT_TERMINAL_PROMPT = '0'
    git -c credential.helper= push -u origin master
    exit $LASTEXITCODE
}
finally {
    if (Test-Path $askpass) { Remove-Item $askpass -Force }
}
