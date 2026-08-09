<#
.SYNOPSIS
    Capture the screen (or one window) to a PNG an agent can read back.

.DESCRIPTION
    There is no computer-use tool on this box, so the loop is built from
    primitives: this captures, the agent looks at the PNG, input.ps1 clicks.

    Exclusive-fullscreen D3D presents cannot be read by CopyFromScreen and come
    back black. If that happens the game must be switched to windowed or
    borderless - this script reports the black-frame case rather than silently
    handing back an empty image.

.EXAMPLE
    .\screenshot.ps1 -Out shot.png
    .\screenshot.ps1 -Window 'Oxygen Not Included' -Out oni.png -MaxWidth 1400
#>
[CmdletBinding()]
param(
    [string]$Out,
    [string]$Window,
    [int]$MaxWidth = 1400,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing, System.Windows.Forms

if (-not $Out) { $Out = Join-Path $PSScriptRoot 'shot.png' }
New-Item -ItemType Directory -Force -Path (Split-Path $Out -Parent) | Out-Null

Add-Type @'
using System;
using System.Runtime.InteropServices;
public class Win32Cap {
    [DllImport("user32.dll")] public static extern IntPtr FindWindow(string c, string n);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
}
'@ -ErrorAction SilentlyContinue

$bounds = $null
if ($Window) {
    $h = [Win32Cap]::FindWindow($null, $Window)
    if ($h -eq [IntPtr]::Zero) {
        # Fall back to a process main window whose title contains the string.
        $p = Get-Process | Where-Object { $_.MainWindowTitle -like "*$Window*" } | Select-Object -First 1
        if ($p) { $h = $p.MainWindowHandle }
    }
    if ($h -and $h -ne [IntPtr]::Zero) {
        if ([Win32Cap]::IsIconic($h)) { [Win32Cap]::ShowWindow($h, 9) | Out-Null; Start-Sleep -Milliseconds 400 }
        [Win32Cap]::SetForegroundWindow($h) | Out-Null
        Start-Sleep -Milliseconds 300
        $r = New-Object Win32Cap+RECT
        [void][Win32Cap]::GetWindowRect($h, [ref]$r)
        $bounds = New-Object Drawing.Rectangle $r.L, $r.T, ($r.R - $r.L), ($r.B - $r.T)
    } else {
        if (-not $Quiet) { Write-Host "[shot] window '$Window' not found - capturing whole screen" -ForegroundColor Yellow }
    }
}
if (-not $bounds) { $bounds = [Windows.Forms.Screen]::PrimaryScreen.Bounds }

$bmp = New-Object Drawing.Bitmap $bounds.Width, $bounds.Height
$g   = [Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($bounds.Location, [Drawing.Point]::Empty, $bounds.Size)
$g.Dispose()

# Sample a grid; an all-black frame means exclusive fullscreen blocked the read.
$nonBlack = 0
for ($x = 4; $x -lt $bmp.Width; $x += [math]::Max(1, [int]($bmp.Width / 40))) {
    for ($y = 4; $y -lt $bmp.Height; $y += [math]::Max(1, [int]($bmp.Height / 40))) {
        $c = $bmp.GetPixel($x, $y)
        if ($c.R + $c.G + $c.B -gt 24) { $nonBlack++ }
    }
}

$scale = 1.0
if ($MaxWidth -gt 0 -and $bmp.Width -gt $MaxWidth) { $scale = $MaxWidth / $bmp.Width }
if ($scale -lt 1.0) {
    $w = [int]($bmp.Width * $scale); $h2 = [int]($bmp.Height * $scale)
    $small = New-Object Drawing.Bitmap $w, $h2
    $g2 = [Drawing.Graphics]::FromImage($small)
    $g2.InterpolationMode = 'HighQualityBicubic'
    $g2.DrawImage($bmp, 0, 0, $w, $h2)
    $g2.Dispose()
    $small.Save($Out, [Drawing.Imaging.ImageFormat]::Png)
    $small.Dispose()
} else {
    $bmp.Save($Out, [Drawing.Imaging.ImageFormat]::Png)
}

$res = [ordered]@{
    out        = $Out
    captured   = "$($bounds.Width)x$($bounds.Height)"
    origin     = "$($bounds.X),$($bounds.Y)"
    savedScale = [math]::Round($scale, 4)
    allBlack   = ($nonBlack -eq 0)
}
$bmp.Dispose()
$res | ConvertTo-Json -Compress
if ($res.allBlack -and -not $Quiet) {
    Write-Host '[shot] frame is entirely black - the game is likely in exclusive fullscreen.' -ForegroundColor Yellow
    Write-Host '       Switch it to Windowed or Borderless for capture to work.' -ForegroundColor Yellow
}
