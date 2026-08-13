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
    [double]$Tolerance = 0.15
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
            HostOnly  = New-Object System.Collections.ArrayList
            PeerOnly  = New-Object System.Collections.ArrayList
            Shared    = 0
        }
    }
    return $cats[$category]
}

foreach ($key in $h.Keys) {
    $category = ($key -split '\|')[0]
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
        if ([Math]::Abs($hn - $cn) -le $Tolerance) { continue }
    }

    [void]$b.Different.Add(("{0}  host={1} client={2}" -f $key, $hv, $cv))
}

foreach ($key in $c.Keys) {
    if ($h.ContainsKey($key)) { continue }
    $b = Bucket ($key -split '\|')[0]
    [void]$b.PeerOnly.Add(("{0} = {1}" -f $key, $c[$key]))
}

$anyProblem = $false
foreach ($category in ($cats.Keys | Sort-Object)) {
    $b = $cats[$category]
    Write-Output ("[state] {0,-9} shared {1,5}   DIFFERENT {2,4}   HOST-ONLY {3,4}   PEER-ONLY {4,4}" -f
        $category, $b.Shared, $b.Different.Count, $b.HostOnly.Count, $b.PeerOnly.Count)
    if ($b.Different.Count -gt 0 -or $b.HostOnly.Count -gt 0 -or $b.PeerOnly.Count -gt 0) {
        $anyProblem = $true
    }
}

foreach ($category in ($cats.Keys | Sort-Object)) {
    $b = $cats[$category]
    if ($b.Different.Count -eq 0 -and $b.HostOnly.Count -eq 0 -and $b.PeerOnly.Count -eq 0) { continue }

    Write-Output ""
    Write-Output ("--- {0} ---" -f $category)
    if ($b.Different.Count -gt 0) {
        Write-Output "  both hold it, values differ:"
        $b.Different | Select-Object -First $Examples | ForEach-Object { Write-Output ("    " + $_) }
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
