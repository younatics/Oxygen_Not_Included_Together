<#
.SYNOPSIS
    One command to read a live two-box session, or to close one out.

.DESCRIPTION
    A big colony is expensive to reproduce, so a run has to yield everything it
    can the first time. This drives both boxes and every analyser in one pass:

      1. trigger the in-game suite on both peers
      2. collect both Player.logs (PC-B over the share, via peer-agent)
      3. refuse the run if the two mod DLLs differ - a mismatched pair produces
         desyncs that look exactly like the bugs under investigation
      4. diff_logs.py      two-box signature and NetId comparison
         netid_compare.py  exact (kind, prefab, cell) -> id comparison
         selfcheck_log.py  single-log audit #3 gate, run on each side
      5. print one verdict and write it to runs\<Label>\verdict.txt

    -Mode live is the same collection without the tests or the gates, for
    checking on a session while it is still running. Safe to run repeatedly.

.EXAMPLE
    .\analyze-session.ps1 -Label bigcolony -Mode live     # mid-session peek
    .\analyze-session.ps1 -Label bigcolony                # after the session
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Label,
    [ValidateSet('final', 'live')][string]$Mode = 'final',
    [string]$Share = 'C:\ONI_MP_Share',
    [string]$Python,
    [int]$TestWaitSeconds = 18
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dest = Join-Path (Join-Path $root 'runs') $Label
New-Item -ItemType Directory -Force -Path $dest | Out-Null

function Head($m) { Write-Host ''; Write-Host ('=' * 72) -ForegroundColor DarkGray; Write-Host "  $m" -ForegroundColor Cyan; Write-Host ('=' * 72) -ForegroundColor DarkGray }
function Info($m) { Write-Host "[analyze] $m" -ForegroundColor Cyan }
function Ok($m)   { Write-Host "[analyze] OK   $m" -ForegroundColor Green }
function Warn($m) { Write-Host "[analyze] WARN $m" -ForegroundColor Yellow }
function Bad($m)  { Write-Host "[analyze] FAIL $m" -ForegroundColor Red }

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
if (-not $Python) { Bad 'no usable python found'; exit 2 }

$peerCmd = Join-Path $root 'peer-cmd.ps1'
$verdict = New-Object System.Collections.Generic.List[string]
function Say($line) { Write-Host $line; $verdict.Add($line) | Out-Null }

# --- 1. run the suite on both peers ----------------------------------------
if ($Mode -eq 'final') {
    Head 'running the in-game suite on both peers'
    Set-Content (Join-Path $env:TEMP 'oni_together_cmd') 'runtests' -Encoding UTF8
    try { & $peerCmd -Verb scenario -Label 'runtests' -Share $Share -TimeoutSeconds 60 | Out-Null; Ok 'PC-B suite triggered' }
    catch { Warn "could not trigger the suite on PC-B: $($_.Exception.Message)" }
    Info "waiting ${TestWaitSeconds}s for both suites"
    Start-Sleep -Seconds $TestWaitSeconds
}

# --- 2. collect -------------------------------------------------------------
Head 'collecting'
& (Join-Path $root 'collect-logs.ps1') -Role host -Label $Label -OutRoot (Join-Path $root 'runs') | Out-Null
if (-not (Test-Path (Join-Path $dest 'host.meta.json'))) { Bad 'local collect produced nothing'; exit 2 }
Ok 'host snapshot'

try {
    & $peerCmd -Verb push-log -Label $Label -Share $Share -TimeoutSeconds 180 | Out-Null
    Copy-Item (Join-Path $Share "drop\$Label\client.log")       (Join-Path $dest 'client.log')       -Force
    Copy-Item (Join-Path $Share "drop\$Label\client.meta.json") (Join-Path $dest 'client.meta.json') -Force
    Ok 'client snapshot'
} catch {
    Bad "could not collect from PC-B: $($_.Exception.Message)"
    Warn 'is peer-agent.ps1 still running there?'
    exit 2
}

$hostMeta   = Get-Content (Join-Path $dest 'host.meta.json')   -Raw | ConvertFrom-Json
$clientMeta = Get-Content (Join-Path $dest 'client.meta.json') -Raw | ConvertFrom-Json

# --- 3. the pair has to match ----------------------------------------------
# Checked in live mode too, just not fatal there. A mismatched pair produces
# desyncs that look exactly like the bugs under investigation, so finding out
# only at the end costs a whole session - and the peer keeps its old binary
# until someone restarts the game there, which is easy to forget.
if ($hostMeta.modDllSha256 -and $clientMeta.modDllSha256) {
    if ($hostMeta.modDllSha256 -ne $clientMeta.modDllSha256) {
        Bad 'MOD DLL SHA256 MISMATCH - the two boxes are not running the same build'
        Bad "  host   $($hostMeta.modDllSha256)"
        Bad "  client $($clientMeta.modDllSha256)"
        Bad '  fix:  .\deploy-to-peer.ps1 -PeerPath C:\ONI_MP_Share\mod'
        Bad '        .\peer-cmd.ps1 -Verb stop-oni; .\peer-cmd.ps1 -Verb pull-mod; .\peer-cmd.ps1 -Verb start-oni'
        if ($Mode -eq 'final') { exit 2 }
        Warn 'continuing anyway because this is a live peek, but the run is not interpretable'
    } else {
        Ok "same binary on both boxes ($($hostMeta.modDllSha256.Substring(0,16))...)"
    }
} else { Warn 'one side reported no dll hash - the pair cannot be verified' }

# --- 4. analysers -----------------------------------------------------------
$hostLog   = Join-Path $dest 'host.log'
$clientLog = Join-Path $dest 'client.log'

Head 'in-game suite'
foreach ($side in @(@('host', $hostLog), @('client', $clientLog))) {
    $name = $side[0]; $path = $side[1]
    $end = Select-String -Path $path -Pattern '\[TEST\] END' -ErrorAction SilentlyContinue | Select-Object -Last 1
    if ($end) {
        Say ("  {0,-7} {1}" -f $name, (($end.Line -replace '^.*\[TEST\] END ', '')))
        $fails = Select-String -Path $path -Pattern '\[TEST\] RESULT FAIL' -ErrorAction SilentlyContinue
        foreach ($f in ($fails | Select-Object -Last 12)) {
            Say ('           ' + ($f.Line -replace '^.*\[TEST\] RESULT FAIL \| ', ''))
        }
    } else { Say ("  {0,-7} no [TEST] records in this log" -f $name) }
}

Head 'packet parity (host vs client)'
# Counted on both sides and shown side by side, because judging a run from one
# log has been wrong three times now: a dig order counted as mining, a paused
# run counted as a session, and "no mining observed" reported for a run that
# mined 26 ore because the packet is only logged on the receiving side. A
# replicated event should leave a trace on both. A row that is busy on one side
# and silent on the other is the finding.
$signals = @(
    'WorldDamageSpawnResource', 'DigCompletePacket', 'DeconstructComplete', 'DeconstructPacket',
    'BuildingActionPacket', 'GroundItemPickedUp', 'Registered workable', 'Registered entity',
    'Lookup failed', 'Overwriting existing entity', 'Failed to handle packet',
    # Ore the client builds from the host's spawn packet and destroys in the same
    # breath, because a pickup notice for it had already arrived. The object then
    # exists on the host and not on the client - which is what host-only
    # Pickupables in netid_compare are. Counted because the packet header has no
    # sequence or tick, so the receiver cannot tell that ordering apart.
    'Consumed pending ground-item pickup', 'not yet registered; queued pending removal',
    'already held by'
)
Say ("  {0,-32} {1,8} {2,8}" -f 'signal', 'host', 'client')
Say ("  {0,-32} {1,8} {2,8}" -f ('-' * 32), '--------', '--------')
foreach ($s in $signals) {
    $hc = (Select-String -Path $hostLog   -Pattern ([regex]::Escape($s)) -ErrorAction SilentlyContinue | Measure-Object).Count
    $cc = (Select-String -Path $clientLog -Pattern ([regex]::Escape($s)) -ErrorAction SilentlyContinue | Measure-Object).Count
    $flag = ''
    if (($hc -eq 0) -ne ($cc -eq 0)) { $flag = '  <-- one side only' }
    elseif ($hc -gt 0 -and $cc -gt 0 -and ([math]::Max($hc, $cc) / [math]::Max(1, [math]::Min($hc, $cc))) -ge 5) { $flag = '  <-- lopsided' }
    Say ("  {0,-32} {1,8} {2,8}{3}" -f $s, $hc, $cc, $flag)
}

Head 'two-box divergence (diff_logs.py)'
& $Python (Join-Path $root 'diff_logs.py') $hostLog $clientLog --json (Join-Path $dest 'diff.json')
$diffCode = $LASTEXITCODE

Head 'exact NetId comparison (netid_compare.py)'
& $Python (Join-Path $root 'netid_compare.py') $hostLog $clientLog --json (Join-Path $dest 'netid.json')
$netidCode = $LASTEXITCODE

Head 'single-log audit #3 gate (selfcheck_log.py)'
$selfCodes = @{}
foreach ($side in @(@('host', $hostLog), @('client', $clientLog))) {
    Write-Host "--- $($side[0]) ---" -ForegroundColor DarkGray
    & $Python (Join-Path $root 'selfcheck_log.py') $side[1] | Select-String -Pattern 'CONFIRMED|VERDICT|keys that got|groups whose|exit code' -Context 0,0
    $selfCodes[$side[0]] = $LASTEXITCODE
}

# --- 5. verdict -------------------------------------------------------------
Head "VERDICT  $Label"
Say ("  host log     {0} MB, {1} mod lines" -f $hostMeta.playerLogMB, $hostMeta.modLogLines)
Say ("  client log   {0} MB, {1} mod lines" -f $clientMeta.playerLogMB, $clientMeta.modLogLines)
Say ("  diff_logs      exit {0}  {1}" -f $diffCode,   $(if ($diffCode   -eq 1) { 'DIVERGENCE CONFIRMED' } elseif ($diffCode   -eq 0) { 'clean' } else { 'error' }))
Say ("  netid_compare  exit {0}  {1}" -f $netidCode,  $(if ($netidCode  -eq 1) { 'PEERS DISAGREE ON IDS' } elseif ($netidCode -eq 0) { 'ids agree' } else { 'no dump - was runtests triggered?' }))
Say ("  selfcheck      host exit {0}, client exit {1}" -f $selfCodes['host'], $selfCodes['client'])
Say ''
Say "  artifacts: $dest"

$verdict -join "`r`n" | Set-Content (Join-Path $dest 'verdict.txt') -Encoding UTF8

if ($Mode -eq 'live') { exit 0 }
if ($diffCode -eq 1 -or $netidCode -eq 1) { exit 1 }
exit 0
