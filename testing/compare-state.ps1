<#
.SYNOPSIS
    Compare every category of game state the two peers dumped, in one pass.

.DESCRIPTION
    The fourth comparison tool, and the last one that should ever need writing. Damage,
    container contents and the id tables each got their own dump and their own comparer -
    about a hundred lines apiece - and the categories nobody had written a pair for
    stayed uncompared. Both of the worst bugs found by playing were in those: research
    read half finished on the client, and a fabricator's recipe queue read zero.

    So the game side emits one uniform row - category|key|field|value - and this diffs
    whatever categories are present. A new category costs a few lines in the dump and
    nothing here.

    Findings are reported per category, because the categories fail for different
    reasons and a single total would hide which one:

      DIFFERENT   both peers hold the fact and disagree about the value
      HOST-ONLY   the host has it, the client does not
      PEER-ONLY   the client has it, the host does not

    Numeric fields are compared with a tolerance, because two peers running the same
    simulation will not agree on the last decimal - and reporting that as divergence is
    how the building count comparison wasted three rounds of this project. Fields that
    are not numbers are compared exactly.

    A flat tolerance is not enough on its own. Duplicant calories run into the millions
    and move every tick, and the two peers do not dump at the same instant, so 48 of 84
    vital rows were reported as divergence on numbers like 3,066,579 against 3,062,743 -
    a tenth of a percent apart. Real vital divergence, a duplicant starving on one peer,
    would have been buried in that.

    So a difference within a relative tolerance is reported as NEAR rather than folded
    into agreement. Nothing is hidden - the row is still named and countable - but the
    DIFFERENT column goes back to meaning something a player would see. A number that is
    small in absolute terms still passes on the flat tolerance, since a percentage moving
    from 0 to 1 is not a tenth of a percent of anything.

    NEAR does not fail the gate. It is drift between two snapshots taken moments apart,
    and treating it as failure is the same mistake in the other direction.

    A category present on neither peer is reported as "not measured" rather than passing
    silently. A pass over an empty set is how the damage test stayed green for a hundred
    runs while damage replication was never exercised at all.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$HostLog,
    [Parameter(Mandatory = $true)][string]$ClientLog,
    [int]$Examples = 6,
    # A tenth is below anything a player can read off a building, and well above
    # float noise between two simulations.
    [double]$Tolerance = 0.15,
    # Half a percent of the larger side. Chosen against the measured spread rather
    # than picked: the calorie rows sit near a tenth of a percent apart and the stamina
    # and research rows that are worth seeing are 1% and 31%, so this separates them
    # with room on both sides. Raise it and real drift starts disappearing; the two
    # numbers to check it against are printed in every report.
    [double]$RelativeTolerance = 0.005
)

$ErrorActionPreference = 'Stop'

function Read-State($path) {
    if (-not (Test-Path $path)) { throw "no log at $path" }

    $table = @{}
    # A literal with -SimpleMatch. Passing an escaped regex alongside -SimpleMatch is
    # how the NetId comparison once reported a clean bill of health from a search that
    # matched nothing at all.
    # GAMESTATE, not STATE. StateDivergenceTests emits [STATE] rows with an entirely
    # different schema - NetId, syncer name, key, value - and reading both produced
    # category names that were NetIds. The tag was already taken; a one-line check of
    # the source before reusing it would have saved a run.
    foreach ($line in (Select-String -Path $path -Pattern '[GAMESTATE] ' -SimpleMatch)) {
        $payload = ($line.Line -split '\[GAMESTATE\] ')[-1].Trim()
        $parts = $payload -split '\|'
        if ($parts.Count -lt 4) { continue }

        # category|key|field is the identity; the rest is the value. Split on the
        # first three separators only, so a value containing one survives.
        $value = ($parts[3..($parts.Count - 1)]) -join '|'
        $table["{0}|{1}|{2}" -f $parts[0], $parts[1], $parts[2]] = $value
    }

    # The structure syncers' own dump, in the same table.
    #
    # StateDivergenceTests emits [STATE] netId|syncerName|key|value, and the only
    # comparer for it was state_compare.py - on a machine with no Python. The stub
    # returns exit 1, and analyze-session reports exit 1 as "PEERS DISAGREE ABOUT
    # WORLD STATE", so every run in this project's history has carried a red flag
    # that meant "python is missing". compare-netids.ps1 was written in PowerShell
    # for exactly this reason and says so in its own header; the lesson just never
    # reached this file.
    #
    # Keyed by syncer rather than by NetId, so the category says which subsystem
    # disagrees. The NetId stays in the key because that is what the dump has.
    foreach ($line in (Select-String -Path $path -Pattern '[STATE] ' -SimpleMatch)) {
        $payload = ($line.Line -split '\[STATE\] ')[-1].Trim()
        $parts = $payload -split '\|'
        if ($parts.Count -lt 4) { continue }
        # netId|syncer|key|value - skip the not-covered note and anything malformed.
        if ($parts[0] -notmatch '^-?\d+$') { continue }

        $value = ($parts[3..($parts.Count - 1)]) -join '|'
        $table["sync:{0}|{1}|{2}" -f $parts[1], $parts[0], $parts[2]] = $value
    }

    return $table
}

# Categories whose rows carry a measured rate beside them. A category is in here
# because the game side emits <name>rate| rows for it, not because someone decided
# it deserves a looser rule.
$rateCategories = [System.Collections.Generic.HashSet[string]]::new()
foreach ($cat in @('vital', 'critter')) { [void]$rateCategories.Add($cat) }

$h = Read-State $HostLog
$c = Read-State $ClientLog

Write-Output ("[state] facts dumped - host {0}, client {1}" -f $h.Count, $c.Count)

if ($h.Count -eq 0 -and $c.Count -eq 0) {
    Write-Output "[state] neither peer dumped any state - this run cannot answer anything"
    exit 0
}

# Per category, so one noisy category cannot hide a silent one.
$cats = @{}
function Bucket($category) {
    if (-not $cats.ContainsKey($category)) {
        $cats[$category] = [pscustomobject]@{
            Different = New-Object System.Collections.ArrayList
            Near      = New-Object System.Collections.ArrayList
            HostOnly  = New-Object System.Collections.ArrayList
            PeerOnly  = New-Object System.Collections.ArrayList
            Shared    = 0
        }
    }
    return $cats[$category]
}

# How far apart the two snapshots were taken, before anything is judged.
#
# The peers dump on separate commands and the client's has a round trip in front of
# it, so continuously-moving values differ for a reason that has nothing to do with
# replication. Reported rather than corrected for: a reader who knows the gap can
# judge a calorie difference, and a comparer that silently normalised it would be
# hiding the case where the gap is zero and the values still disagree.
$simKey = 'meta|_snapshot|simTime'

# How many seconds of movement a continuously-changing value is allowed.
#
# One sync period, because the client's copy of a host-simulated value is at most one
# packet old, plus however far apart the two snapshots were actually taken. Both terms
# are physical: neither is a threshold anybody chose, and the second is measured on
# every run rather than assumed to be zero.
#
# It defaults to the sync period alone when the clock is missing, which is the smallest
# honest answer - a missing measurement must not buy extra slack.
$syncPeriodSeconds = 1.0
$allowedSeconds = $syncPeriodSeconds

if ($h.ContainsKey($simKey) -and $c.ContainsKey($simKey)) {
    $gap = [double]$c[$simKey] - [double]$h[$simKey]
    $allowedSeconds = $syncPeriodSeconds + [Math]::Abs($gap)
    Write-Output ("[state] snapshots taken {0:N2}s apart in sim time (host {1}, client {2})" -f
        $gap, $h[$simKey], $c[$simKey])
    Write-Output ("[state] a continuously-moving value may differ by {0:N2}s of its own measured rate" -f
        $allowedSeconds)
} else {
    Write-Output "[state] snapshot clock missing on at least one peer - continuous values cannot be judged"
}

foreach ($key in $h.Keys) {
    $category = ($key -split '\|')[0]
    # The clock is not a fact about agreement; the peers are meant to differ on it.
    # meta is the snapshot clock and anything ending in 'rate' is the yardstick its own
    # category is judged with - both are inputs to the comparison, not facts the peers
    # should agree about. Only the receiving peer measures a rate, so leaving those in
    # would report every one as a one-sided difference.
    if ($category -eq 'meta' -or $category.EndsWith('rate') -or $category.EndsWith('sent') -or $category.EndsWith('age')) { continue }

    # A pipe cell nobody ever sent is not a replication failure.
    #
    # ConduitFlowSyncer emits contents only for cells a client is currently looking at,
    # so an off-screen pipe is never replicated and the client keeps whatever its own
    # simulation made. Around 44 cells disagreed in every run for that reason, and the
    # report could not tell them from a cell that was sent and did not arrive - which is
    # the one worth fixing.
    #
    # Reported under its own name rather than dropped. It is a real difference between
    # the two worlds and a player who scrolls there sees it corrected within a refresh
    # interval; what it is not is evidence that the syncer is failing.
    if ($category -eq 'conduit') {
        $sentKey = ($key -replace '^conduit\|', 'conduitsent|') -replace '\|[^|]+$', '|sent'
        if (-not $h.ContainsKey($sentKey)) {
            $b = Bucket 'conduit-neversent'
            if (-not $c.ContainsKey($key)) {
                [void]$b.HostOnly.Add(("{0} = {1}" -f $key, $h[$key]))
            } elseif ($h[$key] -ne $c[$key]) {
                $b.Shared++
                [void]$b.Different.Add(("{0}  host={1} client={2}" -f $key, $h[$key], $c[$key]))
            } else {
                $b.Shared++
            }
            continue
        }
    }

    $b = Bucket $category

    if (-not $c.ContainsKey($key)) {
        [void]$b.HostOnly.Add(("{0} = {1}" -f $key, $h[$key]))
        continue
    }

    $b.Shared++
    $hv = $h[$key]; $cv = $c[$key]
    if ($hv -eq $cv) { continue }

    # Numbers within tolerance are agreement, not divergence.
    $hn = 0.0; $cn = 0.0
    if ([double]::TryParse($hv, [ref]$hn) -and [double]::TryParse($cv, [ref]$cn)) {
        $delta = [Math]::Abs($hn - $cn)
        if ($delta -le $Tolerance) { continue }

        # A vital is judged against how fast it actually moves, not a percentage.
        #
        # Calories run into the millions and stamina from 0 to 100, so one tolerance
        # cannot fit both, and the duplicant at maximum stress burns two hundred times
        # faster than the rest. The client measures the size of every correction it
        # applies - one sync period of that duplicant's own drift - and emits it beside
        # the value. Two sync periods is the bound here: the peers cannot be closer than
        # one, and allowing two covers the snapshot not being simultaneous.
        #
        # This is judging against the physical bound rather than tuning a number until
        # the report looks better. A row outside it is a real disagreement.
        #
        # Driven by which categories publish a rate, not by a list of category names.
        # This was written for vital| and critter amounts needed the same treatment a
        # day later; a rule keyed to one name has to be edited for every category that
        # ever measures its own drift, and the one that gets forgotten falls back to a
        # percentage in silence - which is how a threshold ends up judging something it
        # was never chosen for.
        if ($rateCategories.Contains($category)) {
            $rateKey = $key -replace "^$category\|", "${category}rate|"
            $rate = 0.0
            if ($c.ContainsKey($rateKey)) { [double]::TryParse($c[$rateKey], [ref]$rate) | Out-Null }
            elseif ($h.ContainsKey($rateKey)) { [double]::TryParse($h[$rateKey], [ref]$rate) | Out-Null }

            # rate is per second, so the bound is seconds - and the seconds that matter
            # are the ones between the two snapshots plus the sync period the correction
            # can lag by. The gap is measured and printed at the top of this report;
            # using it here rather than a constant is the same principle as using a
            # measured rate rather than a chosen percentage.
            if ($rate -gt 0 -and $delta -le ($rate * $allowedSeconds)) {
                [void]$b.Near.Add(("{0}  host={1} client={2}  ({3:N2} apart; moves {4:N3}/s, {5:N1}s of slack)" -f
                    $key, $hv, $cv, $delta, $rate, $allowedSeconds))
                continue
            }
        }

        # Large values that moved a little between two snapshots taken moments apart.
        # Named and counted, never dropped - see the header.
        $scale = [Math]::Max([Math]::Abs($hn), [Math]::Abs($cn))
        if ($scale -gt 0 -and ($delta / $scale) -le $RelativeTolerance) {
            [void]$b.Near.Add(("{0}  host={1} client={2}  ({3:P3} apart)" -f
                $key, $hv, $cv, ($delta / $scale)))
            continue
        }
    }

    [void]$b.Different.Add(("{0}  host={1} client={2}" -f $key, $hv, $cv))
}

foreach ($key in $c.Keys) {
    if ($h.ContainsKey($key)) { continue }
    $category = ($key -split '\|')[0]
    # meta is the snapshot clock, anything ending in 'rate' is the yardstick its own
    # category is judged with, and anything ending in 'sent' says whether a row was ever
    # transmitted - all three are inputs to the comparison, not facts the peers should
    # agree about. Only one peer emits them, so leaving them in would report every one
    # as a one-sided difference.
    if ($category -eq 'meta' -or $category.EndsWith('rate') -or $category.EndsWith('sent') -or $category.EndsWith('age')) { continue }

    # Same split as the host loop: a pipe cell the host never sent cannot be a
    # replication failure, and a client-only one is the client's own simulation having
    # made something there.
    if ($category -eq 'conduit') {
        $sentKey = ($key -replace '^conduit\|', 'conduitsent|') -replace '\|[^|]+$', '|sent'
        if (-not $h.ContainsKey($sentKey)) { $category = 'conduit-neversent' }
    }

    $b = Bucket $category
    [void]$b.PeerOnly.Add(("{0} = {1}" -f $key, $c[$key]))
}

$anyProblem = $false
foreach ($category in ($cats.Keys | Sort-Object)) {
    $b = $cats[$category]
    Write-Output ("[state] {0,-9} shared {1,5}   DIFFERENT {2,4}   NEAR {3,4}   HOST-ONLY {4,4}   PEER-ONLY {5,4}" -f
        $category, $b.Shared, $b.Different.Count, $b.Near.Count, $b.HostOnly.Count, $b.PeerOnly.Count)
    # NEAR is deliberately not here. It is two snapshots taken moments apart, and
    # failing the gate on it is how a comparison becomes noise nobody reads.
    if ($b.Different.Count -gt 0 -or $b.HostOnly.Count -gt 0 -or $b.PeerOnly.Count -gt 0) {
        $anyProblem = $true
    }
}

foreach ($category in ($cats.Keys | Sort-Object)) {
    $b = $cats[$category]
    if ($b.Different.Count -eq 0 -and $b.HostOnly.Count -eq 0 -and $b.PeerOnly.Count -eq 0 -and
        $b.Near.Count -eq 0) { continue }

    Write-Output ""
    Write-Output ("--- {0} ---" -f $category)
    if ($b.Different.Count -gt 0) {
        Write-Output "  both hold it, values differ:"
        $b.Different | Select-Object -First $Examples | ForEach-Object { Write-Output ("    " + $_) }
    }
    if ($b.Near.Count -gt 0) {
        Write-Output "  near - drift between two snapshots, not a gate failure:"
        $b.Near | Select-Object -First $Examples | ForEach-Object { Write-Output ("    " + $_) }
    }
    if ($b.HostOnly.Count -gt 0) {
        Write-Output "  host only:"
        $b.HostOnly | Select-Object -First $Examples | ForEach-Object { Write-Output ("    " + $_) }
    }
    if ($b.PeerOnly.Count -gt 0) {
        Write-Output "  client only:"
        $b.PeerOnly | Select-Object -First $Examples | ForEach-Object { Write-Output ("    " + $_) }
    }
}

# Categories the dump says it does not cover, echoed so a clean report is not read as
# "everything agrees".
foreach ($line in (Select-String -Path $HostLog -Pattern '[GAMESTATE] not covered yet' -SimpleMatch)) {
    Write-Output ""
    Write-Output ("[state] " + (($line.Line -split '\[GAMESTATE\] ')[-1]).Trim())
    break
}

if ($anyProblem) { exit 1 } else { exit 0 }
