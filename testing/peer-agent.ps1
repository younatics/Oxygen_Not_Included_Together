<#
.SYNOPSIS
    Run this ONCE on the client box (PC-B) and leave it running. It polls the
    host's share for requests and answers them, so the host side can drive the
    test loop without a human at PC-B.

.DESCRIPTION
    WinRM is unavailable here: both boxes sign in with passwordless Microsoft
    accounts, which have no NTLM hash, so New-PSSession can never authenticate.
    The share is the only channel, and only the client can open it. So the
    client polls instead of the host pushing.

    This is deliberately NOT a remote shell. It dispatches a fixed set of verbs
    and ignores anything else - a share is a weak trust boundary and arbitrary
    command execution over it would be a real hole, not a convenience.

    Verbs (written by the host as <share>\cmd\<id>.req.json):
      ping        - report machine, mod dll hash, whether ONI is running
      pull-mod    - copy <share>\mod\ONI_Together_dev over the local dev folder,
                    verify sha256, refuse on mismatch
      push-log    - snapshot Player.log + meta into <share>\drop\<label>\
      stop-oni    - close ONI so a new binary can be loaded
      start-oni   - launch ONI through Steam (joining a session still needs a
                    human - this only gets the process up)
      quit        - stop this agent

    Replies land at <share>\cmd\<id>.done.json. Requests are processed oldest
    first and deleted once answered.

.EXAMPLE
    Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
    \\KYLE\onimp\peer-agent.ps1 -Share \\KYLE\onimp
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Share,
    [int]$IntervalSeconds = 5,
    [string]$AppId = '457140'   # Oxygen Not Included
)

$ErrorActionPreference = 'Stop'
function Info($m) { Write-Host "[agent] $m" -ForegroundColor Cyan }
function Ok($m)   { Write-Host "[agent] OK   $m" -ForegroundColor Green }
function Warn($m) { Write-Host "[agent] WARN $m" -ForegroundColor Yellow }

if (-not (Test-Path $Share)) {
    Write-Host "[agent] FAIL cannot reach $Share" -ForegroundColor Red
    Write-Host "        net use $Share /user:<PC-A>\onishare" -ForegroundColor Red
    exit 1
}

$cmdDir  = Join-Path $Share 'cmd'
$dropDir = Join-Path $Share 'drop'
New-Item -ItemType Directory -Force -Path $cmdDir, $dropDir | Out-Null

$localDev = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Klei\OxygenNotIncluded\mods\dev'
$localMod = Join-Path $localDev 'ONI_Together_dev'
$playerLog = Join-Path $env:USERPROFILE 'AppData\LocalLow\Klei\Oxygen Not Included\Player.log'

function Get-ModHash {
    $dll = Join-Path $localMod 'ONI_Together.dll'
    if (Test-Path $dll) { (Get-FileHash $dll -Algorithm SHA256).Hash } else { $null }
}

function Invoke-PullMod {
    $srcMod = Join-Path $Share 'mod\ONI_Together_dev'
    $srcDll = Join-Path $srcMod 'ONI_Together.dll'
    if (-not (Test-Path $srcDll)) { throw "no published build at $srcDll" }
    $srcHash = (Get-FileHash $srcDll -Algorithm SHA256).Hash

    New-Item -ItemType Directory -Force -Path $localDev | Out-Null
    # Wipe first so files deleted upstream do not linger and get loaded.
    if (Test-Path $localMod) { Remove-Item $localMod -Recurse -Force }
    Copy-Item -Path $srcMod -Destination $localDev -Recurse -Force

    $dstHash = Get-ModHash
    if ($dstHash -ne $srcHash) { throw "hash mismatch after copy (host $srcHash vs local $dstHash)" }
    @{ sha256 = $dstHash; path = $localMod }
}

function Invoke-PushLog($label) {
    if (-not $label) { throw 'push-log needs a label' }
    if (-not (Test-Path $playerLog)) { throw "no Player.log at $playerLog" }
    $dest = Join-Path $dropDir $label
    New-Item -ItemType Directory -Force -Path $dest | Out-Null

    # ONI holds the handle; copy locally first, then ship the copy.
    $tmp = Join-Path $env:TEMP "oni-together-client-$PID.log"
    Copy-Item $playerLog $tmp -Force
    Copy-Item $tmp (Join-Path $dest 'client.log') -Force

    # The session before this one, always. ONI rotates Player.log to
    # Player-prev.log at startup, so a client that crashed and was restarted
    # has its whole crash in the previous file and a nearly empty current one.
    # That is exactly the session worth reading, and collecting only Player.log
    # threw it away every time: the last crash investigation opened a 0.03 MB
    # client log and had nothing in it.
    $prevLog = Join-Path (Split-Path $playerLog) 'Player-prev.log'
    if (Test-Path $prevLog) {
        $tmpPrev = Join-Path $env:TEMP "oni-together-client-prev-$PID.log"
        Copy-Item $prevLog $tmpPrev -Force
        Copy-Item $tmpPrev (Join-Path $dest 'client-prev.log') -Force
    }

    $size     = [math]::Round((Get-Item $tmp).Length / 1MB, 2)
    $modLines = (Select-String -Path $tmp -Pattern '\[ONI_Together\]' -AllMatches | Measure-Object).Count
    $meta = [ordered]@{
        role         = 'client'
        label        = $label
        collectedUtc = (Get-Date).ToUniversalTime().ToString('o')
        machine      = $env:COMPUTERNAME
        lanIPv4      = @((Get-NetIPAddress -AddressFamily IPv4 |
                          Where-Object { $_.IPAddress -notlike '127.*' -and
                                         $_.IPAddress -notlike '169.254.*' }).IPAddress)
        playerLogMB  = $size
        modLogLines  = $modLines
        hasPrevLog   = (Test-Path (Join-Path $dest 'client-prev.log'))
    }
    $dll = Join-Path $localMod 'ONI_Together.dll'
    if (Test-Path $dll) {
        $f = Get-Item $dll
        $meta.modDllUtc    = $f.LastWriteTimeUtc.ToString('o')
        $meta.modDllBytes  = $f.Length
        $meta.modDllSha256 = (Get-FileHash $f.FullName -Algorithm SHA256).Hash
    }
    $meta | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $dest 'client.meta.json') -Encoding UTF8
    Remove-Item $tmp -Force -ErrorAction SilentlyContinue
    @{ playerLogMB = $size; modLogLines = $modLines; dest = $dest }
}

function Get-OniProcess { Get-Process -Name 'OxygenNotIncluded' -ErrorAction SilentlyContinue }

Info "share   : $Share"
Info "mod     : $localMod"
Info "sha256  : $(Get-ModHash)"
Info "polling every ${IntervalSeconds}s - Ctrl-C to stop"

$running = $true
while ($running) {
    try {
        $reqs = Get-ChildItem $cmdDir -Filter '*.req.json' -ErrorAction SilentlyContinue |
                Sort-Object LastWriteTime
        foreach ($r in $reqs) {
            $id = $r.BaseName -replace '\.req$', ''
            $reply = [ordered]@{ id = $id; machine = $env:COMPUTERNAME }
            try {
                $req = Get-Content $r.FullName -Raw | ConvertFrom-Json
                Info "-> $($req.verb) $($req.label)"
                switch ($req.verb) {
                    'ping' {
                        $p = Get-OniProcess
                        $reply.result = @{
                            modSha256 = Get-ModHash
                            oniRunning = [bool]$p
                            oniStartedUtc = if ($p) { $p.StartTime.ToUniversalTime().ToString('o') } else { $null }
                            playerLogUtc = if (Test-Path $playerLog) { (Get-Item $playerLog).LastWriteTimeUtc.ToString('o') } else { $null }
                        }
                    }
                    'pull-mod'  { $reply.result = Invoke-PullMod }
                    'push-log'  { $reply.result = Invoke-PushLog $req.label }
                    'run-tests' {
                        # UnitTestRunner.Tick polls this path from Game.Update,
                        # so the game must have a colony loaded for it to fire.
                        $trigger = Join-Path $env:TEMP 'oni_together_runtests'
                        if ($req.label) { Set-Content $trigger $req.label -Encoding UTF8 }
                        else { Set-Content $trigger '' -Encoding UTF8 }
                        $reply.result = @{ trigger = $trigger; categories = $req.label }
                    }
                    'scenario' {
                        # ScenarioRunner validates the verb set; this only drops
                        # the file. Label carries the command lines, ; separated.
                        if (-not $req.label) { throw 'scenario needs a command in -Label' }
                        $f = Join-Path $env:TEMP 'oni_together_cmd'
                        ($req.label -split ';') | ForEach-Object { $_.Trim() } |
                            Where-Object { $_ } | Set-Content $f -Encoding UTF8
                        $reply.result = @{ file = $f; commands = (Get-Content $f) }
                    }
                    'stop-oni'  {
                        $p = Get-OniProcess
                        if ($p) { $p | Stop-Process -Force; Start-Sleep -Seconds 3 }
                        $reply.result = @{ stopped = [bool]$p }
                    }
                    'start-oni' {
                        Start-Process "steam://rungameid/$AppId"
                        $reply.result = @{ launched = $true }
                    }
                    'quit'      { $reply.result = @{ bye = $true }; $running = $false }
                    default     { throw "unknown verb '$($req.verb)'" }
                }
                $reply.ok = $true
                Ok "<- $($req.verb)"
            } catch {
                $reply.ok = $false
                $reply.error = $_.Exception.Message
                Warn "<- failed: $($_.Exception.Message)"
            }
            $reply.completedUtc = (Get-Date).ToUniversalTime().ToString('o')
            $reply | ConvertTo-Json -Depth 6 |
                Set-Content (Join-Path $cmdDir "$id.done.json") -Encoding UTF8
            Remove-Item $r.FullName -Force -ErrorAction SilentlyContinue
        }
    } catch {
        Warn "poll error: $($_.Exception.Message)"
    }
    if ($running) { Start-Sleep -Seconds $IntervalSeconds }
}
Ok 'agent stopped'
