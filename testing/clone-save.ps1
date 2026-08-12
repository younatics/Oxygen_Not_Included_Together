<#
.SYNOPSIS
    Copy a colony into a genuinely separate one, leaving the original untouched.

.DESCRIPTION
    Testing needs real data - a bug that only appears on a 6900 object colony at
    cycle 124 cannot be reproduced on a fresh map - and the colony being tested on
    is the one somebody is actually playing. Copying the file is not enough: the
    load screen groups saves by the baseName and colonyGuid inside the header, so a
    plain copy shows up as another autosave of the original and writes its own
    autosaves back into that group. Changing only one of the two folds it back in;
    both have to change.

    So this rewrites baseName and mints a fresh colonyGuid, then adjusts the header
    length field, because renaming changes how many bytes the JSON occupies. The
    original is opened read-only and never written.

.EXAMPLE
    .\clone-save.ps1 -Source '꾸밈 없는 우주 오두막' -NewName '식물버그 조사본'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Source,
    [Parameter(Mandatory = $true)][string]$NewName,
    [string]$SaveRoot,
    # Replace an existing clone, autosaves and all.
    #
    # Without this a soak reuses whatever the last run left behind: ONI autosaves
    # over the clone as it plays, so the colony drifts every run and eventually the
    # duplicants die. A dead colony reports no failed lookups because nothing
    # happens in it, which reads exactly like a fix.
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
function Ok($m)   { Write-Host "[clone] OK   $m" -ForegroundColor Green }
function Info($m) { Write-Host "[clone] $m" -ForegroundColor Cyan }
function Die($m)  { Write-Host "[clone] FAIL $m" -ForegroundColor Red; exit 1 }

if (-not $SaveRoot) {
    $docs = [Environment]::GetFolderPath('MyDocuments')
    $candidates = @(
        (Join-Path $docs 'Klei\OxygenNotIncluded\save_files'),
        (Join-Path $docs 'Klei\OxygenNotIncluded\cloud_save_files')
    )
    foreach ($c in $candidates) {
        if (Test-Path $c) {
            $hit = Get-ChildItem $c -Directory -Recurse -ErrorAction SilentlyContinue |
                   Where-Object { $_.Name -eq $Source } | Select-Object -First 1
            if ($hit) { $SaveRoot = $hit.Parent.FullName; break }
        }
    }
}
if (-not $SaveRoot) { Die "could not find a save folder containing '$Source'" }

$srcDir = Join-Path $SaveRoot $Source
if (-not (Test-Path -LiteralPath $srcDir)) { Die "no such colony folder: $srcDir" }

# Newest .sav anywhere under the colony, autosaves included - that is the state
# the player is actually in.
$srcSav = Get-ChildItem -LiteralPath $srcDir -Recurse -Filter *.sav |
          Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $srcSav) { Die "no .sav under $srcDir" }
Info "source: $($srcSav.FullName)"
Info "        $([math]::Round($srcSav.Length/1MB,1)) MB, written $($srcSav.LastWriteTime)"

$dstDir = Join-Path $SaveRoot $NewName
if ($Force -and (Test-Path $dstDir)) {
    # The autosave folder too - it is what the game loads back if it is newer.
    Remove-Item $dstDir -Recurse -Force
}
$dstSav = Join-Path $dstDir "$NewName.sav"
New-Item -ItemType Directory -Force -Path $dstDir | Out-Null

# Read the whole file once. Opened read-only so a mistake below cannot reach the
# original, which is the one rule that matters here.
$bytes = [System.IO.File]::ReadAllBytes($srcSav.FullName)

# buildVersion, headerSize, headerVersion, isCompressed, then headerSize bytes of
# UTF-8 JSON.
$headerSize    = [BitConverter]::ToInt32($bytes, 4)
$jsonStart     = 16
if ($headerSize -le 0 -or ($jsonStart + $headerSize) -gt $bytes.Length) {
    Die "header length $headerSize is not plausible for a $($bytes.Length) byte file"
}

$json = [System.Text.Encoding]::UTF8.GetString($bytes, $jsonStart, $headerSize)
if ($json -notmatch '"baseName"') { Die "no baseName in the header - format may have changed" }

$oldName = ([regex]'"baseName"\s*:\s*"([^"]*)"').Match($json).Groups[1].Value
$oldGuid = ([regex]'"colonyGuid"\s*:\s*"([^"]*)"').Match($json).Groups[1].Value
$newGuid = [guid]::NewGuid().ToString()
Info "baseName   '$oldName' -> '$NewName'"
Info "colonyGuid '$oldGuid' -> '$newGuid'"

$newJson = $json `
    -replace '("baseName"\s*:\s*")[^"]*(")', "`${1}$NewName`${2}" `
    -replace '("colonyGuid"\s*:\s*")[^"]*(")', "`${1}$newGuid`${2}"

$newJsonBytes = [System.Text.Encoding]::UTF8.GetBytes($newJson)

# Renaming changes the byte count, so the length field has to follow it. Leaving
# it stale is how a copy loads as a truncated header.
$bodyStart = $jsonStart + $headerSize
$bodyLength = $bytes.Length - $bodyStart

# A stream rather than array slicing: PowerShell's range operator hands back
# Object[], which List[byte].AddRange will not take, and the failure is a type
# error a long way from the cause.
$ms = New-Object System.IO.MemoryStream
$ms.Write($bytes, 0, 4)                                                  # buildVersion
$sizeBytes = [BitConverter]::GetBytes([int]$newJsonBytes.Length)
$ms.Write($sizeBytes, 0, 4)                                              # headerSize
$ms.Write($bytes, 8, 8)                                                  # headerVersion, isCompressed
$ms.Write($newJsonBytes, 0, $newJsonBytes.Length)
$ms.Write($bytes, $bodyStart, $bodyLength)                               # everything after the header
[System.IO.File]::WriteAllBytes($dstSav, $ms.ToArray())
$ms.Dispose()

Ok "wrote $dstSav"
Ok ("{0} MB" -f [math]::Round((Get-Item -LiteralPath $dstSav).Length/1MB, 1))
Info "the original was opened read-only and is unchanged:"
Info "  $($srcSav.FullName)  $($srcSav.LastWriteTime)"
