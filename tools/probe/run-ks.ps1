# Diagnostic bench: real target app + real KeySwitch + injected key events.
# Scenario: the window is in EN layout and the user types a Russian word on it
# ("ghbdtn " -> Russian "privet "). Prints what the field got and the KeySwitch log.
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\probe\run-ks.ps1 -Target notepad
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\probe\run-ks.ps1 -Target chrome
#
# ASCII only: Windows PowerShell 5.1 reads UTF-8 without BOM as ANSI.
param(
    [Parameter(Mandatory = $true)][ValidateSet('notepad', 'chrome')][string]$Target,
    [ValidateSet('auto', 'force', 'single')][string]$Scenario = 'auto'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class W {
  [DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr SetFocus(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern IntPtr GetKeyboardLayout(uint tid);
  [DllImport("user32.dll")] public static extern IntPtr LoadKeyboardLayout(string id, uint flags);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
  [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);

  public static string Describe(IntPtr h) {
    if (h == IntPtr.Zero) return "<none>";
    uint pid; GetWindowThreadProcessId(h, out pid);
    string proc = "?";
    try { proc = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { }
    var sb = new System.Text.StringBuilder(256);
    GetWindowText(h, sb, sb.Capacity);
    return "hwnd=0x" + h.ToInt64().ToString("X") + " proc=" + proc + " title=" + sb.ToString();
  }
  public static uint ThreadOf(IntPtr h) { uint tid; GetWindowThreadProcessId(h, out tid); return tid; }
  public static string LayoutOf(IntPtr h) { return "0x" + GetKeyboardLayout(ThreadOf(h)).ToInt64().ToString("X8"); }
  public static string SetLayout(IntPtr h, string id) {
    IntPtr hkl = LoadKeyboardLayout(id, 1);
    PostMessage(h, 0x0050, IntPtr.Zero, hkl);
    return LayoutOf(h);
  }

  public static void TapAlt() {
    keybd_event(0x12, 0, 0, UIntPtr.Zero);
    System.Threading.Thread.Sleep(30);
    keybd_event(0x12, 0, 2, UIntPtr.Zero);
    System.Threading.Thread.Sleep(30);
  }

  // Tap ALT first (gives our thread the last input event), then attach to the
  // target and to the current foreground queue, then take the foreground.
  // Measured: this is the only variant that works while a game holds the focus.
  public static bool Activate(IntPtr h) {
    uint fgPid;
    IntPtr fg = GetForegroundWindow();
    uint fgTid = GetWindowThreadProcessId(fg, out fgPid);
    uint tid = ThreadOf(h);
    uint me = GetCurrentThreadId();

    TapAlt();
    ShowWindow(h, 9);
    AttachThreadInput(me, tid, true);
    AttachThreadInput(me, fgTid, true);
    SetForegroundWindow(h);
    SetFocus(h);
    AttachThreadInput(me, fgTid, false);
    AttachThreadInput(me, tid, false);
    return GetForegroundWindow() == h;
  }
}
"@

$root = Join-Path $env:TEMP 'ksprobe'
New-Item -ItemType Directory -Force -Path $root | Out-Null
$appData = Join-Path $env:APPDATA 'KeySwitchDeepSeek'
$logPath = Join-Path $appData 'logs\keyswitch.log'
$exe = Join-Path $repo 'src\KeySwitch.App\bin\Release\net8.0-windows\win-x64\KeySwitchDeepSeek.exe'

$consoleHwnd = (Get-Process -Id $PID).MainWindowHandle
if ($consoleHwnd -ne 0) { [void][W]::ShowWindow($consoleHwnd, 6) }

# Chrome's launcher process exits at once, the window belongs to a child process:
# kill everything that carries our throwaway profile, or leftovers pile up.
function Stop-ProbeChrome {
    Get-CimInstance Win32_Process -Filter "Name='chrome.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -like '*ksprobe*' } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Milliseconds 300
}

function Stop-KeySwitch {
    Get-Process KeySwitchDeepSeek -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 300
}

function Send-Key([byte]$vk) {
    [W]::keybd_event($vk, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 12
    [W]::keybd_event($vk, 0, 2, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 28
}

function Send-CtrlKey([byte]$vk) {
    [W]::keybd_event(0x11, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 20
    [W]::keybd_event($vk, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 20
    [W]::keybd_event($vk, 0, 2, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 20
    [W]::keybd_event(0x11, 0, 2, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 80
}

# Whatever holds the foreground now blocks activation: step it aside, then take over.
function Set-TargetForeground([IntPtr]$hwnd) {
    for ($i = 0; $i -lt 15; $i++) {
        if ([W]::GetForegroundWindow() -eq $hwnd) { return $true }
        $fg = [W]::GetForegroundWindow()
        if ($fg -ne [IntPtr]::Zero -and $fg -ne $hwnd) { [void][W]::ShowWindow($fg, 6) }
        if ([W]::Activate($hwnd)) { return $true }
        Start-Sleep -Milliseconds 250
    }
    return ([W]::GetForegroundWindow() -eq $hwnd)
}

function Get-FieldText([IntPtr]$hwnd) {
    $el = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
    if ($null -eq $el) { return '<no window>' }

    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $candidates = New-Object System.Collections.ArrayList
    $stack = New-Object System.Collections.Stack
    $stack.Push($el)
    $guard = 0
    while ($stack.Count -gt 0 -and $guard -lt 6000) {
        $guard++
        $node = $stack.Pop()
        $child = $walker.GetFirstChild($node)
        while ($null -ne $child) {
            $ct = $child.Current.ControlType
            if ($ct -eq [System.Windows.Automation.ControlType]::Edit -or
                $ct -eq [System.Windows.Automation.ControlType]::Document) {
                [void]$candidates.Add($child)
            }
            $stack.Push($child)
            $child = $walker.GetNextSibling($child)
        }
    }

    foreach ($cand in $candidates) {
        try {
            $vp = $cand.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
            if ($vp.Current.Value) { return $vp.Current.Value }
        } catch { }
        try {
            $tp = $cand.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern)
            $text = $tp.DocumentRange.GetText(400)
            if ($text) { return $text }
        } catch { }
    }

    return '<no text pattern>'
}

# --- target -----------------------------------------------------------------
Stop-KeySwitch
Stop-ProbeChrome

if ($Target -eq 'notepad') {
    $np = @(Get-Process -Name Notepad -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 })
    if ($np.Count -eq 0) {
        Start-Process notepad.exe | Out-Null
        for ($i = 0; $i -lt 100; $i++) {
            Start-Sleep -Milliseconds 100
            $np = @(Get-Process -Name Notepad -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 })
            if ($np.Count -gt 0) { break }
        }
    }
    if ($np.Count -eq 0) { throw 'notepad window not found' }
    $hwnd = $np[0].MainWindowHandle
    [void](Set-TargetForeground $hwnd)
    Start-Sleep -Milliseconds 400
    Send-CtrlKey 0x4E     # Ctrl+N: a fresh empty tab, existing text is left alone
    Start-Sleep -Milliseconds 800
    $np = @(Get-Process -Name Notepad -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 })
    if ($np.Count -gt 0) { $hwnd = $np[0].MainWindowHandle }
}
else {
    $profile = Join-Path $root ('profile-' + [Guid]::NewGuid().ToString('N'))
    $page = Join-Path $root 'probe.html'
    if (-not (Test-Path $page)) {
        Set-Content -Path $page -Encoding ASCII -Value @'
<!doctype html><meta charset="utf-8"><title>probe</title>
<style>html,body{margin:0;height:100%}textarea{width:100%;height:100%;font:24px/1.4 Consolas;border:0;outline:0;padding:8px}</style>
<textarea id="f" autofocus oninput="document.title='F:'+this.value"></textarea>
<script>var f=document.getElementById('f');f.focus();f.addEventListener('input',function(){document.title='F:'+f.value;});</script>
'@
    }
    $chrome = @(
        "$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
        "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe",
        "$env:LOCALAPPDATA\Google\Chrome\Application\chrome.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $chrome) { throw 'chrome.exe not found' }

    $uri = 'file:///' + ($page -replace '\\', '/')
    $targetProcess = Start-Process -FilePath $chrome -PassThru -ArgumentList @(
        "--user-data-dir=$profile", "--no-first-run", "--no-default-browser-check",
        "--disable-features=Translate", "--window-size=1100,760", "--app=$uri"
    )
    $hwnd = [IntPtr]::Zero
    for ($i = 0; $i -lt 150; $i++) {
        Start-Sleep -Milliseconds 100
        $p = Get-Process -Id $targetProcess.Id -ErrorAction SilentlyContinue
        if ($p -and $p.MainWindowHandle -ne 0) { $hwnd = $p.MainWindowHandle; break }
    }
    if ($hwnd -eq [IntPtr]::Zero) { throw 'chrome window not found' }
    Start-Sleep -Milliseconds 1200
    [void](Set-TargetForeground $hwnd)
    Start-Sleep -Milliseconds 600
}

# The bug scenario: the window is in EN, the user types Russian words on it.
$layoutBefore = [W]::SetLayout($hwnd, '00000409')
Start-Sleep -Milliseconds 300
[void](Set-TargetForeground $hwnd)
[void][W]::SetCursorPos(600, 400)
[W]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
[W]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 400

$fgOk = ([W]::GetForegroundWindow() -eq $hwnd)
Write-Output ("FG_NOW=" + [W]::Describe([W]::GetForegroundWindow()))
Write-Output ("TARGET_WIN=" + [W]::Describe($hwnd))
Write-Output ("TARGET=$Target FG_OK=$fgOk LAYOUT_BEFORE=$layoutBefore")

# --- key switch -------------------------------------------------------------
if (Test-Path $logPath) { Remove-Item $logPath -Force -ErrorAction SilentlyContinue }
$env:KEYSWITCH_DEBUG = '1'
$env:KEYSWITCH_ALLOW_INJECTED = '1'
$ks = Start-Process -FilePath $exe -PassThru
Start-Sleep -Milliseconds 2500

[void](Set-TargetForeground $hwnd)
Start-Sleep -Milliseconds 400

if ($Scenario -eq 'force') {
    # hello is a valid English word: the detector keeps it, so only the double Shift can convert it.
    Write-Output '--- INPUT: hello + space, then double LEFT Shift ---'
    foreach ($vk in @(0x48, 0x45, 0x4C, 0x4C, 0x4F, 0x20)) { Send-Key $vk }
    Start-Sleep -Milliseconds 600
    for ($i = 0; $i -lt 2; $i++) {
        [W]::keybd_event(0xA0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 30
        [W]::keybd_event(0xA0, 0, 2, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 90
    }
    Start-Sleep -Milliseconds 1200
}
elseif ($Scenario -eq 'single') {
    # Russian words around one letter typed in EN layout.
    Write-Output '--- INPUT: privet malysh + b (EN) + space + karlson ---'
    [void][W]::SetLayout($hwnd, '00000419')
    Start-Sleep -Milliseconds 250
    foreach ($vk in @(0x47, 0x48, 0x42, 0x44, 0x54, 0x4E, 0x20)) { Send-Key $vk }
    Start-Sleep -Milliseconds 150
    foreach ($vk in @(0x56, 0x46, 0x4B, 0x53, 0x49, 0x20)) { Send-Key $vk }
    Start-Sleep -Milliseconds 300
    [void][W]::SetLayout($hwnd, '00000409')
    Start-Sleep -Milliseconds 250
    Send-Key 0x42
    Start-Sleep -Milliseconds 120
    Send-Key 0x20
    Start-Sleep -Milliseconds 900
    # Do not force the layout here: if the app switched it, the next word is typed in RU.
    foreach ($vk in @(0x52, 0x46, 0x48, 0x4B, 0x43, 0x4A, 0x59, 0x20)) { Send-Key $vk }
    Start-Sleep -Milliseconds 1200
}
else {
    Write-Output '--- INPUT: ghbdtn + space ---'
    foreach ($vk in @(0x47, 0x48, 0x42, 0x44, 0x54, 0x4E, 0x20)) { Send-Key $vk }
    Start-Sleep -Milliseconds 1200
}

$fieldText = Get-FieldText $hwnd
if ($Target -eq 'chrome') {
    # The page mirrors the textarea into document.title: the most reliable read.
    $titleNow = [W]::Describe($hwnd)
    Write-Output ("WINDOW_TITLE=" + $titleNow)
}
$layoutAfter = [W]::LayoutOf($hwnd)
Write-Output ("LAYOUT_AFTER=$layoutAfter")
Write-Output ("FIELD=[" + $fieldText.Trim() + "]")
Write-Output ("CODEPOINTS=" + (($fieldText.Trim().ToCharArray() | ForEach-Object { [int]$_ }) -join ","))
Write-Output '--- LOG ---'
if (Test-Path $logPath) { Get-Content $logPath -Encoding UTF8 } else { Write-Output '(no log file)' }

# --- cleanup ----------------------------------------------------------------
Stop-KeySwitch
Stop-ProbeChrome
