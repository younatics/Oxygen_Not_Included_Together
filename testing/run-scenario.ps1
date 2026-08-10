<#
.SYNOPSIS
    Run a whole two-box scenario with nobody at either keyboard.

.DESCRIPTION
    Load a save, host on LAN, join from the peer, dig, let it replicate, run
    the suite on both, and analyse - driven entirely through ScenarioRunner's
    command file and peer-agent.

    This exists because a class of bug cannot be caught any other way. Protocol
    changes alter what the receiver does with a packet, and no amount of
    serialization-size testing sees that: batching the plant and dig sweeps
    passed every unit test and would have deleted the client's state, because
    both receivers reconcile by absence. Anything that changes what arrives has
    to be verified here, not in a unit test.

    Digging matters specifically: NetworkIdentity.NetId is [Serialize]d, so a
    save restores its old ids and never calls the hash. Only freshly spawned
    objects exercise the NetId path.

.EXAMPLE
    .\run-scenario.ps1 -Label s1auto -Save 피난처 -DigCells 8
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Label,
    [Parameter(Mandatory = $true)][string]$Save,
    [int]$DigCells = 8,
    [string]$HostIp = '192.168.45.39',
    [int]$Port = 8080,
    [string]$Share = 'C:\ONI_MP_Share',
    [int]$PeerDigCells = 0,
    [int]$SettleSeconds = 45,
    [ValidateRange(0, 3)][int]$Speed = 3
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$peerCmd = Join-Path $root 'peer-cmd.ps1'
$log = Join-Path $env:USERPROFILE 'AppData\LocalLow\Klei\Oxygen Not Included\Player.log'
$cmdFile = Join-Path $env:TEMP 'oni_together_cmd'

function Step($m) { Write-Host ''; Write-Host "==> $m" -ForegroundColor Cyan }
function Ok($m)   { Write-Host "    OK   $m" -ForegroundColor Green }
function Die($m)  { Write-Host "    FAIL $m" -ForegroundColor Red; exit 1 }

function Send-Host([string]$command) { Set-Content $cmdFile $command -Encoding UTF8 }
function Send-Peer([string]$command) { & $peerCmd -Verb scenario -Label $command -Share $Share -TimeoutSeconds 60 | Out-Null }

# Waits for a pattern to appear in the host log after a given byte offset, so a
# line from an earlier step is never mistaken for this step's result.
function Wait-HostLog([string]$pattern, [int]$timeoutSeconds, [long]$after) {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 3
        $tmp = Join-Path $env:TEMP 'scenario-peek.log'
        try { Copy-Item $log $tmp -Force } catch { continue }
        $hit = Get-Content $tmp | Select-Object -Skip $after | Select-String -Pattern $pattern
        if ($hit) { return $hit[-1].Line }
    }
    return $null
}
function HostLogLines { try { (Get-Content $log | Measure-Object -Line).Lines } catch { 0 } }

function Wait-PeerStatus([string]$pattern, [int]$timeoutSeconds) {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        Send-Peer 'status'
        Start-Sleep -Seconds 4
        & $peerCmd -Verb push-log -Label "$Label-probe" -Share $Share -TimeoutSeconds 120 | Out-Null
        $hit = Select-String -Path (Join-Path $Share "drop\$Label-probe\client.log") `
                             -Pattern '\[SCENARIO\] OK status' -ErrorAction SilentlyContinue
        if ($hit -and $hit[-1].Line -match $pattern) { return $hit[-1].Line }
        Start-Sleep -Seconds 6
    }
    return $null
}

Step 'waiting for ScenarioRunner on this box'

# The process first, because the log cannot answer this. Player.log is not
# truncated between runs, so searching it from the beginning finds the previous
# run's "installed" line and reports a runner that is not there - which is what
# it did with the game closed, passing this step and then failing the next one
# with a misleading message. A check that cannot fail is not a check.
if (-not (Get-Process -Name OxygenNotIncluded -ErrorAction SilentlyContinue)) {
    Step 'ONI is not running, launching it'
    Start-Process 'steam://rungameid/457140'
    $deadline = (Get-Date).AddSeconds(180)
    while ((Get-Date) -lt $deadline -and -not (Get-Process -Name OxygenNotIncluded -ErrorAction SilentlyContinue)) {
        Start-Sleep -Seconds 5
    }
    if (-not (Get-Process -Name OxygenNotIncluded -ErrorAction SilentlyContinue)) { Die 'ONI did not start' }
    Ok 'process up'
}

# Ask, rather than look for a line that may be from last time. A status reply
# written after this mark can only have come from a runner that is alive now.
$mark = HostLogLines
Send-Host 'status'
if (-not (Wait-HostLog '\[SCENARIO\] (OK|FAIL) status' 240 $mark)) {
    Die 'ScenarioRunner did not answer - is ONI running with the dev mod?'
}
Ok 'runner up'

# Never start a run on top of a live session.
#
# The script only launches ONI when the process is missing, so running it twice
# without closing the game left the previous session hosting and joined - and
# then hosted and joined again on top. That run reported the suite twice, a 19 MB
# client log, two identities per NetId, "session has 0 players but transport
# reports 2", and a duplicant that had never received a position. Every one of
# those is an artefact of the doubled session, and any of them could have been
# taken for a real defect.
$mark = HostLogLines
Send-Host 'status'
$state = Wait-HostLog '\[SCENARIO\] (OK|FAIL) status' 60 $mark
if ($state -match 'insession=True') {
    Step 'a session is already live - tearing it down first'
    Send-Peer 'stop-net'
    Start-Sleep -Seconds 2
    $mark = HostLogLines
    Send-Host 'stop-net'
    if (Wait-HostLog '\[SCENARIO\] (OK|FAIL) stop-net' 60 $mark) { Ok 'previous session stopped' }
    else { Die 'could not stop the previous session - close ONI on both boxes and retry' }
    Start-Sleep -Seconds 5
}

Step "loading $Save"
$mark = HostLogLines
Send-Host "load $Save"
$loadLine = Wait-HostLog '\[SCENARIO\] (OK|FAIL) load' 120 $mark
if (-not $loadLine) { Die 'no response to load' }
# Matching OK|FAIL and then reporting success regardless is how the first run of
# this script reported "load accepted" for a save it had not found.
if ($loadLine -notmatch '\[SCENARIO\] OK load') { Die "load rejected: $loadLine" }
Ok 'load accepted'

Step 'waiting for the world'
$loaded = $false
foreach ($i in 1..30) {
    $mark = HostLogLines
    Send-Host 'status'
    $line = Wait-HostLog '\[SCENARIO\] OK status' 20 $mark
    if ($line -and $line -match 'game=True') { Ok $line.Substring($line.IndexOf('[SCENARIO]')); $loaded = $true; break }
}
if (-not $loaded) { Die 'world never finished loading' }

Step "hosting on ${HostIp}:${Port}"
$mark = HostLogLines
Send-Host "host-lan $HostIp $Port"
if (-not (Wait-HostLog '\[SCENARIO\] OK host-lan' 60 $mark)) { Die 'host-lan did not report success' }
Ok 'hosting'
Start-Sleep -Seconds 5

Step 'joining from the peer'
Send-Peer "join-lan $HostIp $Port"
$joined = Wait-PeerStatus 'insession=True.*game=True' 300
if (-not $joined) { Die 'peer never reached an in-session state - check the save transfer' }
Ok $joined.Substring($joined.IndexOf('[SCENARIO]'))

Step "digging $DigCells cells"
$mark = HostLogLines
Send-Host "dig $DigCells"
$dug = Wait-HostLog '\[SCENARIO\] OK dig' 60 $mark
if (-not $dug) { Die 'dig did not report' }
Ok $dug.Substring($dug.IndexOf('[SCENARIO]'))

# Both peers issue orders, because a one-sided scenario never exercises the
# intent path: the client asks the host to dig rather than digging by itself,
# and that request-and-confirm round trip is half the protocol.
if ($PeerDigCells -gt 0) {
    Step "digging $PeerDigCells cells from the peer"
    Send-Peer "dig $PeerDigCells"
    Start-Sleep -Seconds 6
    Ok 'peer dig requested'
}

# Without this the run proves only that markers spawn and replicate. Paused,
# no duplicant moves and nothing is ever mined, so the ore-spawn, chore and
# pathing paths - where the bugs actually are - go untouched.
Step "unpausing at speed $Speed"
$mark = HostLogLines
Send-Host "play $Speed"
$played = Wait-HostLog '\[SCENARIO\] OK play' 60 $mark
if (-not $played) { Die 'play did not report' }
Ok $played.Substring($played.IndexOf('[SCENARIO]'))

Step "letting duplicants work for ${SettleSeconds}s"
$mark = HostLogLines
Start-Sleep -Seconds $SettleSeconds

# The run used to be called a success once the dig order was placed. Placing an
# order proves nothing: every scenario so far reported OK while paused, with
# nothing mined. Ore spawning is what says a duplicant actually finished a dig.
# Asked of the peer, not of this box. WorldDamageSpawnResourcePacket is logged
# by whoever receives it, and the host is the one sending - so looking here
# reported "no mining observed" for a run that mined 26 ore, every time.
Step 'checking that mining actually happened'
& $peerCmd -Verb push-log -Label "$Label-mining" -Share $Share -TimeoutSeconds 120 | Out-Null
$peerLog = Join-Path $Share "drop\$Label-mining\client.log"
$mined = (Select-String -Path $peerLog -Pattern 'WorldDamageSpawnResourcePacket' -ErrorAction SilentlyContinue |
          Measure-Object).Count
if ($mined -le 1) {
    Write-Host "    WARN no ore reached the peer in the last ${SettleSeconds}s - the dig cells may be unreachable" -ForegroundColor Yellow
    Write-Host "         replication results below still stand; nothing about ore, chores or pathing does" -ForegroundColor Yellow
} else {
    Ok "ore spawned and replicated ($mined notices on the peer)"
}

# Stop the world before either box describes it.
#
# The suites do not run at the same instant - one run had them 4.2 s apart with
# the game at speed 3 - and the cross-peer comparison treats the two dumps as
# simultaneous. Four seconds of a running colony is a duplicant moving 0.8 kg
# between two storages and a building being repaired, and that is exactly what
# the comparison reported: two storage differences and two buildings damaged on
# the peer that dumped first and whole on the peer that dumped second. Note the
# direction - the *earlier* snapshot had the damage. Nothing had diverged; the
# measurement was taken twice at different times and subtracted.
Step 'pausing both boxes so the two snapshots describe the same moment'
$mark = HostLogLines
Send-Host 'pause'
if (Wait-HostLog '\[SCENARIO\] (OK|FAIL) pause' 60 $mark) { Ok 'host paused' }
else { Write-Host '    WARN host did not confirm pause - state comparison may drift' -ForegroundColor Yellow }
Send-Peer 'pause'
Start-Sleep -Seconds 3

Step 'analysing'
& (Join-Path $root 'analyze-session.ps1') -Label $Label -Share $Share
$analysis = $LASTEXITCODE

# Leave it as it was found, so a later step or a person can keep using the session.
Send-Host "play $Speed"
Send-Peer "play $Speed"

exit $analysis
