<#
.SYNOPSIS
    Compare the two peers' NetId tables and report every disagreement.

.DESCRIPTION
    Counts are not a sync check. "4774 buildings on the host, 4776 on the client"
    was quoted three times in this work as evidence of divergence, and it is not:
    the two peers run their test suites seconds apart and buildings finish in
    between, so a small difference is a sampling artifact. It says nothing about
    whether the two peers agree on which object is which.

    The id table does say that. Each peer dumps "kind|prefab|cell|netId" for every
    registered identity, and the only question that matters is whether an object
    both peers have is called the same thing by both.

    Three findings are reported separately, because they mean different things:

      MISMATCH - both peers hold this object and disagree about its id. This is
                 the real defect: every packet addressed to it lands on nothing.
      HOST     - the host has it and the client does not.
      CLIENT   - the client has it and the host does not, which is the client
                 simulating something on its own.

    Written in PowerShell rather than Python because this machine has no Python,
    and a check that cannot be run is not a check.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$HostLog,
    [Parameter(Mandatory = $true)][string]$ClientLog,
    # Per-category cap on printed examples. The totals are always exact.
    [int]$Examples = 10
)

$ErrorActionPreference = 'Stop'

function Read-Table($path) {
    if (-not (Test-Path $path)) { throw "no log at $path" }

    $table = @{}
    # A literal with -SimpleMatch, not a regex with one. The first version passed
    # the escaped form '\[NETID\] ' together with -SimpleMatch, so it searched for
    # backslashes, found nothing, and reported "0 groups, 0 mismatches" - a clean
    # bill of health from a comparison that never read a single line.
    foreach ($line in (Select-String -Path $path -Pattern '[NETID] ' -SimpleMatch)) {
        $payload = ($line.Line -split '\[NETID\] ')[-1].Trim()
        $parts = $payload -split '\|'
        if ($parts.Count -lt 4) { continue }

        # kind|prefab|cell identifies the object; the netId is what is being
        # compared. A cell can hold several pickupables of one prefab, so the
        # key is made unique by index within its group - both peers sort the
        # dump the same way, which is what makes that comparable at all.
        $key = "{0}|{1}|{2}" -f $parts[0], $parts[1], $parts[2]
        if (-not $table.ContainsKey($key)) { $table[$key] = New-Object System.Collections.ArrayList }
        [void]$table[$key].Add($parts[3])
    }
    return $table
}

function Read-ById($path) {
    $byId = @{}
    foreach ($line in (Select-String -Path $path -Pattern '[NETID] ' -SimpleMatch)) {
        $parts = (($line.Line -split '\[NETID\] ')[-1].Trim()) -split '\|'
        if ($parts.Count -lt 4) { continue }
        # kind and prefab only. What the id points at, not where it is standing.
        $byId[$parts[3]] = "{0}|{1}" -f $parts[0], $parts[1]
    }
    return $byId
}

# The id-keyed comparison is the one that answers "is any id wrong".
#
# Keying on the cell cannot answer it. A duplicant standing in 42358 on the host
# and 34460 on the client is one duplicant that moved, and the cell-keyed pass
# reports it twice - once as host-only and once as client-only. The same is true
# of every critter and every item being carried. The first run of this reported
# 72 host-only and 47 client-only, and the examples were Minion, Drecko, Pacu:
# all mobile, all present on both peers, none of them a defect.
#
# What is fatal is an id that means different things on the two peers, because
# then every packet about it lands on the wrong object. That is position
# independent, so this pass ignores position entirely.
$hostById   = Read-ById $HostLog
$clientById = Read-ById $ClientLog

$wrongTarget = New-Object System.Collections.ArrayList
$shared = 0
foreach ($id in $hostById.Keys) {
    if (-not $clientById.ContainsKey($id)) { continue }
    $shared++
    if ($hostById[$id] -ne $clientById[$id]) {
        [void]$wrongTarget.Add(("{0}  host={1}  client={2}" -f $id, $hostById[$id], $clientById[$id]))
    }
}

Write-Output ("[netid] ids on both peers: {0}   pointing at different things: {1}" -f $shared, $wrongTarget.Count)
if ($wrongTarget.Count -gt 0) {
    Write-Output ""
    Write-Output "--- same id, different object (fatal: packets land on the wrong thing) ---"
    $wrongTarget | Select-Object -First $Examples | ForEach-Object { Write-Output ("  " + $_) }
}
Write-Output ""

# Ids the host issued that the client has never heard of, grouped by what they name.
#
# This is the position-independent version of HOST-ONLY below, and the only one of
# the two worth acting on. The cell-keyed pass reports every duplicant and critter
# that moved between the two snapshots, which is why its 111-119 was never chased:
# most of it is not a defect and there is no way to tell which part is.
#
# An id is different. If the host calls something 12345 and the client holds no
# 12345 at all, then either the client does not have the object or it named it
# something else - and both of those are real, whatever cell anything is standing
# in. Grouped by prefab because the class of object is what names the cause:
# all-critters points at creature replication, all-food at storage, a spread across
# everything at the handshake.
$hostIdSet = New-Object System.Collections.ArrayList
foreach ($id in $hostById.Keys) {
    if ($clientById.ContainsKey($id)) { continue }
    [void]$hostIdSet.Add($hostById[$id])
}

Write-Output ("[netid] ids the host issued that the client never heard of: {0}" -f $hostIdSet.Count)
if ($hostIdSet.Count -gt 0) {
    Write-Output "--- by prefab (top 12) ---"
    $hostIdSet | Group-Object | Sort-Object Count -Descending |
        Select-Object -First 12 | ForEach-Object {
            Write-Output ("  {0,5}  {1}" -f $_.Count, $_.Name)
        }
}

# And the reverse: ids the client invented that the host knows nothing about.
$clientIdSet = New-Object System.Collections.ArrayList
foreach ($id in $clientById.Keys) {
    if ($hostById.ContainsKey($id)) { continue }
    [void]$clientIdSet.Add($clientById[$id])
}
Write-Output ("[netid] ids only the client holds: {0}" -f $clientIdSet.Count)
if ($clientIdSet.Count -gt 0) {
    Write-Output "--- by prefab (top 12) ---"
    $clientIdSet | Group-Object | Sort-Object Count -Descending |
        Select-Object -First 12 | ForEach-Object {
            Write-Output ("  {0,5}  {1}" -f $_.Count, $_.Name)
        }
}
Write-Output ""

# The client's unresolved ids, named from the host's table.
#
# The [UNRESOLVED] dump is a list of numbers, and a list of numbers cannot be acted
# on - 2,316 of them read as a catastrophe and mean almost nothing. Joined against
# what the host calls each one, the same list says: 1000 Tile, 598 Ladder, 167
# TilePOI. Buildings going up during the run, with progress arriving before the
# client has finished creating them. That is the known early-packet class and it
# resolves itself; the number worth acting on is the handful that survive asking the
# host.
#
# Done here rather than by hand, because it was done by hand once and that is how a
# measurement stops being repeated.
$unresolved = @()
foreach ($line in (Select-String -Path $ClientLog -Pattern '[UNRESOLVED] ' -SimpleMatch)) {
    $unresolved += (($line.Line -split '\[UNRESOLVED\] ')[-1]).Trim()
}

if ($unresolved.Count -gt 0) {
    $named = $unresolved | Where-Object { $hostById.ContainsKey($_) }
    $orphans = $unresolved.Count - @($named).Count

    Write-Output ("[netid] client could not resolve {0} distinct id(s); the host knows {1}, " +
                  "neither peer knows {2} (dead-object references)" -f
                  $unresolved.Count, @($named).Count, $orphans)

    if (@($named).Count -gt 0) {
        Write-Output "--- what the host calls them (top 10) ---"
        $named | ForEach-Object { $hostById[$_] } | Group-Object |
            Sort-Object Count -Descending | Select-Object -First 10 |
            ForEach-Object { Write-Output ("  {0,5}  {1}" -f $_.Count, $_.Name) }
    }
    Write-Output ""
}

$hostTable   = Read-Table $HostLog
$clientTable = Read-Table $ClientLog

Write-Output ("[netid] host {0} groups, client {1} groups" -f $hostTable.Count, $clientTable.Count)

$mismatch = New-Object System.Collections.ArrayList
$hostOnly = New-Object System.Collections.ArrayList
$clientOnly = New-Object System.Collections.ArrayList

foreach ($key in $hostTable.Keys) {
    if (-not $clientTable.ContainsKey($key)) {
        [void]$hostOnly.Add($key)
        continue
    }

    # Compared as sets: within one cell the dump order of two identical piles is
    # not meaningful, but the set of ids they hold is.
    $h = @($hostTable[$key] | Sort-Object)
    $c = @($clientTable[$key] | Sort-Object)
    if (($h -join ',') -ne ($c -join ',')) {
        [void]$mismatch.Add(("{0}  host[{1}]  client[{2}]" -f $key, ($h -join ','), ($c -join ',')))
    }
}

foreach ($key in $clientTable.Keys) {
    if (-not $hostTable.ContainsKey($key)) { [void]$clientOnly.Add($key) }
}

Write-Output ""
Write-Output ("[netid] MISMATCH {0}   HOST-ONLY {1}   CLIENT-ONLY {2}" -f $mismatch.Count, $hostOnly.Count, $clientOnly.Count)

if ($mismatch.Count -gt 0) {
    Write-Output ""
    Write-Output "--- both peers hold it and disagree about the id ---"
    $mismatch | Select-Object -First $Examples | ForEach-Object { Write-Output ("  " + $_) }
}

if ($hostOnly.Count -gt 0) {
    Write-Output ""
    Write-Output "--- host has it, client does not ---"
    $hostOnly | Select-Object -First $Examples | ForEach-Object { Write-Output ("  " + $_) }
}

if ($clientOnly.Count -gt 0) {
    Write-Output ""
    Write-Output "--- client has it, host does not (client simulating on its own) ---"
    $clientOnly | Select-Object -First $Examples | ForEach-Object { Write-Output ("  " + $_) }
}

# Only a mismatch is a wrong id. A missing object is a replication gap and is
# reported, but it does not mean the two peers disagree about a name.
if ($mismatch.Count -gt 0) { exit 1 } else { exit 0 }

