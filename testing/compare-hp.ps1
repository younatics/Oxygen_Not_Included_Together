<#
.SYNOPSIS
    Compare the two peers' damaged-building tables and report every disagreement.

.DESCRIPTION
    The reported bug was "I repaired the wire and the client still shows it
    broken". That is a claim about one number on two machines, and nothing in this
    harness could check it: the scenario never damaged anything, so the in-game
    damage test passed every run by finding zero damaged buildings. A pass over an
    empty set is how the egg block and the fabricator block both hid for three runs.

    Keyed on prefab and cell rather than on NetId, deliberately. If the two peers
    disagree about the id then keying on the id would hide exactly the case worth
    finding. Buildings do not move, so the cell is a stable key - which is not true
    of the pickupables the NetId comparison has to handle.

    Three findings, and they mean different things:

      DIFFERENT  - both peers hold the building and disagree about its health. This
                   is the reported bug. If the host is whole and the client is not,
                   a repair did not replicate.
      HOST-ONLY  - damaged on the host, unharmed or absent on the client. Same
                   defect seen from the other side: the damage did not replicate.
      PEER-ONLY  - damaged on the client only, which means the client damaged it
                   locally. Its damage is supposed to be suppressed, so this is a
                   suppression leak.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$HostLog,
    [Parameter(Mandatory = $true)][string]$ClientLog,
    [int]$Examples = 10
)

$ErrorActionPreference = 'Stop'

function Read-Hp($path) {
    if (-not (Test-Path $path)) { throw "no log at $path" }

    # A literal with -SimpleMatch. Passing an escaped regex together with
    # -SimpleMatch is how the NetId comparison once reported a clean bill of health
    # from a search that matched nothing at all.
    $table = @{}
    foreach ($line in (Select-String -Path $path -Pattern '[HP] ' -SimpleMatch)) {
        $payload = ($line.Line -split '\[HP\] ')[-1].Trim()
        $parts = $payload -split '\|'
        if ($parts.Count -lt 4) { continue }

        # Last write wins: the dump may run more than once in a session and the
        # latest one describes the moment both peers were paused.
        $table["{0}|{1}" -f $parts[0], $parts[1]] = @{ Health = $parts[2]; NetId = $parts[3] }
    }
    return $table
}

$hostHp = Read-Hp $HostLog
$peerHp = Read-Hp $ClientLog

Write-Output ("[hp] damaged buildings - host {0}, client {1}" -f $hostHp.Count, $peerHp.Count)

# Nothing damaged anywhere is not a pass. It is a run that could not have found
# this bug, and saying so is the whole point of the line.
if ($hostHp.Count -eq 0 -and $peerHp.Count -eq 0) {
    Write-Output "[hp] nothing damaged on either peer - this run cannot say anything about repair"
    exit 0
}

$different = New-Object System.Collections.ArrayList
$hostOnly  = New-Object System.Collections.ArrayList
$peerOnly  = New-Object System.Collections.ArrayList

foreach ($key in $hostHp.Keys) {
    if (-not $peerHp.ContainsKey($key)) {
        [void]$hostOnly.Add(("{0}  host={1} id={2}" -f $key, $hostHp[$key].Health, $hostHp[$key].NetId))
        continue
    }
    if ($hostHp[$key].Health -ne $peerHp[$key].Health) {
        [void]$different.Add(("{0}  host={1} client={2}  hostId={3} clientId={4}" -f
            $key, $hostHp[$key].Health, $peerHp[$key].Health,
            $hostHp[$key].NetId, $peerHp[$key].NetId))
    }
}

foreach ($key in $peerHp.Keys) {
    if (-not $hostHp.ContainsKey($key)) {
        [void]$peerOnly.Add(("{0}  client={1} id={2}" -f $key, $peerHp[$key].Health, $peerHp[$key].NetId))
    }
}

Write-Output ("[hp] DIFFERENT {0}   HOST-ONLY {1}   PEER-ONLY {2}" -f
    $different.Count, $hostOnly.Count, $peerOnly.Count)

if ($different.Count -gt 0) {
    Write-Output ""
    Write-Output "--- both peers hold it and disagree about its health (the reported bug) ---"
    $different | Select-Object -First $Examples | ForEach-Object { Write-Output ("  " + $_) }
}
if ($hostOnly.Count -gt 0) {
    Write-Output ""
    Write-Output "--- damaged on the host, not on the client ---"
    $hostOnly | Select-Object -First $Examples | ForEach-Object { Write-Output ("  " + $_) }
}
if ($peerOnly.Count -gt 0) {
    Write-Output ""
    Write-Output "--- damaged on the client only (its damage is meant to be suppressed) ---"
    $peerOnly | Select-Object -First $Examples | ForEach-Object { Write-Output ("  " + $_) }
}

if ($different.Count -gt 0 -or $hostOnly.Count -gt 0 -or $peerOnly.Count -gt 0) { exit 1 } else { exit 0 }
