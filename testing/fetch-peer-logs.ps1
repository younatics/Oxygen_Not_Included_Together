<#
.SYNOPSIS
    Pull the client box's Player.log to this box and run the differ, in one step.

.DESCRIPTION
    collect-logs.ps1 snapshots the machine it runs on. In a two-box run that
    leaves the two halves on two disks and someone has to carry them together
    by hand. This closes the loop from the host box:

      1. snapshot this box's Player.log      -> runs\<Label>\host.log
      2. snapshot the peer's over WinRM      -> runs\<Label>\client.log
      3. compare the two modDllSha256 values - a mismatched pair produces
         desyncs that look exactly like the bugs under investigation
      4. run diff_logs.py and hand back its exit code

    The peer does NOT need this repo cloned; the snapshot logic is sent over
    the session. ONI keeps Player.log open, so the peer copies it to a temp
    file first and that copy is what comes across.

.EXAMPLE
    .\fetch-peer-logs.ps1 -PeerHost 192.168.45.57 -PeerUser PC-B\kyle -Label S1

.EXAMPLE
    # reuse a session you already opened (no second credential prompt)
    $s = New-PSSession -ComputerName 192.168.45.57 -Credential (Get-Credential)
    .\fetch-peer-logs.ps1 -Session $s -Label S1
#>
[CmdletBinding(DefaultParameterSetName = 'WinRM')]
param(
    [Parameter(ParameterSetName = 'WinRM', Mandatory = $true)][string]$PeerHost,
    [Parameter(ParameterSetName = 'WinRM')][string]$PeerUser,
    [Parameter(ParameterSetName = 'Session', Mandatory = $true)]
    [System.Management.Automation.Runspaces.PSSession]$Session,
    [Parameter(Mandatory = $true)][string]$Label,
    [string]$Python,
    [switch]$SkipDiff
)

$ErrorActionPreference = 'Stop'
function Info($m) { Write-Host "[fetch] $m" -ForegroundColor Cyan }
function Ok($m)   { Write-Host "[fetch] OK   $m" -ForegroundColor Green }
function Warn($m) { Write-Host "[fetch] WARN $m" -ForegroundColor Yellow }
function Die($m)  { Write-Host "[fetch] FAIL $m" -ForegroundColor Red; exit 1 }

$dest = Join-Path (Join-Path $PSScriptRoot 'runs') $Label
New-Item -ItemType Directory -Force -Path $dest | Out-Null

# --- 1. this box (host) -----------------------------------------------------
Info 'snapshotting this box'
& (Join-Path $PSScriptRoot 'collect-logs.ps1') -Role host -Label $Label -OutRoot (Join-Path $PSScriptRoot 'runs')
# collect-logs.ps1 only calls exit on failure, so on success $LASTEXITCODE keeps
# whatever the previous native command left behind. Check the artifact instead.
if (-not (Test-Path (Join-Path $dest 'host.meta.json'))) { Die 'local collect-logs.ps1 produced nothing' }

# --- 2. the peer (client) ---------------------------------------------------
$sess = $Session
if (-not $sess) {
    $p = @{ ComputerName = $PeerHost }
    if ($PeerUser) { $p.Credential = Get-Credential -UserName $PeerUser -Message "Credentials for $PeerHost" }
    Info "opening PSSession to $PeerHost"
    try { $sess = New-PSSession @p }
    catch {
        Die @"
could not open a PSSession to $PeerHost : $($_.Exception.Message)
On the CLIENT box, elevated:  Enable-PSRemoting -Force
On this box, elevated (workgroup machines need this):
  Start-Service WinRM
  Set-Item WSMan:\localhost\Client\TrustedHosts -Value '$PeerHost' -Concatenate -Force
"@
    }
}

try {
    Info 'snapshotting the peer'
    $remote = Invoke-Command -Session $sess -ScriptBlock {
        $logDir = Join-Path $env:USERPROFILE 'AppData\LocalLow\Klei\Oxygen Not Included'
        $player = Join-Path $logDir 'Player.log'
        if (-not (Test-Path $player)) { return @{ ok = $false; reason = "no Player.log at $player" } }

        # ONI holds the handle; copy first, ship the copy.
        $tmp = Join-Path $env:TEMP "oni-together-client-$(Get-Random).log"
        Copy-Item -Path $player -Destination $tmp -Force

        $modDir = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Klei\OxygenNotIncluded\mods\dev\ONI_Together_dev'
        $dll = Join-Path $modDir 'ONI_Together.dll'
        $meta = [ordered]@{
            role         = 'client'
            collectedUtc = (Get-Date).ToUniversalTime().ToString('o')
            machine      = $env:COMPUTERNAME
            lanIPv4      = @((Get-NetIPAddress -AddressFamily IPv4 |
                              Where-Object { $_.IPAddress -notlike '127.*' -and
                                             $_.IPAddress -notlike '169.254.*' }).IPAddress)
            playerLogMB  = [math]::Round((Get-Item $tmp).Length / 1MB, 2)
            modLogLines  = (Select-String -Path $tmp -Pattern '\[ONI_Together\]' -AllMatches | Measure-Object).Count
        }
        if (Test-Path $dll) {
            $f = Get-Item $dll
            $meta.modDllUtc    = $f.LastWriteTimeUtc.ToString('o')
            $meta.modDllBytes  = $f.Length
            $meta.modDllSha256 = (Get-FileHash $f.FullName -Algorithm SHA256).Hash
        }
        return @{ ok = $true; tmp = $tmp; meta = $meta }
    }

    if (-not $remote.ok) { Die "peer: $($remote.reason)" }

    $clientLog = Join-Path $dest 'client.log'
    Copy-Item -Path $remote.tmp -Destination $clientLog -FromSession $sess -Force
    Invoke-Command -Session $sess -ArgumentList $remote.tmp -ScriptBlock {
        param($t) Remove-Item $t -Force -ErrorAction SilentlyContinue
    }

    $meta = [ordered]@{}
    foreach ($k in $remote.meta.Keys) { $meta[$k] = $remote.meta[$k] }
    $meta['label'] = $Label
    $meta | ConvertTo-Json -Depth 4 | Set-Content -Path (Join-Path $dest 'client.meta.json') -Encoding utf8
    Ok "$clientLog  ($($meta.playerLogMB) MB, $($meta.modLogLines) mod lines)"
    if ($meta.modLogLines -eq 0) { Warn 'peer log has zero [ONI_Together] lines - the mod did not load there' }
} finally {
    if (-not $Session -and $sess) { Remove-PSSession $sess }
}

# --- 3. the binaries must match --------------------------------------------
$hostMeta   = Get-Content (Join-Path $dest 'host.meta.json')   -Raw | ConvertFrom-Json
$clientMeta = Get-Content (Join-Path $dest 'client.meta.json') -Raw | ConvertFrom-Json
if ($hostMeta.modDllSha256 -and $clientMeta.modDllSha256) {
    if ($hostMeta.modDllSha256 -eq $clientMeta.modDllSha256) {
        Ok "mod dll matches on both boxes ($($hostMeta.modDllSha256.Substring(0,16))...)"
    } else {
        Warn 'MOD DLL SHA256 MISMATCH - discard this run.'
        Warn "  host   $($hostMeta.modDllSha256)"
        Warn "  client $($clientMeta.modDllSha256)"
        Warn 'Re-run deploy-to-peer.ps1, restart ONI on both boxes, redo the scenario.'
        Die 'binaries differ; results would be uninterpretable'
    }
} else {
    Warn 'one side reported no mod dll hash - cannot verify the pair'
}

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
