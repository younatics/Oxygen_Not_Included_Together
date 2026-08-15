<#
.SYNOPSIS
    Run the two-box scenario over and over, unattended, and keep a one-line
    verdict per run.

.DESCRIPTION
    A single run answers "does this build diverge". A night of runs answers
    questions a single run cannot: which failures are intermittent, which
    counters grow with session length, whether a fix held on the tenth attempt
    as well as the first. Several defects in this work appeared in one run out
    of three - the ColdWheat naming loop, the duplicant that never receives its
    vitals - and were dismissed as noise the first time.

    Deliberately varied between runs. Digging the same four cells at the same
    speed proves that one path works; changing the dig size and the settle time
    exercises different amounts of churn, which is what the id paths are
    sensitive to.

    Each run is independent: ONI is restarted on both boxes, so a leak cannot
    carry over and be blamed on the next build. That costs about a minute per
    run and buys the right to compare runs at all.

    Writes soak-summary.txt as it goes, one line per run, so progress is
    readable while it is still going and survives whatever kills it.

.EXAMPLE
    .\soak.ps1 -Runs 12
#>
[CmdletBinding()]
param(
    [int]$Runs = 10,
    # Required rather than defaulted, and deliberately so: the save name is
    # Korean, and a non-ASCII literal in this file is read as ANSI by
    # PowerShell 5.1 unless the file carries a byte order mark - which broke the
    # parse of the parameter block itself, several lines below the actual cause.
    # Keeping the script ASCII removes the whole question.
    [Parameter(Mandatory = $true)][string]$Save,
    # The colony to re-copy from before each run. Read-only; never written.
    [string]$Pristine,
    [string]$Share = 'C:\ONI_MP_Share',
    # Game types to dump the members of, comma separated. Empty for normal runs.
    [string]$AskApi = '',
    [string]$Summary = 'C:\GITHUB\Oxygen_Not_Included_Together\testing\soak-summary.txt',
    # One settle length for every run, instead of the 150/120/180 rotation.
    #
    # Every measurement this project has taken describes about six minutes of colony.
    # That is long enough to catch anything that breaks on an event - a dig, a build, a
    # reconnect - and structurally blind to anything that accumulates: a table that
    # grows, an id space that fills, a divergence that only shows after an hour. Both of
    # the worst reports from real play were about long sessions.
    #
    # A parameter rather than an edit, so a long run is something anyone can ask for and
    # the summary records what was asked.
    [int]$SettleSeconds = 0,
    # Make every run a reconnect run instead of one in three.
    #
    # The reconnect path is where the last defects have been hiding - a client hatching
    # its own eggs through the gap, a tracker guard reading a flag that is false while
    # the session is down - and one run in three is too slow a rate to tell a fix from
    # variance. Running the scenario directly in a loop is not a substitute: soak
    # restarts ONI between runs, and without that the logs accumulate and every count
    # taken from them is the sum of every run so far.
    [switch]$AlwaysReconnect
)

$ErrorActionPreference = 'Continue'
$root = $PSScriptRoot
$peerCmd = Join-Path $root 'peer-cmd.ps1'

# The same resolution analyze-session uses, and for the same reason: the python on
# PATH here is the Windows Store stub, a zero-length shim that prints "Python was not
# found" and returns a failure code. Testing with it produced the conclusion that
# python was missing and the gates were fake - they were not, analyze-session had been
# skipping the stub all along and running the real interpreter.
$Python = $null
$pyCmd = Get-Command python -ErrorAction SilentlyContinue
if ($pyCmd -and (Get-Item $pyCmd.Source).Length -gt 0) { $Python = $pyCmd.Source }
if (-not $Python) {
    $cand = Get-ChildItem "$env:LOCALAPPDATA\Programs\Python" -Filter python.exe -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notlike '*\venv\*' } | Select-Object -First 1
    if ($cand) { $Python = $cand.FullName }
}

# Varied on purpose: see the note above about one run proving one path.
# Held equal on purpose for the moment.
#
# Every run that ended with the client out of the session was a run 2, and run 2
# differed from run 1 in three ways at once: more cells dug, a longer settle, and
# the client rather than the host placing the build orders. Reading that as "the
# client's build order drops the session" is picking one of three candidates
# because it is the interesting one.
#
# So dig and settle are identical here and only the acting peer changes. If the
# client still ends up solo and the host-build run does not, the build order is
# the cause; if both do, it is the twenty extra seconds.
# Back to a varied matrix now that the question the equal-parameter runs were
# asked for is answered: the client leaving the session was the suite kicking it
# out, not the client's build orders.
$digSizes = @(4, 6, 3)
$settles  = @(150, 120, 180)

# Which side acts, per run.
#
# Every run until now dug from the host and nothing else, so two landed fixes -
# the fabricator product block and the egg hatch block - reported zero across
# three runs each. Zero reads as "no duplicates" and meant "never tried".
#
# Rotated rather than combined, because "both peers built and the counts differ"
# does not say whose order was wrong. Run 1 is the host alone, run 2 the client
# alone, run 3 both at once with a teardown after - so a disagreement names the
# side that caused it.
$hostBuilds  = @(4, 4, 3)
$peerBuilds  = @(0, 0, 3)
$deconstructs = @(0, 0, 3)

# What each run builds. Tiles keep their scaffold on a different object layer from
# ladders, and the leftover-scaffold sweep exists for exactly that mismatch - a
# matrix that only ever built ladders could never reach the case it guards.
# Wire and conduit on purpose, not only ladders and tiles.
#
# A ladder's and a tile's construction site sits on the building's own object layer.
# A wire's and a conduit's does not, and that is the case behind the reported "host
# says the tile is built, the client still shows it scheduled" - measured across peers
# as HighWattageWire and InsulatedLiquidConduit finished on the host and still
# UnderConstruction on the client. The handler for it has been in for four commits and
# siteOtherLayer has read zero every run since, because nothing here could reach it.
#
# Both names are ones this colony's own logs have printed, rather than names that look
# right - Assets.GetBuildingDef takes the PrefabID and a wrong one costs a whole run.
# Tile stays in the rotation so the case that already passes keeps being checked.
$buildWhat = @('Wire', 'Tile', 'InsulatedLiquidConduit')

# Which run drops the client and rejoins.
#
# Not every run, because a reconnect adds forty seconds and rebuilds the client's
# world - and if it breaks the session, every check after it reports a solo host and
# says nothing about replication. One run in three exercises it while the other two
# keep measuring an unbroken session, so a regression can be told apart from a
# reconnect artifact.
$reconnects = @($false, $false, $true)

function Note($text) {
    $line = "[{0}] {1}" -f (Get-Date -Format 'MM-dd HH:mm:ss'), $text
    Write-Host $line
    Add-Content -Path $Summary -Value $line -Encoding UTF8
}

Note "soak starting: $Runs runs on '$Save'"

for ($i = 1; $i -le $Runs; $i++) {
    $label = 'soak{0:D2}' -f $i
    $dig    = $digSizes[($i - 1) % $digSizes.Count]
    $settle = if ($SettleSeconds -gt 0) { $SettleSeconds } else { $settles[($i - 1) % $settles.Count] }
    $hostBuild = $hostBuilds[($i - 1) % $hostBuilds.Count]
    $peerBuild = $peerBuilds[($i - 1) % $peerBuilds.Count]
    $tearDown  = $deconstructs[($i - 1) % $deconstructs.Count]

    $reconnect = if ($AlwaysReconnect) { $true } else { $reconnects[($i - 1) % $reconnects.Count] }
    Note "run $i/$Runs  label=$label dig=$dig settle=${settle}s build=$hostBuild peerBuild=$peerBuild deconstruct=$tearDown reconnect=$reconnect"

    # A fresh copy of the colony for every run.
    #
    # Runs were consuming the thing they measure. Each one plays 90-180 seconds at
    # speed 3 and ONI autosaves the result over the clone, so the colony drifts a
    # little further every time - and after enough runs the duplicants are dead. It
    # happened once already and went unnoticed for hours: with nobody alive nothing
    # digs, nothing spawns, and the run reports zero failed lookups. That reads as a
    # fix and is the absence of activity.
    #
    # Re-cloning makes every run start from the same state, which is the only way
    # two runs can be compared at all. The original is opened read-only.
    if ($Pristine) {
        try {
            & (Join-Path $root 'clone-save.ps1') -Source $Pristine -NewName $Save -Force | Out-Null
            Note "  colony reset from '$Pristine'"
        } catch {
            Note "  colony reset FAILED: $($_.Exception.Message)"
        }
    }

    # Fresh processes each time, so a growth reading belongs to this run only.
    try {
        Get-Process -Name OxygenNotIncluded -ErrorAction SilentlyContinue | Stop-Process -Force
        & $peerCmd -Verb stop-oni -Share $Share -TimeoutSeconds 120 | Out-Null
        Start-Sleep -Seconds 5
        & $peerCmd -Verb start-oni -Share $Share -TimeoutSeconds 120 | Out-Null
        Start-Sleep -Seconds 45
    } catch {
        Note "  restart failed: $($_.Exception.Message)"
    }

    & (Join-Path $root 'run-scenario.ps1') -Label $label -Save $Save `
        -DigCells $dig -SettleSeconds $settle -Share $Share `
        -BuildCells $hostBuild -PeerBuildCells $peerBuild -DeconstructCells $tearDown `
        -HatchEggs 3 -FabricateOrders 2 -FinishBuilds 4 -DamageBuildings 3 `
        -BuildWhat $buildWhat[($i - 1) % $buildWhat.Count] `
        -Reconnect:$reconnects[($i - 1) % $reconnects.Count] `
        -AskApi $AskApi | Out-Null
    $code = $LASTEXITCODE
    Note "  exit=$code"

    # Read the run's own artifacts, not the scenario's console output.
    #
    # The first version captured `& run-scenario ... 2>&1` and summarised that.
    # It summarised nothing: run-scenario reports through Write-Host, which goes
    # to the host and never enters the pipeline, so ten runs produced ten exit
    # codes and no findings at all. The logs were there the whole time.
    $runDir = Join-Path $root "runs\$label"
    foreach ($side in 'host', 'client') {
        $logPath = Join-Path $runDir "$side.log"
        if (-not (Test-Path $logPath)) {
            Add-Content -Path $Summary -Value "      $side : no log collected" -Encoding UTF8
            continue
        }

        foreach ($f in (Select-String -Path $logPath -Pattern '\[TEST\] RESULT FAIL')) {
            $t = ($f.Line -split 'RESULT FAIL \| ')[-1]
            if ($t.Length -gt 200) { $t = $t.Substring(0, 200) + ' ...' }
            Add-Content -Path $Summary -Value "      FAIL $side  $t" -Encoding UTF8
        }

        # Errors ONI itself raised. Excluding the ones a test provoked on purpose:
        # the handler sweep feeds every packet a bad address and some of those make
        # Klei log, which is not a finding about gameplay.
        $errCount = (Select-String -Path $logPath -Pattern '\[ERROR\]|Assert failed' |
                     Where-Object { $_.Line -notmatch '\[TEST\]|\[test-scope\]|packet-robustness-probe' }).Count
        $health = Select-String -Path $logPath -Pattern '\[HEALTH\] ' | Select-Object -Last 1
        $row = if ($health) { ($health.Line -split '\[HEALTH\] ')[-1] } else { 'no health row' }
        Add-Content -Path $Summary -Value "      $side  errors=$errCount  $row" -Encoding UTF8
    }

    # The id tables, compared. This is the sync verdict.
    #
    # Building counts were quoted three times as evidence of divergence and were
    # never evidence of anything - the two peers sample seconds apart and finish
    # buildings in between. Whether an id means the same thing on both peers does
    # not depend on when it was sampled, so that is what gets recorded per run.
    $hostLog = Join-Path $runDir 'host.log'
    $clientLog = Join-Path $runDir 'client.log'
    if ((Test-Path $hostLog) -and (Test-Path $clientLog)) {
        $verdict = & (Join-Path $root 'compare-netids.ps1') -HostLog $hostLog -ClientLog $clientLog -Examples 4 2>&1
        foreach ($line in $verdict) {
            if ("$line" -match 'ids on both peers|MISMATCH|never heard of') {
                Add-Content -Path $Summary -Value ("      netid  " + ("$line").Trim()) -Encoding UTF8
            }
        }

        # Repair, judged across the two peers. The reported bug is one number on two
        # machines, so no single-box assertion can settle it - and until this run
        # nothing damaged anything, so the in-game damage test passed on an empty set
        # every time.
        $hp = & (Join-Path $root 'compare-hp.ps1') -HostLog $hostLog -ClientLog $clientLog -Examples 4 2>&1
        foreach ($line in $hp) {
            if ("$line" -match 'damaged buildings|DIFFERENT|cannot say anything') {
                Add-Content -Path $Summary -Value ("      hp     " + ("$line").Trim()) -Encoding UTF8
            }
        }

        # Storage contents. The last big disagreement, and the one the gas-physics
        # explanation was wrong about.
        $store = & (Join-Path $root 'compare-storage.ps1') -HostLog $hostLog -ClientLog $clientLog -Examples 4 2>&1
        foreach ($line in $store) {
            if ("$line" -match 'container/item groups|COUNT \d|cannot say anything') {
                Add-Content -Path $Summary -Value ("      store  " + ("$line").Trim()) -Encoding UTF8
            }
        }

        # The verdict analyze-session already wrote, surfaced here.
        #
        # It has been reporting "PEERS DISAGREE ON IDS" and "PEERS DISAGREE ABOUT WORLD
        # STATE" on every run of this project, and this summary never showed it: the
        # grep above only picks up RESULT FAIL lines and the comparers written later.
        # A whole day went into chasing the tile that reads "scheduled" on one peer and
        # built on the other, while state_compare was naming it every run as
        # "mid-construction ... the other peer has the same building at a different
        # stage". Reading what is already measured comes before measuring more.
        $verdict = Join-Path $runDir 'verdict.txt'
        if (Test-Path $verdict) {
            foreach ($line in (Get-Content $verdict)) {
                if ("$line" -match 'diff_logs|netid_compare|state_compare|selfcheck') {
                    Add-Content -Path $Summary -Value ("      gate   " + ("$line").Trim()) -Encoding UTF8
                }
            }
        }

        # The detail behind those verdicts. analyze-session printed it to a console
        # nobody kept, so the numbers existed for months and were never read - re-run
        # here against the collected logs so the summary carries them.
        $sc = Join-Path $root 'state_compare.py'
        if ((Test-Path $sc) -and $Python) {
            foreach ($line in (& $Python $sc $hostLog $clientLog 2>&1)) {
                if ("$line" -match 'differ\s+\d|host only\s+\d|client only\s+\d|mid-construction\s+\w|buildings: host') {
                    Add-Content -Path $Summary -Value ("      xstate " + ("$line").Trim()) -Encoding UTF8
                }
            }
        }

        # Everything else: research, recipe queues, player-set flags, duplicant vitals.
        # One line per category, so a silent category cannot hide behind a noisy one.
        #
        # The examples come too, and that is the point of this block rather than a detail
        # of it. The filter here used to take only the "shared" summary lines, so the
        # summary said "research DIFFERENT 1" and never which row - and three open
        # findings sat undiagnosable for a day behind counts with no names. Running the
        # comparer by hand showed all three in one pass: two pressure doors Locked
        # against Opened, a research percentage 0.67 against 0.46, and vitals that were
        # a tenth of a percent of calorie drift.
        #
        # This is the third time in this project that a verdict existed and did not
        # reach the summary. A judgement that is not in the summary does not exist.
        $state = & (Join-Path $root 'compare-state.ps1') -HostLog $hostLog -ClientLog $clientLog -Examples 4 2>&1
        foreach ($line in $state) {
            $text = "$line"
            # 'snapshot' is in here because leaving it out dropped the one line that
            # says whether the continuous values can be judged at all - caught by
            # running the filter against real output instead of trusting it.
            if ($text -match 'shared\s+\d|facts dumped|cannot answer|not covered yet|snapshot') {
                Add-Content -Path $Summary -Value ("      state  " + $text.Trim()) -Encoding UTF8
            }
            # The category header and its examples: "--- flag ---", the bucket label,
            # and the indented rows underneath. Bounded by -Examples above rather than
            # by a filter here, so raising the cap is one number in one place.
            elseif ($text -match '^\s*---\s|^\s{2}\S.*:$|^\s{4}\S') {
                Add-Content -Path $Summary -Value ("      state    " + $text.Trim()) -Encoding UTF8
            }
        }
    }
}

Note 'soak finished'

