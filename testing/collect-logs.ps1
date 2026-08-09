<#
.SYNOPSIS
    Snapshot this machine's ONI log into testing/runs/<Label>/<Role>.log

.DESCRIPTION
    ONI writes Unity's Player.log. The mod's DebugConsole.Log goes to Debug.Log,
    so everything with an "[ONI_Together]" prefix lands there.

    Run this on BOTH machines with the SAME -Label right after a scenario, then
    put the two files side by side and run diff_logs.py.

.EXAMPLE
    .\collect-logs.ps1 -Role host   -Label S1-baseline
    .\collect-logs.ps1 -Role client -Label S1-baseline -IncludePrevious
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateSet('host', 'client')][string]$Role,
    [Parameter(Mandatory = $true)][string]$Label,
    [switch]$IncludePrevious,
    [string]$OutRoot
)

$ErrorActionPreference = 'Stop'

$logDir = Join-Path $env:USERPROFILE 'AppData\LocalLow\Klei\Oxygen Not Included'
$player = Join-Path $logDir 'Player.log'
$prev   = Join-Path $logDir 'Player-prev.log'

if (-not (Test-Path $player)) {
    Write-Host "FAIL Player.log not found at: $player" -ForegroundColor Red
    Write-Host '     Has ONI been launched on this machine yet?' -ForegroundColor Red
    exit 1
}

if (-not $OutRoot) { $OutRoot = Join-Path $PSScriptRoot 'runs' }
$dest = Join-Path $OutRoot $Label
New-Item -ItemType Directory -Force -Path $dest | Out-Null

# Copy rather than move - ONI may still hold the handle; Copy-Item tolerates the share.
$target = Join-Path $dest "$Role.log"
Copy-Item -Path $player -Destination $target -Force
$size = [math]::Round((Get-Item $target).Length / 1MB, 2)

$modLines = (Select-String -Path $target -Pattern '\[ONI_Together\]' -AllMatches |
             Measure-Object).Count

Write-Host "OK  $target  (${size} MB, $modLines mod lines)" -ForegroundColor Green
if ($modLines -eq 0) {
    Write-Host 'WARN zero [ONI_Together] lines - the mod did not load. Check the Mods menu.' -ForegroundColor Yellow
}

# Always, not on request. ONI rotates Player.log to Player-prev.log at startup,
# so a peer that crashed and was restarted keeps the entire crash in the
# previous file. Making that opt-in meant it was never opted into: the last
# crash investigation opened a client log of 0.03 MB and found nothing, because
# the session it wanted had already been rotated away. It costs a file copy.
if (Test-Path $prev) {
    Copy-Item -Path $prev -Destination (Join-Path $dest "$Role-prev.log") -Force
    $prevSize = [math]::Round((Get-Item $prev).Length / 1MB, 2)
    Write-Host "OK  also copied Player-prev.log (${prevSize} MB - the session before this one)" -ForegroundColor Green
}

# Environment fingerprint - makes a run reproducible after the fact.
$meta = [ordered]@{
    role          = $Role
    label         = $Label
    collectedUtc  = (Get-Date).ToUniversalTime().ToString('o')
    machine       = $env:COMPUTERNAME
    lanIPv4       = @((Get-NetIPAddress -AddressFamily IPv4 |
                        Where-Object { $_.IPAddress -notlike '127.*' -and
                                       $_.IPAddress -notlike '169.254.*' }).IPAddress)
    playerLogMB   = $size
    modLogLines   = $modLines
}
$modDir = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Klei\OxygenNotIncluded\mods\dev\ONI_Together_dev'
if (Test-Path (Join-Path $modDir 'ONI_Together.dll')) {
    $dll = Get-Item (Join-Path $modDir 'ONI_Together.dll')
    $meta.modDllUtc   = $dll.LastWriteTimeUtc.ToString('o')
    $meta.modDllBytes = $dll.Length
    $meta.modDllSha256 = (Get-FileHash $dll.FullName -Algorithm SHA256).Hash
}
$meta | ConvertTo-Json -Depth 4 | Set-Content -Path (Join-Path $dest "$Role.meta.json") -Encoding UTF8
Write-Host "OK  $Role.meta.json  (mod dll hash recorded - both boxes must match)" -ForegroundColor Green
