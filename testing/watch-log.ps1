<#
.SYNOPSIS
    Follow ONI's Player.log live and keep a rolling summary on disk.

.DESCRIPTION
    collect-logs.ps1 snapshots a finished run. This one watches a run in
    progress, so a second person (or an agent) can read the current state
    without opening a 15 MB log.

    Every -IntervalSeconds it rewrites two small files under testing/runs/live:
      status.txt  - counters for every audit signature, plus the session header
      recent.log  - the last -Keep interesting lines

    ONI keeps Player.log open, so the file is read through a FileStream with
    FileShare ReadWrite|Delete rather than Get-Content.

.EXAMPLE
    .\watch-log.ps1                       # follow until Ctrl-C
    .\watch-log.ps1 -Once                 # summarise what is already there
    .\watch-log.ps1 -OutDir runs\S1-live  # keep the summary with a scenario
#>
[CmdletBinding()]
param(
    [string]$LogPath,
    [string]$OutDir,
    [int]$IntervalSeconds = 3,
    [int]$Keep = 200,
    [switch]$Once,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'

if (-not $LogPath) {
    $LogPath = Join-Path $env:USERPROFILE 'AppData\LocalLow\Klei\Oxygen Not Included\Player.log'
}
if (-not (Test-Path $LogPath)) {
    Write-Host "FAIL Player.log not found at: $LogPath" -ForegroundColor Red
    Write-Host '     Launch ONI once, then re-run.' -ForegroundColor Red
    exit 1
}
if (-not $OutDir) { $OutDir = Join-Path $PSScriptRoot 'runs\live' }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$statusFile = Join-Path $OutDir 'status.txt'
$recentFile = Join-Path $OutDir 'recent.log'

# signature -> severity, kept in sync with diff_logs.py SIGNATURES
$SIGNATURES = [ordered]@{
    'Overwriting existing entity for NetId'    = 'HIGH  #8 registry aliasing'
    'Invalid PacketType received'              = 'HIGH  chunk corruption'
    'readyToProcess was false for'             = 'HIGH  packet drop window'
    'Requesting full resend'                   = 'HIGH  #6 resend spiral'
    'Invalid chunk index'                      = 'HIGH  #6 chunk-size misderivation'
    'No pending transfer for client'           = 'HIGH  TCP failed -> UDP fallback'
    'TCP file transfer server failed to start' = 'HIGH  port 8081 blocked'
    'NetId still 0 after RegisterIdentity'     = 'HIGH  spawn sync skipped'
    'Failed to handle packet'                  = 'HIGH  swallowed dispatch exception'
    'Failed to handle incoming packet'         = 'HIGH  swallowed dispatch exception'
    'Error in server update'                   = 'HIGH  swallowed host update exception'
    'Connection timed out'                     = 'HIGH  disconnect'
    'Received DUPLICATE chunk'                 = 'MED   #6 duplicate chunk'
    'Possible transfer stall'                  = 'MED   #6 stall detector'
    'has no NetworkIdentity; skipping sync'    = 'MED   spawn sync skipped'
    'No connection found for SteamID'          = 'MED   send to unknown peer'
    'Send to player'                           = 'MED   transport send failure'
    'Host attempted to send packet'            = 'LOW   self-send blocked'
    'Lookup failed'                            = 'INFO  #3/#8 downstream symptom'
    'Registered workable'                      = 'INFO  NetId issued (workable)'
    'Registered entity'                        = 'INFO  NetId issued (entity)'
    'Started transfer'                         = 'INFO  save transfer'
}

# lines worth echoing verbatim into recent.log
$INTERESTING = @(
    'Trying to start LAN lobby', 'Updated network transport', 'Connecting to',
    'Client connected', 'Client disconnected', 'HardSync', 'Hard sync',
    'Started transfer', 'Connection timed out', 'Failed to handle',
    'Error in server update', 'Invalid chunk index', 'Requesting full resend',
    'No pending transfer', 'TCP file transfer server', 'Overwriting existing entity'
)

$counts   = [ordered]@{}
foreach ($k in $SIGNATURES.Keys) { $counts[$k] = 0 }
$recent   = New-Object System.Collections.Generic.Queue[string]
$header   = New-Object System.Collections.Generic.List[string]
$position = 0L
$started  = Get-Date

function Read-NewLines {
    param([string]$Path, [ref]$Pos)
    $lines = New-Object System.Collections.Generic.List[string]
    $fs = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open,
                                 [System.IO.FileAccess]::Read,
                                 [System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete)
    try {
        # ONI truncates Player.log on relaunch; restart from 0 when that happens.
        if ($fs.Length -lt $Pos.Value) { $Pos.Value = 0 }
        $null = $fs.Seek($Pos.Value, [System.IO.SeekOrigin]::Begin)
        $sr = New-Object System.IO.StreamReader($fs, [System.Text.Encoding]::UTF8)
        while (-not $sr.EndOfStream) { $lines.Add($sr.ReadLine()) }
        $Pos.Value = $fs.Position
    } finally { $fs.Dispose() }
    return $lines
}

function Write-Status {
    $sb = New-Object System.Text.StringBuilder
    $null = $sb.AppendLine("ONI Together - live log summary")
    $null = $sb.AppendLine("log        : $LogPath")
    $null = $sb.AppendLine("updated    : $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
    $null = $sb.AppendLine("watching   : $([int]((Get-Date) - $started).TotalSeconds)s")
    $null = $sb.AppendLine("bytes read : $position")
    $null = $sb.AppendLine('')
    if ($header.Count -gt 0) {
        $null = $sb.AppendLine('session')
        foreach ($h in $header) { $null = $sb.AppendLine("  $h") }
        $null = $sb.AppendLine('')
    }
    $null = $sb.AppendLine('signature counts')
    $any = $false
    foreach ($k in $counts.Keys) {
        if ($counts[$k] -gt 0) {
            $any = $true
            $null = $sb.AppendLine(("  {0,8}  {1,-42} {2}" -f $counts[$k], $k, $SIGNATURES[$k]))
        }
    }
    if (-not $any) { $null = $sb.AppendLine('  (none yet)') }
    Set-Content -Path $statusFile -Value $sb.ToString() -Encoding utf8
    Set-Content -Path $recentFile -Value ($recent.ToArray() -join "`n") -Encoding utf8
}

function Process-Lines {
    param($lines, [switch]$Echo)
    $hits = 0
    foreach ($line in $lines) {
        if ($null -eq $line) { continue }
        foreach ($k in $SIGNATURES.Keys) {
            if ($line.Contains($k)) { $counts[$k]++ }
        }
        if ($line -match 'Trying to start LAN lobby|Updated network transport to|staticID|Loading MOD dll') {
            $t = $line.Trim()
            if (-not $header.Contains($t)) { $header.Add($t) }
        }
        foreach ($i in $INTERESTING) {
            if ($line.Contains($i)) {
                $recent.Enqueue($line.Trim())
                while ($recent.Count -gt $Keep) { $null = $recent.Dequeue() }
                $hits++
                if ($Echo -and -not $Quiet) { Write-Host $line.Trim() -ForegroundColor Yellow }
                break
            }
        }
    }
    return $hits
}

if (-not $Quiet) {
    Write-Host "[watch] $LogPath" -ForegroundColor Cyan
    Write-Host "[watch] summary -> $statusFile" -ForegroundColor Cyan
}

# Initial catch-up: count everything already in the file, but do not echo it -
# a finished session is 15 MB and would bury the live lines that follow.
$posRef = [ref]$position
$null = Process-Lines (Read-NewLines -Path $LogPath -Pos $posRef)
$position = $posRef.Value
Write-Status

if ($Once) {
    if (-not $Quiet) { Get-Content $statusFile | Write-Host }
    exit 0
}

if (-not $Quiet) { Write-Host '[watch] following - Ctrl-C to stop' -ForegroundColor Cyan }
while ($true) {
    Start-Sleep -Seconds $IntervalSeconds
    $posRef = [ref]$position
    $null = Process-Lines (Read-NewLines -Path $LogPath -Pos $posRef) -Echo
    $position = $posRef.Value
    Write-Status
}
