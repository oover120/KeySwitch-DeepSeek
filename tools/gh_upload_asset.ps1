# Uploads the second asset (portable exe) into an existing release.
# ASCII-only on purpose: Windows PowerShell 5.1 reads UTF-8 without BOM as ANSI.
param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [Parameter(Mandatory = $true)][string]$Asset
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
$token = (powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'gh_askpass.ps1') -Prompt 'x' 2>&1) -join ''
$token = $token.Trim()
$repo = 'oover120/KeySwitch-DeepSeek'
$client = [System.Net.Http.HttpClient]::new()
$client.DefaultRequestHeaders.UserAgent.ParseAdd('KeySwitchDeepSeek-Release')
$client.DefaultRequestHeaders.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $token)
$json = $client.GetStringAsync("https://api.github.com/repos/$repo/releases/tags/$Tag").GetAwaiter().GetResult()
$rel = $json | ConvertFrom-Json
Write-Output ("release_id=" + $rel.id + " existing_assets=" + $rel.assets.Count)
$uploadUrl = ($rel.upload_url -replace '\{.*\}$', '') + '?name=' + [IO.Path]::GetFileName($Asset)
$stream = [IO.File]::OpenRead($Asset)
try {
    $content = [System.Net.Http.StreamContent]::new($stream)
    $content.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::new('application/octet-stream')
    $resp = $client.PostAsync($uploadUrl, $content).GetAwaiter().GetResult()
    $text = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    Write-Output ("upload_status=" + [int]$resp.StatusCode)
    if ($resp.IsSuccessStatusCode) {
        $a = $text | ConvertFrom-Json
        Write-Output ("asset=" + $a.name + " size=" + $a.size + " url=" + $a.browser_download_url)
    } else { Write-Output $text }
}
finally {
    $stream.Dispose()
    $client.Dispose()
}
