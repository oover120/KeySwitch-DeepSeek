# Печатает ответ для git credential: логин или токен.
# Токен читается напрямую из Windows Credential Manager и никогда не попадает в файл.
param([string]$Prompt)

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class Cred {
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

if ($Prompt -match 'Username') {
    Write-Output 'oover120'
    exit 0
}

$target = 'GitHub - https://api.github.com/oover120'
$ptr = [IntPtr]::Zero
if (-not [Cred]::CredRead($target, 1, 0, [ref]$ptr)) { exit 1 }
$cred = [Runtime.InteropServices.Marshal]::PtrToStructure($ptr, [type][Cred+CREDENTIAL])
$blob = New-Object byte[] $cred.CredentialBlobSize
[Runtime.InteropServices.Marshal]::Copy($cred.CredentialBlob, $blob, 0, $cred.CredentialBlobSize)
[Cred]::CredFree($ptr)
Write-Output ([Text.Encoding]::UTF8.GetString($blob).Trim())
