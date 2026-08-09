<#
.SYNOPSIS
    Host-side half of the share-based loop: snapshot this box, pick up the
    client log the peer dropped, verify the pair, run the differ.

.DESCRIPTION
    This is fetch-peer-logs.ps1 for the case where WinRM cannot be used - both
    boxes here sign in with passwordless Microsoft accounts, which have no NTLM
    hash and therefore cannot authenticate a PSSession at all. The client pushes
    its log to a share on this box instead (peer-sync.ps1 -Mode push).

    The rule-2 gate is unchanged: if the two modDllSha256 values differ, the run
    is uninterpretable and the differ does not run.

.EXAMPLE
    .\collect-from-drop.ps1 -Label S1
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Label,
    [string]$Drop = 'C:\ONI_MP_Share\drop',
    [string]$Python,
    [switch]$SkipDiff
)

$ErrorActionPreference = 'Stop'
function Info($m) { Write-Host "[collect] $m" -ForegroundColor Cyan }
function Ok($m)   { Write-Host "[collect] OK   $m" -ForegroundColor Green }
function Warn($m) { Write-Host "[collect] WARN $m" -ForegroundColor Yellow }
function Die($m)  { Write-Host "[collect] FAIL $m" -ForegroundColor Red; exit 1 }

$dest = Join-Path (Join-Path $PSScriptRoot 'runs') $Label
New-Item -ItemType Directory -Force -Path $dest | Out-Null

# --- 1. this box (host) -----------------------------------------------------
Info 'snapshotting this box'
& (Join-Path $PSScriptRoot 'collect-logs.ps1') -Role host -Label $Label -OutRoot (Join-Path $PSScriptRoot 'runs')
if (-not (Test-Path (Join-Path $dest 'host.meta.json'))) { Die 'local collect-logs.ps1 produced nothing' }

# --- 2. what the peer dropped -----------------------------------------------
$src = Join-Path $Drop $Label
$srcLog  = Join-Path $src 'client.log'
$srcMeta = Join-Path $src 'client.meta.json'
if (-not (Test-Path $srcLog)) {
    Die "no client log at $srcLog - on PC-B run: \\KYLE\onimp\peer-sync.ps1 -Share \\KYLE\onimp -Mode push -Label $Label"
}
Copy-Item $srcLog  (Join-Path $dest 'client.log')       -Force
Copy-Item $srcMeta (Join-Path $dest 'client.meta.json') -Force
$mb = [math]::Round((Get-Item (Join-Path $dest 'client.log')).Length / 1MB, 2)
Ok "client.log picked up (${mb} MB)"

# --- 3. the binaries must match ---------------------------------------------
$hostMeta   = Get-Content (Join-Path $dest 'host.meta.json')   -Raw | ConvertFrom-Json
$clientMeta = Get-Content (Join-Path $dest 'client.meta.json') -Raw | ConvertFrom-Json
if ($hostMeta.modDllSha256 -and $clientMeta.modDllSha256) {
    if ($hostMeta.modDllSha256 -eq $clientMeta.modDllSha256) {
        Ok "mod dll matches on both boxes ($($hostMeta.modDllSha256.Substring(0,16))...)"
    } else {
        Warn 'MOD DLL SHA256 MISMATCH - discard this run.'
        Warn "  host   $($hostMeta.modDllSha256)"
        Warn "  client $($clientMeta.modDllSha256)"
        Warn 'Re-publish (deploy-to-peer.ps1 -PeerPath C:\ONI_MP_Share\mod), re-pull on PC-B, restart both games, redo the scenario.'
        Die 'binaries differ; results would be uninterpretable'
    }
} else {
    Warn 'one side reported no mod dll hash - cannot verify the pair'
}
if ($clientMeta.modLogLines -eq 0) { Warn 'client log has zero [ONI_Together] lines - the mod did not load on PC-B' }

# --- 4. diff ----------------------------------------------------------------
if ($SkipDiff) { Ok "collected into $dest"; exit 0 }

if (-not $Python) {
    $c = Get-Command python -ErrorAction SilentlyContinue
    # The Store stub is a 0-byte reparse point that exits without running anything.
    if ($c -and (Get-Item $c.Source).Length -gt 0) { $Python = $c.Source }
    else {
        $cand = Get-ChildItem "$env:LOCALAPPDATA\Programs\Python" -Filter python.exe -Recurse -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -notlike '*\venv\*' } | Select-Object -First 1
        if ($cand) { $Python = $cand.FullName }
    }
}
if (-not $Python) {
    Warn 'no usable python found - collected the logs but skipped the diff. Run manually:'
    Warn "  python diff_logs.py `"$dest\host.log`" `"$dest\client.log`""
    exit 0
}

Info "diff_logs.py ($Python)"
& $Python (Join-Path $PSScriptRoot 'diff_logs.py') `
    (Join-Path $dest 'host.log') (Join-Path $dest 'client.log') `
    --json (Join-Path $dest 'diff.json')
$code = $LASTEXITCODE
Write-Host ''
if ($code -eq 1) { Warn "CONFIRMED divergence - see $dest\diff.json" }
elseif ($code -eq 0) { Ok 'no confirmed divergence in this run' }
else { Warn "diff_logs.py exited $code" }
exit $code
