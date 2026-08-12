<#
.SYNOPSIS
    Build, prove the build is the thing that ships, deploy, prove both boxes
    carry it, and only then run.

.DESCRIPTION
    Written after losing two full runs to the same silent failure. The build
    failed on a missing using directive; the chained command carried on and
    deployed yesterday's DLL; the run reported zero lines from the new
    instrumentation. Zero lines reads exactly like "that code path never
    executes" - which is a finding, and it would have been a wrong one. The
    instrument was not there.

    Chaining steps with newlines lets every later step run on the wreckage of an
    earlier one. Each step here is a gate: it throws, and nothing downstream
    happens.

    The marker check is the part that is not obvious. A build can succeed and
    still not contain what was just written - a stale obj, a project that
    excludes the file, a deploy that copies from somewhere else. Searching the
    shipped bytes for the literal being looked for closes that gap: if the run
    is going to be judged on '[ClientSpawn]' lines, the DLL must be shown to
    contain '[ClientSpawn]' before the run is allowed to start.

.EXAMPLE
    .\verify.ps1 -Marker '[ClientSpawn]' -Runs 1 -Save 'X' -Pristine 'Y'
#>
[CmdletBinding()]
param(
    # Literals that must be present in the built DLL. Name whatever this run's
    # conclusion depends on.
    [string[]]$Marker = @(),
    [int]$Runs = 1,
    [Parameter(Mandatory = $true)][string]$Save,
    [string]$Pristine,
    [string]$Share = 'C:\ONI_MP_Share',
    # Game types to dump the members of, comma separated. Passed through to the
    # scenario.
    #
    # Goes through this gate rather than straight to run-scenario, and that is the
    # point: the api verb was added, run-scenario was called directly to avoid
    # restarting ONI, and the game answered "unknown command 'api'" four times
    # because mods load at startup. The gate builds, checks the marker, deploys and
    # restarts - which is the only way a new verb can exist in the running game.
    [string]$AskApi = '',
    # Skips the run and stops after deploying - for a live session, where the
    # point is to ship a verified binary and not to drive it.
    [switch]$NoRun
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$repo = Split-Path $root -Parent
# Found rather than spelled out: this profile is Korean-localised, so Documents
# is a non-ASCII name, and a non-ASCII literal in this file would be read as
# ANSI by PowerShell 5.1 and break the parse of the parameter block.
$dll = $null
foreach ($docs in @([Environment]::GetFolderPath('MyDocuments'), $env:OneDrive)) {
    if (-not $docs) { continue }
    $c = Join-Path $docs 'Klei\OxygenNotIncluded\mods\dev\ONI_Together_dev\ONI_Together.dll'
    if (Test-Path $c) { $dll = $c; break }
}

function Step($text) { Write-Host ("[verify] " + $text) }
function Fail($text) { throw ("[verify] STOP: " + $text) }

# 1. Build. The exit code decides, not the log text: dotnet writes its errors in
#    the system locale, and matching Korean error strings out of a mangled
#    console is how the failure went unnoticed the first time.
Step 'building'
& "C:\Program Files\dotnet\dotnet.exe" build (Join-Path $repo 'ONI_Together\ONI_Together.csproj') -c Debug |
    Select-String -Pattern 'error CS' | ForEach-Object { Write-Host ("  " + $_.Line.Trim()) }
if ($LASTEXITCODE -ne 0) { Fail "build failed (exit $LASTEXITCODE). Nothing was deployed." }

# 2. Prove the build contains what this run will be judged on.
if ($Marker.Count -gt 0) {
    if (-not (Test-Path $dll)) { Fail "built DLL not found at $dll" }
    # Both byte alignments, and the single-byte encodings too.
    #
    # .NET stores string literals as UTF-16, and decoding the file from offset 0
    # only sees the ones that happen to land on an even boundary. Whether a marker
    # was found then depended on how the rest of the assembly had shifted - the
    # same literal was present in one build and invisible in the next, and the gate
    # blocked a build that was in fact correct. A check that fails at random is
    # worse than no check: it teaches you to disbelieve it.
    $bytes = [System.IO.File]::ReadAllBytes($dll)
    $text = [System.Text.Encoding]::Unicode.GetString($bytes) +
            [System.Text.Encoding]::Unicode.GetString($bytes, 1, $bytes.Length - 1) +
            [System.Text.Encoding]::UTF8.GetString($bytes)

    # Split on commas as well as taking the array.
    #
    # powershell -File does not parse array arguments: passing 'a','b' arrives as
    # the single string "a,b", and the gate then looked for a marker containing a
    # comma, found nothing, and blocked a build that was in fact correct. A gate
    # that fails on its own calling convention teaches people to bypass it.
    foreach ($m in ($Marker -split ',' | Where-Object { $_ })) {
        if ($text.Contains($m)) {
            Step "marker present: $m"
        } else {
            Fail "marker '$m' is not in the built DLL. The run would have proved nothing."
        }
    }
}

# 3. Deploy, then read the hash back off the other box rather than trusting the
#    copy. A mismatched pair produces exactly the symptoms under investigation.
Step 'deploying'
Get-Process -Name OxygenNotIncluded -ErrorAction SilentlyContinue | Stop-Process -Force
& (Join-Path $root 'deploy-to-peer.ps1') -PeerPath (Join-Path $Share 'mod') | Out-Null
if ($LASTEXITCODE -ne 0) { Fail "deploy-to-peer failed (exit $LASTEXITCODE)" }

& (Join-Path $root 'peer-cmd.ps1') -Verb stop-oni -Share $Share -TimeoutSeconds 120 | Out-Null
& (Join-Path $root 'peer-cmd.ps1') -Verb pull-mod -Share $Share -TimeoutSeconds 180 | Out-Null

$hostHash = (Get-FileHash $dll -Algorithm SHA256).Hash
$peer = & (Join-Path $root 'peer-cmd.ps1') -Verb ping -Share $Share -TimeoutSeconds 120 | ConvertFrom-Json
$peerHash = $peer.result.modSha256
if (-not $peerHash) { Fail 'the client did not report a mod hash' }
if ($peerHash -ne $hostHash) {
    Fail ("binaries differ - host {0} client {1}" -f $hostHash.Substring(0,16), $peerHash.Substring(0,16))
}
Step ("both boxes on " + $hostHash.Substring(0,16))

if ($NoRun) { Step 'deployed; -NoRun given, not running'; exit 0 }

# 4. Only now is a run worth its four minutes.
Step "running $Runs"
# A hashtable, not an array. Splatting an array passes every element as a
# positional argument, so the string '-Runs' was handed to soak.ps1's first
# parameter - an int - and the failure read as a bad -Runs value rather than as
# the wrong kind of splat. Only a hashtable splat carries parameter names.
#
# ($args itself is off limits here for a different reason: it is an automatic
# variable holding this script's own unbound arguments.)
$soakArgs = @{ Runs = $Runs; Save = $Save; Share = $Share }
if ($Pristine) { $soakArgs['Pristine'] = $Pristine }
if ($AskApi) { $soakArgs['AskApi'] = $AskApi }
& (Join-Path $root 'soak.ps1') @soakArgs
exit $LASTEXITCODE
