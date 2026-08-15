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
    # Construction orders, per side. Zero means that side does not build, which
    # is how a scenario asks "does the client's own order work" separately from
    # "does the host's".
    [int]$BuildCells = 0,
    [int]$PeerBuildCells = 0,
    # Deconstruction, after the ladders have had time to be built. Only touches
    # what this scenario put there.
    [int]$DeconstructCells = 0,
    # Eggs to bring to term before unpausing, so the client-side hatch block is
    # actually exercised instead of reporting zero for want of an event.
    [int]$HatchEggs = 0,
    # Products to ask the peer's fabricators for, so the client-side product guard
    # is exercised instead of reporting zero for want of a duplicant to work them.
    [int]$FabricateOrders = 0,
    # Construction sites to finish outright, because duplicants do not finish one
    # inside a run and the completion path was therefore never reached.
    [int]$FinishBuilds = 0,
    # Repairable buildings to damage on the host before the settle, so the repair
    # path exists at all. Wire repair was reported broken from play and the lab
    # never reproduced it: nothing here ever damaged anything.
    [int]$DamageBuildings = 0,
    # Drop the client and rejoin before comparing. "Reconnect never succeeds" is
    # listed as a known trap with a named cause and a test for that cause, but no
    # live session was ever put through it.
    [switch]$Reconnect,
    # Comma-separated game types to dump the members of, so an accessor can be copied
    # instead of guessed. Empty for normal runs.
    [string]$AskApi = '',
    # What the build orders place. Tiles keep their scaffold on a different object
    # layer from ladders, which is the case the leftover sweep exists for.
    [string]$BuildWhat = 'Ladder',
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

# Building, from whichever side is asked to.
#
# Digging was the only action a run ever took, and that left the half of the
# protocol that creates objects unmeasured. Two fixes landed on paths a run has
# never once exercised - the fabricator product block and the egg hatch block
# both reported zero across three runs, which reads like "no duplicates" and
# means "never tried". Counting that as verified is how a fix ships broken.
#
# Ordered before the unpause, so the sites exist when duplicants start moving
# and the run measures construction rather than an order sitting in a queue.
if ($BuildCells -gt 0) {
    Step "ordering $BuildCells ladders on the host"
    $mark = HostLogLines
    Send-Host "build $BuildCells $BuildWhat"
    $built = Wait-HostLog '\[SCENARIO\] OK build' 60 $mark

    # A warning, not a Die.
    #
    # The build verb threw on its first cell and Die killed the run before the
    # collect step, so three runs produced no logs at all - and the analysis then
    # read the previous run's files and reported its numbers as though they were
    # new. A broken action should cost that action, not the whole run's evidence.
    if ($built) {
        Ok $built.Substring($built.IndexOf('[SCENARIO]'))
    } else {
        Write-Host '    WARN host build did not report - continuing so the run still collects logs' -ForegroundColor Yellow
    }
}

if ($PeerBuildCells -gt 0) {
    Step "ordering $PeerBuildCells ladders from the peer"
    Send-Peer "build $PeerBuildCells $BuildWhat"
    Start-Sleep -Seconds 6
    Ok 'peer build requested'
}

# Without this the run proves only that markers spawn and replicate. Paused,
# no duplicant moves and nothing is ever mined, so the ore-spawn, chore and
# pathing paths - where the bugs actually are - go untouched.
# Eggs, brought to term so hatching happens inside a run.
#
# The client-side hatch block shipped and reported zero every time. The
# patch-attachment test has since proved the hook is on the method, so the zero
# means incubation never finishes in 150 seconds - eggs take cycles. Waiting for
# one is not a test.
if ($HatchEggs -gt 0) {
    Step "bringing $HatchEggs eggs to term on the host"
    $mark = HostLogLines
    Send-Host "hatch $HatchEggs"
    $hatched = Wait-HostLog '\[SCENARIO\] OK hatch' 60 $mark
    if ($hatched) { Ok $hatched.Substring($hatched.IndexOf('[SCENARIO]')) }
    else { Write-Host '    WARN hatch did not report' -ForegroundColor Yellow }

    # And on the client, because the block being tested lives there.
    #
    # Forcing only the host's eggs left the client's counter at zero, which is
    # correct behaviour and proves nothing: the client's own eggs never reached
    # term, so its SpawnBaby never ran and there was nothing to refuse. A
    # suppression can only be exercised on the peer that would otherwise act.
    Send-Peer "hatch $HatchEggs"
    Start-Sleep -Seconds 4
    Ok 'peer eggs brought to term'
}

# The fabricator guard, exercised on the peer that has it.
#
# A fabricator needs a duplicant to work it and the client's AI is off, so the
# client will never finish an order by itself - the guard could sit there
# forever reporting zero. Asking directly is the only way to find out whether it
# refuses.
if ($FabricateOrders -gt 0) {
    Step "asking the peer's fabricators for $FabricateOrders product(s)"
    Send-Peer "fabricate $FabricateOrders"
    Start-Sleep -Seconds 4
    Ok 'peer fabricate requested'
}

# Damage, before the settle, so duplicants have the run to repair it.
#
# Wire repair not appearing on the client was reported from play and never
# reproduced here, because nothing in this scenario damages anything - so the
# damage test passed every run on an empty set. That is the same vacuous pass that
# hid the egg block and the fabricator block.
#
# Only the host damages. The client's damage is suppressed on purpose, so asking it
# would measure the suppression, not the repair.
if ($DamageBuildings -gt 0) {
    Step "damaging $DamageBuildings repairable building(s) on the host"
    $mark = HostLogLines
    Send-Host "damage $DamageBuildings"
    $hit = Wait-HostLog '\[SCENARIO\] OK damage' 60 $mark
    if ($hit) { Ok $hit.Substring($hit.IndexOf('[SCENARIO]')) }
    else { Write-Host '    WARN damage did not report' -ForegroundColor Yellow }
}

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
# Deconstruction goes after the settle, so it tears down ladders that were
# actually built rather than sites still waiting for a duplicant. Marking a site
# for deconstruction is a different path from marking a finished building, and
# the finished one is the case the removal fix was about.
if ($DeconstructCells -gt 0) {
    Step "marking $DeconstructCells ladders for deconstruction on the host"
    $mark = HostLogLines
    Send-Host "deconstruct $DeconstructCells"
    $torn = Wait-HostLog '\[SCENARIO\] OK deconstruct' 60 $mark
    if ($torn) { Ok $torn.Substring($torn.IndexOf('[SCENARIO]')) } else { Write-Host '    WARN deconstruct did not report' -ForegroundColor Yellow }

    # Long enough for a duplicant to walk over and finish the job, so the run
    # measures a building actually disappearing on both peers.
    Start-Sleep -Seconds 45
}

# Finish what was ordered, so completion is actually replicated.
#
# A run produced zero BuildCompletePacket applications: duplicants do not finish a
# tile inside 150 seconds, so the completion path - and the leftover-scaffold sweep
# that hangs off it - was never reached. The counter read zero for want of the
# event, the same way the egg block did.
if ($FinishBuilds -gt 0) {
    Step "finishing $FinishBuilds construction sites on the host"
    $mark = HostLogLines
    Send-Host "finishbuild $FinishBuilds"
    $finished = Wait-HostLog '\[SCENARIO\] OK finishbuild' 60 $mark
    if ($finished) { Ok $finished.Substring($finished.IndexOf('[SCENARIO]')) }
    else { Write-Host '    WARN finishbuild did not report' -ForegroundColor Yellow }
    Start-Sleep -Seconds 5
}

# Drop the client and bring it back, before anything is compared.
#
# This is the highest-value untested path in the project and it needed no new game
# code at all - stop-net and join-lan were already there. The notes list "reconnect
# never succeeds" as a known trap with a named cause, and a test was written for the
# cause; nothing ever put a live session through it. So a fix that landed cannot be
# distinguished from a fix that did not.
#
# Placed before the comparisons on purpose: every check that follows - hit points,
# container contents, the id tables - then describes a session that has reconnected.
# A rejoin that appears to work and leaves the two peers disagreeing is the failure
# worth catching, and it is invisible if the run ends at "connected".
if ($Reconnect) {
    Step 'dropping the client and reconnecting'
    Send-Peer 'stop-net'
    Start-Sleep -Seconds 10

    Send-Peer "join-lan $HostIp $Port"
    $rejoined = Wait-PeerStatus 'insession=True.*game=True' 300
    if (-not $rejoined) {
        Write-Host '    FAIL the client never rejoined - reconnect is broken' -ForegroundColor Red
        Write-Host '         the comparisons below describe a solo host and mean nothing' -ForegroundColor Red
    } else {
        Ok $rejoined.Substring($rejoined.IndexOf('[SCENARIO]'))
        # Time to receive the world again before anything is compared, or the
        # comparison measures the handshake rather than the session.
        Start-Sleep -Seconds 30
    }
}

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
#
# The peer stops first, and both commands go out before either is waited on.
#
# Pausing the host first and then waiting for its confirmation left the client
# running, uncorrected, for the whole of that window - because a paused host runs no
# Sim1000ms and therefore sends no vitals, while the client's own simulation carries
# on burning stamina and calories with nothing arriving to correct it.
#
# It was measured rather than reasoned about, and the numbers are unambiguous: every
# one of the 84 duplicant vital rows reported exactly 25.3 seconds since its last
# correction - the same figure for all of them, which is a shared cause, not drift -
# while simulation time between the two snapshots differed by 0.33 seconds. One
# duplicant's stamina read 99.6 on the host and 31.8 on the client, and 25.3 seconds
# of an awake duplicant's stamina is 67, which is the gap.
#
# So the comparison was measuring the length of its own pause sequence. Stopping the
# client first inverts it: the client freezes while the host is still sending, so the
# last corrections it applies are the host's own final values.
# How much each peer's own containers move while the colony runs, before anything is
# frozen.
#
# Three containers of 953 disagree at the end of every long run and the staleness dump
# ruled out the easy answer - all 1,396 syncers were last corrected at the same instant,
# so the three are not stale. What differs is where the mass sits rather than how much:
# one building holds 1.0 and 2.0 kg of a prefab across two containers on the host and 0
# and 3.0 on the client, the same three kilograms distributed differently.
#
# That is a question about each peer's own building logic, and it cannot be asked of a
# paused colony - sample one twice and it says the same thing twice. Asked here, while
# both are still running, it needs no cross-peer comparison at all: if one peer's
# containers move between two samples and the other's do not, that names the side.
Step 'sampling container churn on both boxes while they are still running'
Send-Host 'churn'
Send-Peer 'churn'
Start-Sleep -Seconds 4
Send-Host 'churn-diff'
Send-Peer 'churn-diff'
Start-Sleep -Seconds 2

$mark = HostLogLines
Send-Peer 'pause'
Send-Host 'pause'
if (Wait-HostLog '\[SCENARIO\] (OK|FAIL) pause' 60 $mark) { Ok 'host paused' }
else { Write-Host '    WARN host did not confirm pause - state comparison may drift' -ForegroundColor Yellow }
Start-Sleep -Seconds 3

# Both peers describe their damaged buildings, while both are paused.
#
# Asked after the pause on purpose. An earlier version of the state comparison read
# two snapshots taken 4.2 seconds apart at speed 3 and reported two buildings as
# diverged; nothing had diverged, the colony had simply been repaired in between.
# Health changes fast enough for that to matter, so this is the one place it can be
# asked honestly.
if ($DamageBuildings -gt 0) {
    Step 'asking both peers about damaged buildings'
    Send-Host 'hp'
    Send-Peer 'hp'
    Start-Sleep -Seconds 3
}

# What is inside every container, from both peers, while both are paused.
#
# Asked unconditionally: this is the last big disagreement and it has no scenario
# step to switch on. The dump is bounded by containers, not cells.
Step 'asking both peers what is in their containers'
Send-Host 'storage'
Send-Peer 'storage'
Start-Sleep -Seconds 4

# Everything else worth comparing, in one uniform dump.
#
# Research, recipe queues, the flags a player clicks and duplicant vitals had no
# comparison at all, and two of the three worst bugs found by playing were in
# exactly those categories. One dump plus one comparer replaces the pattern of
# writing a new pair of files per category, which is why most categories never got
# one.
Step 'asking both peers for the rest of the game state'
Send-Host 'state'
Send-Peer 'state'
Start-Sleep -Seconds 5

# What state each building type carries, and how much of it anything is watching.
#
# A single-peer audit, so only the host is asked. This is the generalisation of how
# the storage gap was found: seven Harmony patches each naming one game type, no list
# anywhere of what should be on the list, and therefore no way to notice a type that
# was left off. Storage cost 96 divergent containers before a cross-peer comparison
# happened to catch it.
Step 'auditing what state is tracked at all'
Send-Host 'coverage'
Start-Sleep -Seconds 4

# The accessors for player-set building state, asked of the running game.
#
# Door control state, the building enable toggle and the manual delivery amount are
# only touched by event patches, and none of those reads the current value - so there
# was no call site to copy, and the rule here is not to guess ONI API names. Offline
# reflection cannot answer it either: PowerShell 5.1 refuses to load Assembly-CSharp.
if ($AskApi) {
    Step "asking the game about $AskApi"
    foreach ($type in ($AskApi -split ',')) {
        Send-Host "api $($type.Trim())"
        Start-Sleep -Seconds 3
    }
}

Step 'analysing'
& (Join-Path $root 'analyze-session.ps1') -Label $Label -Share $Share
$analysis = $LASTEXITCODE

# Leave it as it was found, so a later step or a person can keep using the session.
Send-Host "play $Speed"
Send-Peer "play $Speed"

exit $analysis
