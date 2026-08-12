<#
.SYNOPSIS
    Compare what the two peers believe is inside each container.

.DESCRIPTION
    The last big disagreement, asked directly. The host holds 108-121 NetIds the
    client has never heard of, and that was attributed to gas and liquid physics for
    three rounds. A verdict breakdown refuted it: of the registered objects carrying
    an element, one to three were loose ephemeral matter and 193 on the host against
    139 on the client were in-storage. The 54 difference is the size of the whole
    disagreement.

    Three findings, and they name different causes:

      COUNT      - both peers hold the container and disagree about how many of an
                   item are in it. This is the one that can also break the ids: the
                   stored-item hash is (item prefab, container prefab, container
                   cell, workable type) and nothing else, so two identical items in
                   one container collide and the second is placed by a free-slot
                   walk that only sees this peer's table.
      MASS       - same count, different mass. Contents replicate but quantity
                   drifts.
      HOST-ONLY / PEER-ONLY - one peer has the item and the other does not.

    Mass is compared with a tolerance. Two peers simulating one storage will not
    agree on the last decimal, and calling that a divergence is how the building
    count comparison wasted three rounds of this project.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$HostLog,
    [Parameter(Mandatory = $true)][string]$ClientLog,
    [int]$Examples = 10,
    # A tenth of a kilogram is already below what a duplicant moves in one trip.
    [double]$MassTolerance = 0.2
)

$ErrorActionPreference = 'Stop'

function Read-Store($path) {
    if (-not (Test-Path $path)) { throw "no log at $path" }

    $table = @{}
    # A literal with -SimpleMatch. Passing an escaped regex with -SimpleMatch is how
    # the NetId comparison once gave a clean bill of health from a search that
    # matched nothing.
    foreach ($line in (Select-String -Path $path -Pattern '[STORE] ' -SimpleMatch)) {
        $payload = ($line.Line -split '\[STORE\] ')[-1].Trim()
        $parts = $payload -split '\|'
        if ($parts.Count -lt 4) { continue }

        # Last write wins - the dump may run more than once and the last one is the
        # moment both peers were paused.
        $table["{0}|{1}" -f $parts[0], $parts[1]] = @{
            Count = [int]$parts[2]
            Mass  = [double]$parts[3]
        }
    }
    return $table
}

$h = Read-Store $HostLog
$c = Read-Store $ClientLog

Write-Output ("[store] container/item groups - host {0}, client {1}" -f $h.Count, $c.Count)

if ($h.Count -eq 0 -and $c.Count -eq 0) {
    Write-Output "[store] nothing stored on either peer - this run cannot say anything about storage"
    exit 0
}

$countDiff = New-Object System.Collections.ArrayList
$massDiff  = New-Object System.Collections.ArrayList
$hostOnly  = New-Object System.Collections.ArrayList
$peerOnly  = New-Object System.Collections.ArrayList

foreach ($key in $h.Keys) {
    if (-not $c.ContainsKey($key)) {
        [void]$hostOnly.Add(("{0}  host x{1} {2}kg" -f $key, $h[$key].Count, $h[$key].Mass))
        continue
    }
    if ($h[$key].Count -ne $c[$key].Count) {
        [void]$countDiff.Add(("{0}  host x{1} client x{2}" -f $key, $h[$key].Count, $c[$key].Count))
    }
    elseif ([Math]::Abs($h[$key].Mass - $c[$key].Mass) -gt $MassTolerance) {
        [void]$massDiff.Add(("{0}  host {1}kg client {2}kg" -f $key, $h[$key].Mass, $c[$key].Mass))
    }
}

foreach ($key in $c.Keys) {
    if (-not $h.ContainsKey($key)) {
        [void]$peerOnly.Add(("{0}  client x{1} {2}kg" -f $key, $c[$key].Count, $c[$key].Mass))
    }
}

Write-Output ("[store] COUNT {0}   MASS {1}   HOST-ONLY {2}   PEER-ONLY {3}" -f
    $countDiff.Count, $massDiff.Count, $hostOnly.Count, $peerOnly.Count)

# Grouped by item prefab, because which class of item drifts names the syncer to
# look at. A flat list of 54 lines does not.
$all = @()
$all += $countDiff; $all += $hostOnly; $all += $peerOnly
if ($all.Count -gt 0) {
    Write-Output ""
    Write-Output "--- by item prefab ---"
    $all | ForEach-Object { (("$_" -split '\|')[1] -split '  ')[0] } |
        Group-Object | Sort-Object Count -Descending | Select-Object -First 12 |
        ForEach-Object { Write-Output ("  {0,5}  {1}" -f $_.Count, $_.Name) }
}

if ($countDiff.Count -gt 0) {
    Write-Output ""
    Write-Output "--- same container, different number of items (can also break the ids) ---"
    $countDiff | Select-Object -First $Examples | ForEach-Object { Write-Output ("  " + $_) }
}
if ($massDiff.Count -gt 0) {
    Write-Output ""
    Write-Output "--- same number, different mass ---"
    $massDiff | Select-Object -First $Examples | ForEach-Object { Write-Output ("  " + $_) }
}
if ($hostOnly.Count -gt 0) {
    Write-Output ""
    Write-Output "--- the host has it stored, the client does not ---"
    $hostOnly | Select-Object -First $Examples | ForEach-Object { Write-Output ("  " + $_) }
}
if ($peerOnly.Count -gt 0) {
    Write-Output ""
    Write-Output "--- the client has it stored, the host does not ---"
    $peerOnly | Select-Object -First $Examples | ForEach-Object { Write-Output ("  " + $_) }
}

if ($countDiff.Count -gt 0 -or $hostOnly.Count -gt 0 -or $peerOnly.Count -gt 0) { exit 1 } else { exit 0 }
