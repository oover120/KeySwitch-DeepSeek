param([string]$Target = "notepad", [string]$Key = "g")

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class L2 {
  [StructLayout(LayoutKind.Sequential)]
  public struct GUITHREADINFO {
    public int cbSize;
    public int flags;
    public IntPtr hwndActive;
    public IntPtr hwndFocus;
    public IntPtr hwndCapture;
    public IntPtr hwndMenuOwner;
    public IntPtr hwndMoveSize;
    public IntPtr hwndCaret;
    public int rcCaret_left, rcCaret_top, rcCaret_right, rcCaret_bottom;
  }
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern IntPtr GetKeyboardLayout(uint tid);
  [DllImport("user32.dll")] public static extern IntPtr LoadKeyboardLayout(string klid, uint flags);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
  [DllImport("user32.dll")] public static extern bool GetGUIThreadInfo(uint tid, ref GUITHREADINFO info);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();

  public static string H(IntPtr v) { return "0x" + v.ToInt64().ToString("X8"); }
  public static uint Tid(IntPtr h) { uint p; return GetWindowThreadProcessId(h, out p); }
  public static string HklOf(uint tid) { return H(GetKeyboardLayout(tid)); }
  public static string FocusInfo(IntPtr h) {
    uint tid = Tid(h);
    GUITHREADINFO g = new GUITHREADINFO();
    g.cbSize = Marshal.SizeOf(typeof(GUITHREADINFO));
    if (!GetGUIThreadInfo(tid, ref g)) return "GetGUIThreadInfo failed err=" + Marshal.GetLastWin32Error();
    uint ftid = g.hwndFocus == IntPtr.Zero ? 0 : Tid(g.hwndFocus);
    return "focus=" + H(g.hwndFocus) + " focusTid=" + ftid + " focusHkl=" + (ftid == 0 ? "n/a" : HklOf(ftid));
  }
  public static void ForceForeground(IntPtr h) {
    uint fg = Tid(GetForegroundWindow());
    uint tg = Tid(h);
    AttachThreadInput(fg, tg, true);
    SetForegroundWindow(h);
    AttachThreadInput(fg, tg, false);
  }
  public static string SetRu(IntPtr h) { IntPtr hkl = LoadKeyboardLayout("00000419", 1); PostMessage(h, 0x0050, IntPtr.Zero, hkl); return H(hkl); }
  public static void Tap(byte vk) { keybd_event(vk, 0, 0, UIntPtr.Zero); keybd_event(vk, 0, 2, UIntPtr.Zero); }
}
"@

function Read-Field([IntPtr]$hwnd) {
    $el = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
    $focused = [System.Windows.Automation.AutomationElement]::FocusedElement
    foreach ($cand in @($focused, $el)) {
        if ($null -eq $cand) { continue }
        try { $vp = $cand.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern); return $vp.Current.Value } catch { }
        try { $tp = $cand.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern); return $tp.DocumentRange.GetText(400) } catch { }
    }
    return "<none>"
}

Get-Process -Name Notepad,chrome,KeySwitch -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 600

if ($Target -eq "chrome") {
    $page = Join-Path $env:TEMP "ksprobe\probe.html"
    if (-not (Test-Path $page)) {
        New-Item -ItemType Directory -Force (Split-Path $page) | Out-Null
        $html = '<html><body><textarea id="t" autofocus style="position:fixed;inset:0;font-size:32px"></textarea></body></html>'
        [System.IO.File]::WriteAllText($page, $html, [System.Text.Encoding]::UTF8)
    }
    $chrome = "C:\Program Files\Google\Chrome\Application\chrome.exe"
    $profile = Join-Path $env:TEMP ("ksprobe\lc-" + [guid]::NewGuid().ToString("N"))
    $proc = Start-Process -FilePath $chrome -ArgumentList @("--app=file:///" + ($page -replace '\\','/'), "--user-data-dir=$profile", "--window-size=1100,760", "--no-first-run") -PassThru
    Start-Sleep -Seconds 3
    $hwnd = [IntPtr]::Zero
    foreach ($p in (Get-Process -Name chrome -ErrorAction SilentlyContinue)) {
        if ($p.MainWindowHandle -ne 0 -and $p.MainWindowTitle -notmatch "Chrome") { $hwnd = [IntPtr]$p.MainWindowHandle; break }
    }
    if ($hwnd -eq [IntPtr]::Zero) {
        foreach ($p in (Get-Process -Name chrome -ErrorAction SilentlyContinue)) { if ($p.MainWindowHandle -ne 0) { $hwnd = [IntPtr]$p.MainWindowHandle; break } }
    }
} else {
    $before = @(Get-Process -Name Notepad -ErrorAction SilentlyContinue | ForEach-Object { $_.Id })
    Start-Process notepad.exe | Out-Null
    Start-Sleep -Milliseconds 1800
    $hwnd = [IntPtr]::Zero
    foreach ($p in (Get-Process -Name Notepad -ErrorAction SilentlyContinue)) {
        if ($before -contains $p.Id) { continue }
        if ($p.MainWindowHandle -ne 0) { $hwnd = [IntPtr]$p.MainWindowHandle; break }
    }
}

if ($hwnd -eq [IntPtr]::Zero) { "ERROR: window not found"; exit 1 }

[L2]::ForceForeground($hwnd)
Start-Sleep -Milliseconds 500
"FG_IS_TARGET=$([L2]::GetForegroundWindow() -eq $hwnd)"
"WIN_TID=$([L2]::Tid($hwnd)) WIN_HKL=$([L2]::HklOf([L2]::Tid($hwnd)))"
"FOCUS_BEFORE=$([L2]::FocusInfo($hwnd))"
"SET_RU=$([L2]::SetRu($hwnd))"
Start-Sleep -Milliseconds 400
"WIN_HKL_AFTER_SET=$([L2]::HklOf([L2]::Tid($hwnd)))"
"FOCUS_AFTER_SET=$([L2]::FocusInfo($hwnd))"

$vk = [byte][char]$Key.ToUpperInvariant()
[L2]::Tap($vk)
Start-Sleep -Milliseconds 800
"WIN_HKL_AFTER_TAP=$([L2]::HklOf([L2]::Tid($hwnd)))"
"FOCUS_AFTER_TAP=$([L2]::FocusInfo($hwnd))"
$field = Read-Field $hwnd
"FIELD=[$field]"
"CODEPOINTS=" + (($field.ToCharArray() | ForEach-Object { [int]$_ }) -join ",")
