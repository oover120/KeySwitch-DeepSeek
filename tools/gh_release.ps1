# Creates a GitHub release and uploads the exe into it.
# The token is read from Credential Manager and never printed.
# ASCII-only on purpose: Windows PowerShell 5.1 reads UTF-8 without BOM as ANSI.
param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [Parameter(Mandatory = $true)][string]$Name,
    [Parameter(Mandatory = $true)][string]$BodyFile,
    [Parameter(Mandatory = $true)][string]$Asset
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class CredRel {
  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
  public struct CREDENTIAL {
    public int Flags; public int Type; public string TargetName; public string Comment;
    public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
    public int CredentialBlobSize; public IntPtr CredentialBlob;
    public int Persist; public int AttributeCount; public IntPtr Attributes;
    public string TargetAlias; public string UserName;
  }
  [DllImport("advapi32", CharSet = CharSet.Unicode, SetLastError = true)]
  public static extern bool CredRead(string target, int type, int flags, out IntPtr credential);
  [DllImport("advapi32", SetLastError = true)]
  public static extern void CredFree(IntPtr buffer);
}
"@

$ptr = [IntPtr]::Zero
if (-not [CredRel]::CredRead('GitHub - https://api.github.com/oover120', 1, 0, [ref]$ptr)) { throw 'CredRead failed' }
$cred = [Runtime.InteropServices.Marshal]::PtrToStructure($ptr, [type][CredRel+CREDENTIAL])
$blob = New-Object byte[] $cred.CredentialBlobSize
[Runtime.InteropServices.Marshal]::Copy($cred.CredentialBlob, $blob, 0, $cred.CredentialBlobSize)
[CredRel]::CredFree($ptr)
$token = [Text.Encoding]::UTF8.GetString($blob).Trim()

$repo = 'oover120/KeySwitch-DeepSeek'
$body = [IO.File]::ReadAllText($BodyFile, [Text.Encoding]::UTF8)
$payload = @{ tag_name = $Tag; name = $Name; body = $body; draft = $false; prerelease = $false } | ConvertTo-Json -Depth 4
$bytes = [Text.Encoding]::UTF8.GetBytes($payload)

$client = [System.Net.Http.HttpClient]::new()
$client.DefaultRequestHeaders.UserAgent.ParseAdd('KeySwitchDeepSeek-Release')
$client.DefaultRequestHeaders.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $token)

$content = [System.Net.Http.ByteArrayContent]::new([byte[]]$bytes)
$content.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::new('application/json')
$resp = $client.PostAsync("https://api.github.com/repos/$repo/releases", $content).GetAwaiter().GetResult()
$text = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
Write-Output ("create_status=" + [int]$resp.StatusCode)
if (-not $resp.IsSuccessStatusCode) { Write-Output $text; exit 1 }
$release = $text | ConvertFrom-Json
Write-Output ("release_id=" + $release.id + " tag=" + $release.tag_name)

$uploadUrl = $release.upload_url -replace '\{.*\}$', ''
$uploadUrl = $uploadUrl + '?name=' + [IO.Path]::GetFileName($Asset)
$stream = [IO.File]::OpenRead($Asset)
try {
    $assetContent = [System.Net.Http.StreamContent]::new($stream)
    $assetContent.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::new('application/octet-stream')
    $up = $client.PostAsync($uploadUrl, $assetContent).GetAwaiter().GetResult()
    $upText = $up.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    Write-Output ("upload_status=" + [int]$up.StatusCode)
    if ($up.IsSuccessStatusCode) {
        $a = $upText | ConvertFrom-Json
        Write-Output ("asset=" + $a.name + " size=" + $a.size + " url=" + $a.browser_download_url)
    } else { Write-Output $upText }
}
finally {
    $stream.Dispose()
    $client.Dispose()
}
