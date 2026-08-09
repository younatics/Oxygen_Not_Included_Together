<#
.SYNOPSIS
    Inject real mouse/keyboard input with SendInput, so a Unity game accepts it.

.DESCRIPTION
    SendKeys and PostMessage go through the window message queue, which Unity's
    input layer ignores. SendInput is injected at the same level the driver
    feeds, so ONI treats it as a real device.

    Coordinates are screen pixels. Screenshots are usually downscaled for
    reading, so -Scale lets a caller pass the coordinates it measured on the
    scaled image and have them converted back (real = measured / Scale).

.EXAMPLE
    .\input.ps1 -Action click -X 700 -Y 400 -Scale 0.5469
    .\input.ps1 -Action drag  -X 100 -Y 100 -X2 200 -Y2 140
    .\input.ps1 -Action key   -Key escape
    .\input.ps1 -Action type  -Text 'S1 test'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('move', 'click', 'rightclick', 'doubleclick', 'drag', 'key', 'type', 'scroll', 'where')]
    [string]$Action,
    [double]$X, [double]$Y, [double]$X2, [double]$Y2,
    [double]$Scale = 1.0,
    [string]$Key,
    [string]$Text,
    [int]$Amount = 0,
    [int]$SettleMs = 120,
    [string]$Window
)

$ErrorActionPreference = 'Stop'

Add-Type @'
using System;
using System.Runtime.InteropServices;
public class Win32In {
    [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT {
        public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT {
        public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)] public struct INPUT {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public MOUSEINPUT mi;
        [FieldOffset(8)] public KEYBDINPUT ki; }

    [DllImport("user32.dll", SetLastError=true)] public static extern uint SendInput(uint n, INPUT[] p, int cb);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern IntPtr FindWindow(string c, string n);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern ushort VkKeyScan(char ch);
    [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint code, uint type);
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }

    public const uint MOUSE = 0, KEYBD = 1;
    public const uint MOVE = 0x0001, LDOWN = 0x0002, LUP = 0x0004,
                      RDOWN = 0x0008, RUP = 0x0010, WHEEL = 0x0800,
                      ABSOLUTE = 0x8000;
    public const uint KEYUP = 0x0002, SCANCODE = 0x0008;

    static INPUT[] One(INPUT i) { return new INPUT[] { i }; }

    public static void MoveTo(int x, int y) {
        // SendInput absolute coords are normalised 0..65535 over the virtual desktop.
        int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77);
        int vw = GetSystemMetrics(78), vh = GetSystemMetrics(79);
        double nx = (x - vx) * 65535.0 / (vw - 1);
        double ny = (y - vy) * 65535.0 / (vh - 1);
        INPUT i = new INPUT(); i.type = MOUSE;
        i.mi.dx = (int)Math.Round(nx); i.mi.dy = (int)Math.Round(ny);
        i.mi.dwFlags = MOVE | ABSOLUTE;
        SendInput(1, One(i), Marshal.SizeOf(typeof(INPUT)));
    }
    public static void Button(uint flag) {
        INPUT i = new INPUT(); i.type = MOUSE; i.mi.dwFlags = flag;
        SendInput(1, One(i), Marshal.SizeOf(typeof(INPUT)));
    }
    public static void Wheel(int amount) {
        INPUT i = new INPUT(); i.type = MOUSE;
        i.mi.mouseData = unchecked((uint)(amount * 120)); i.mi.dwFlags = WHEEL;
        SendInput(1, One(i), Marshal.SizeOf(typeof(INPUT)));
    }
    // Scan codes, not virtual keys: Unity reads raw input and ignores VK-only events.
    public static void KeyScan(ushort vk, bool up) {
        ushort scan = (ushort)MapVirtualKey(vk, 0);
        INPUT i = new INPUT(); i.type = KEYBD;
        i.ki.wScan = scan; i.ki.dwFlags = SCANCODE | (up ? KEYUP : 0);
        SendInput(1, One(i), Marshal.SizeOf(typeof(INPUT)));
    }
    public static POINT Where() { POINT p; GetCursorPos(out p); return p; }
}
'@ -ErrorAction SilentlyContinue

$VK = @{
    escape=0x1B; enter=0x0D; return=0x0D; space=0x20; tab=0x09; backspace=0x08; delete=0x2E
    left=0x25; up=0x26; right=0x27; down=0x28; home=0x24; end=0x23
    f1=0x70; f2=0x71; f3=0x72; f4=0x73; f5=0x74; f9=0x78; f10=0x79; f11=0x7A; f12=0x7B
    shift=0x10; ctrl=0x11; alt=0x12
}

function To-Real($v) { if ($Scale -gt 0 -and $Scale -ne 1.0) { [int][math]::Round($v / $Scale) } else { [int][math]::Round($v) } }

if ($Window) {
    $h = [Win32In]::FindWindow($null, $Window)
    if ($h -eq [IntPtr]::Zero) {
        $p = Get-Process | Where-Object { $_.MainWindowTitle -like "*$Window*" } | Select-Object -First 1
        if ($p) { $h = $p.MainWindowHandle }
    }
    if ($h -and $h -ne [IntPtr]::Zero) { [Win32In]::SetForegroundWindow($h) | Out-Null; Start-Sleep -Milliseconds 250 }
}

switch ($Action) {
    'where' { $p = [Win32In]::Where(); @{ x = $p.X; y = $p.Y } | ConvertTo-Json -Compress; break }
    'move'  { [Win32In]::MoveTo((To-Real $X), (To-Real $Y)); break }
    'click' {
        [Win32In]::MoveTo((To-Real $X), (To-Real $Y)); Start-Sleep -Milliseconds 60
        [Win32In]::Button([Win32In]::LDOWN); Start-Sleep -Milliseconds 45
        [Win32In]::Button([Win32In]::LUP); break
    }
    'rightclick' {
        [Win32In]::MoveTo((To-Real $X), (To-Real $Y)); Start-Sleep -Milliseconds 60
        [Win32In]::Button([Win32In]::RDOWN); Start-Sleep -Milliseconds 45
        [Win32In]::Button([Win32In]::RUP); break
    }
    'doubleclick' {
        [Win32In]::MoveTo((To-Real $X), (To-Real $Y)); Start-Sleep -Milliseconds 60
        foreach ($n in 1..2) {
            [Win32In]::Button([Win32In]::LDOWN); Start-Sleep -Milliseconds 40
            [Win32In]::Button([Win32In]::LUP);   Start-Sleep -Milliseconds 60
        }
        break
    }
    'drag' {
        # Step the move: a single jump can land inside one frame and the game
        # never sees a drag, only a click at the end point.
        $sx = To-Real $X; $sy = To-Real $Y; $ex = To-Real $X2; $ey = To-Real $Y2
        [Win32In]::MoveTo($sx, $sy); Start-Sleep -Milliseconds 120
        [Win32In]::Button([Win32In]::LDOWN); Start-Sleep -Milliseconds 120
        $steps = 18
        foreach ($n in 1..$steps) {
            $t = $n / $steps
            [Win32In]::MoveTo([int]($sx + ($ex - $sx) * $t), [int]($sy + ($ey - $sy) * $t))
            Start-Sleep -Milliseconds 25
        }
        Start-Sleep -Milliseconds 120
        [Win32In]::Button([Win32In]::LUP); break
    }
    'scroll' { [Win32In]::MoveTo((To-Real $X), (To-Real $Y)); Start-Sleep -Milliseconds 60; [Win32In]::Wheel($Amount); break }
    'key' {
        if (-not $Key) { throw '-Key required' }
        $k = $Key.ToLower()
        $vk = if ($VK.ContainsKey($k)) { $VK[$k] } elseif ($k.Length -eq 1) { [Win32In]::VkKeyScan([char]$k) -band 0xFF } else { throw "unknown key '$Key'" }
        [Win32In]::KeyScan([uint16]$vk, $false); Start-Sleep -Milliseconds 45
        [Win32In]::KeyScan([uint16]$vk, $true); break
    }
    'type' {
        if (-not $Text) { throw '-Text required' }
        foreach ($ch in $Text.ToCharArray()) {
            $s = [Win32In]::VkKeyScan($ch)
            $vk = $s -band 0xFF
            $shift = (($s -shr 8) -band 1) -eq 1
            if ($shift) { [Win32In]::KeyScan(0x10, $false) }
            [Win32In]::KeyScan([uint16]$vk, $false); Start-Sleep -Milliseconds 25
            [Win32In]::KeyScan([uint16]$vk, $true)
            if ($shift) { [Win32In]::KeyScan(0x10, $true) }
            Start-Sleep -Milliseconds 35
        }
        break
    }
}
Start-Sleep -Milliseconds $SettleMs
