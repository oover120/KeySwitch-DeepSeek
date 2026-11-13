# Сохраняет новый токен GitHub в Windows Credential Manager.
# Запускать в своём терминале: токен вводится скрыто и никуда не печатается.
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\gh_set_token.ps1
$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class CredWriteApi {
  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
  public struct CREDENTIAL {
    public int Flags; public int Type; public string TargetName; public string Comment;
    public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
    public int CredentialBlobSize; public IntPtr CredentialBlob;
    public int Persist; public int AttributeCount; public IntPtr Attributes;
    public string TargetAlias; public string UserName;
  }
  [DllImport("advapi32", CharSet = CharSet.Unicode, SetLastError = true)]
  public static extern bool CredWrite(ref CREDENTIAL credential, int flags);
}
"@

$target = 'GitHub - https://api.github.com/oover120'
$user = 'oover120'

Write-Host 'Вставьте новый токен GitHub (ввод скрыт), Enter — сохранить:' -ForegroundColor Cyan
$secure = Read-Host -AsSecureString
$bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
try {
    $token = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr).Trim()
}
finally {
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
}

if ([string]::IsNullOrWhiteSpace($token)) { Write-Host 'Пусто — ничего не сохранено.' -ForegroundColor Yellow; exit 1 }

$bytes = [Text.Encoding]::UTF8.GetBytes($token)
$blob = [Runtime.InteropServices.Marshal]::AllocHGlobal($bytes.Length)
try {
    [Runtime.InteropServices.Marshal]::Copy($bytes, 0, $blob, $bytes.Length)
    $cred = New-Object CredWriteApi+CREDENTIAL
    $cred.Type = 1                 # CRED_TYPE_GENERIC
    $cred.TargetName = $target
    $cred.UserName = $user
    $cred.Comment = 'KeySwitch DeepSeek: публикация релизов'
    $cred.CredentialBlob = $blob
    $cred.CredentialBlobSize = $bytes.Length
    $cred.Persist = 2              # CRED_PERSIST_LOCAL_MACHINE

    if (-not [CredWriteApi]::CredWrite([ref]$cred, 0)) {
        throw "CredWrite: $([ComponentModel.Win32Exception]::new([Runtime.InteropServices.Marshal]::GetLastWin32Error()).Message)"
    }
}
finally {
    [Runtime.InteropServices.Marshal]::ZeroFreeGlobalAllocUnicode($blob)
}

Write-Host 'Токен сохранён. Проверяю доступ к GitHub…' -ForegroundColor Cyan
try {
    $resp = Invoke-WebRequest -Uri 'https://api.github.com/user' -Headers @{ Authorization = "Bearer $token"; 'User-Agent' = 'KeySwitchDeepSeek' } -UseBasicParsing
    $scopes = $resp.Headers['x-oauth-scopes']
    $login = ($resp.Content | ConvertFrom-Json).login
    Write-Host "OK: вход как $login, права: $scopes" -ForegroundColor Green
}
catch {
    Write-Host "Токен сохранён, но GitHub ответил ошибкой: $($_.Exception.Message)" -ForegroundColor Yellow
}
