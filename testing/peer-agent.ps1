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

# Why the game is not running any more, asked of Windows rather than of the
# game. A client that logs Game.OnApplicationQuit() and then a clean disconnect
# left through an orderly shutdown path - no exception, no truncated log - and
# nothing inside the game can distinguish "the user closed it", "Steam closed
# it", "it ran out of memory" and "it hit an unhandled native fault". The OS
# recorded all four differently.
function Invoke-WhyExit {
    $since = (Get-Date).AddHours(-6)
    $events = @()
    foreach ($logName in @('Application', 'System')) {
        try {
            $events += Get-WinEvent -FilterHashtable @{
                LogName   = $logName
                StartTime = $since
                Level     = 1, 2, 3    # critical, error, warning
            } -MaxEvents 400 -ErrorAction Stop |
            Where-Object {
                $_.Message -match 'Oxygen|OxygenNotIncluded|Unity|steam' -or
                $_.ProviderName -match 'Application Error|Windows Error Reporting|Application Hang|Kernel-Power|Resource-Exhaustion'
            } |
            Select-Object -First 25 |
            ForEach-Object {
                [ordered]@{
                    log      = $logName
                    time     = $_.TimeCreated.ToUniversalTime().ToString('o')
                    level    = $_.LevelDisplayName
                    provider = $_.ProviderName
                    id       = $_.Id
                    # Trimmed: full WER payloads run to kilobytes and the useful
                    # part - faulting module, exception code - is at the front.
                    message  = ($_.Message -replace '\s+', ' ').Substring(0, [Math]::Min(400, ($_.Message -replace '\s+', ' ').Length))
                }
            }
        } catch { }
    }

    # Klei drops crash dumps and its own error reports next to the log.
    $logDir = Split-Path $playerLog
    $recent = @()
    if (Test-Path $logDir) {
        $recent = Get-ChildItem $logDir -File -ErrorAction SilentlyContinue |
                  Where-Object { $_.LastWriteTime -gt $since } |
                  Sort-Object LastWriteTime -Descending |
                  Select-Object -First 15 |
                  ForEach-Object { [ordered]@{ name = $_.Name; kb = [math]::Round($_.Length / 1KB, 1); utc = $_.LastWriteTimeUtc.ToString('o') } }
    }

    $p = Get-OniProcess
    @{
        oniRunning  = [bool]$p
        events      = $events
        logDirFiles = $recent
        # A restart resets this, so it says how long the current run has lasted -
        # a session that quits at the same age every time is a different story
        # from one that quits at random.
        oniStartedUtc = if ($p) { $p.StartTime.ToUniversalTime().ToString('o') } else { $null }
    }
}

function Invoke-PushLog($label) {
    if (-not $label) { throw 'push-log needs a label' }
    if (-not (Test-Path $playerLog)) { throw "no Player.log at $playerLog" }
    $dest = Join-Path $dropDir $label

    # Empty the label first. Reusing a label left the previous run's files in
    # place, and the reader has no way to tell them apart from this run's - it
    # picked up a client.meta.json from two days earlier and reported the two
    # boxes as running different binaries. They were not. A stale file that
    # parses is worse than a truncated one, because nothing about it looks wrong.
    if (Test-Path $dest) { Remove-Item (Join-Path $dest '*') -Force -Recurse -ErrorAction SilentlyContinue }
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
    # Written aside and renamed, and written LAST, so its presence means the whole
    # snapshot is there. Set-Content straight to the final name leaves a window in
    # which the file exists and is incomplete - and the reader on the other side of
    # a share cannot distinguish that from a finished file. A rename is atomic.
    $metaFinal = Join-Path $dest 'client.meta.json'
    $metaTemp  = Join-Path $dest 'client.meta.writing'
    $meta | ConvertTo-Json -Depth 4 | Set-Content $metaTemp -Encoding UTF8
    Move-Item -LiteralPath $metaTemp -Destination $metaFinal -Force
    Remove-Item $tmp -Force -ErrorAction SilentlyContinue
    @{ playerLogMB = $size; modLogLines = $modLines; dest = $dest }
}

function Get-OniProcess { Get-Process -Name 'OxygenNotIncluded' -ErrorAction SilentlyContinue }

Info "share   : $Share"
Info "mod     : $localMod"
Info "sha256  : $(Get-ModHash)"
Info "polling every ${IntervalSeconds}s - Ctrl-C to stop"

# Ask Windows not to sleep while this is polling.
#
# The agent has stopped answering twice with no error in its own log, both times after
# a stretch with ONI closed and nothing else running on that box. An idle Windows
# machine suspends, and a suspended agent looks exactly like a closed one from here -
# which is why the heartbeat above exists.
#
# Not proof. It is the most common cause of this shape and it costs nothing to remove:
# ES_CONTINUOUS with ES_SYSTEM_REQUIRED holds only for this process and lapses the
# moment it exits, so no power setting on that machine is changed and nothing has to be
# put back. If the agent stops again anyway, the heartbeat will now say whether it was
# alive at the time, and that narrows it properly.
try {
    Add-Type -Namespace Win32 -Name Power -MemberDefinition @'
[DllImport("kernel32.dll", SetLastError = true)]
public static extern uint SetThreadExecutionState(uint esFlags);
'@ -ErrorAction Stop
    # ES_CONTINUOUS (0x80000000) | ES_SYSTEM_REQUIRED (0x00000001)
    [void][Win32.Power]::SetThreadExecutionState(0x80000001)
    Info 'asked Windows to stay awake while this agent runs'
} catch {
    Warn "could not request stay-awake: $($_.Exception.Message) - the box may sleep and the agent will look closed"
}

$running = $true
# Ids already dispatched this agent lifetime. Belt to the .taken rename's
# braces: a request must run at most once even if the file reappears.
$seen = New-Object 'System.Collections.Generic.HashSet[string]'

# A heartbeat, so a timeout on the host side stops being a guess.
#
# peer-cmd prints "is peer-agent.ps1 running on PC-B?" when a request goes
# unanswered, and that sentence is a guess - the only thing measured is the
# timeout. It has already been read as fact once and the agent was alive, just
# busy. It has also been true. Those are different problems with different fixes
# and the host had no way to tell them apart.
#
# Written before the requests are read and again after, so the file's timestamp
# says "this process was looping N seconds ago". If the share is unreachable the
# write fails, the outer catch logs it, and the absence of a fresh heartbeat is
# then itself the measurement.
$heartbeat = Join-Path $cmdDir 'agent-heartbeat.json'
function Beat($phase) {
    try {
        @{
            machine = $env:COMPUTERNAME
            pid     = $PID
            phase   = $phase
            utc     = (Get-Date).ToUniversalTime().ToString('o')
        } | ConvertTo-Json -Compress | Set-Content $heartbeat -Encoding UTF8
    } catch { }
}

while ($running) {
    Beat 'polling'
    try {
        $reqs = Get-ChildItem $cmdDir -Filter '*.req.json' -ErrorAction SilentlyContinue |
                Sort-Object LastWriteTime
        foreach ($r in $reqs) {
            $id = $r.BaseName -replace '\.req$', ''

            # Claim the request before running it, and skip anything already
            # claimed. This used to delete the file afterwards with
            # -ErrorAction SilentlyContinue, which turns dispatch into
            # at-least-once: when the delete lost (share held the handle, the
            # writer still had it open), the next poll found the same
            # *.req.json and ran the verb again. None of these verbs are
            # idempotent. It cost a whole run - join-lan executed twice, one
            # second apart, and the second join reset the client's state while
            # a 2.2 MB save transfer was in flight, so the client never loaded
            # the world and the run died at "peer never reached an in-session
            # state" with nothing wrong with the session. start-oni and
            # stop-oni twice are worse.
            #
            # Renaming is atomic and fails loudly rather than silently, so a
            # request that cannot be claimed is left for the next poll instead
            # of being executed on a guess.
            if ($seen.Contains($id)) { Remove-Item $r.FullName -Force -ErrorAction SilentlyContinue; continue }
            $taken = Join-Path $cmdDir "$id.taken"
            try { Move-Item -LiteralPath $r.FullName -Destination $taken -Force -ErrorAction Stop }
            catch { Warn "could not claim $id, leaving it: $($_.Exception.Message)"; continue }
            $seen.Add($id) | Out-Null

            $reply = [ordered]@{ id = $id; machine = $env:COMPUTERNAME }
            try {
                $req = Get-Content $taken -Raw | ConvertFrom-Json
                Info "-> $($req.verb) $($req.label)"
                switch ($req.verb) {
                    'ping' {
                        $p = Get-OniProcess
                        $reply.result = @{
                            modSha256 = Get-ModHash
                            oniRunning = [bool]$p
                            oniStartedUtc = if ($p) { $p.StartTime.ToUniversalTime().ToString('o') } else { $null }
                            playerLogUtc = if (Test-Path $playerLog) { (Get-Item $playerLog).LastWriteTimeUtc.ToString('o') } else { $null }
                            # Six clean exits with no exception, no Windows error
                            # and no crash dump all point the same way, and there
                            # was no memory figure anywhere to confirm or kill the
                            # idea. Cheap to carry on every ping, and the shape
                            # over a session is the whole answer.
                            workingSetMB = if ($p) { [math]::Round($p.WorkingSet64 / 1MB, 0) } else { $null }
                            privateMB    = if ($p) { [math]::Round($p.PrivateMemorySize64 / 1MB, 0) } else { $null }
                            freeRamMB    = [math]::Round((Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory / 1KB, 0)

                            # Is it alive, or only running?
                            #
                            # Twice the client's log stopped a minute into a
                            # session with the process still listed and no
                            # exception, no shutdown sequence and no Windows
                            # event - and Unity's own lines stopped alongside the
                            # mod's, so the game loop stopped, not just logging.
                            # "oniRunning" cannot tell a frozen game from a
                            # healthy one; Responding asks the window whether it
                            # is still pumping messages, and CPU time separates a
                            # deadlock from a busy loop.
                            responding      = if ($p) { [bool]$p.Responding } else { $null }
                            threads         = if ($p) { $p.Threads.Count } else { $null }
                            cpuSeconds      = if ($p) { [math]::Round($p.TotalProcessorTime.TotalSeconds, 1) } else { $null }
                            playerLogAgeSec = if (Test-Path $playerLog) {
                                                  [math]::Round(((Get-Date).ToUniversalTime() - (Get-Item $playerLog).LastWriteTimeUtc).TotalSeconds, 0)
                                              } else { $null }
                            gpuDriverCrash  = (Get-WinEvent -LogName System -MaxEvents 200 -ErrorAction SilentlyContinue |
                                               Where-Object { $_.Id -eq 4101 -or $_.ProviderName -match 'Display' } |
                                               Select-Object -First 1 -ExpandProperty TimeCreated)
                        }
                    }
                    'pull-mod'  { $reply.result = Invoke-PullMod }
                    'push-log'  { $reply.result = Invoke-PushLog $req.label }
                    'why-exit'  { $reply.result = Invoke-WhyExit }
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

            # Written to one side and renamed into place, so the host never sees a
            # half-written reply.
            #
            # Set-Content straight to the final name means the file exists before it
            # is complete, and the reader on the other end of a share cannot tell
            # "still being written" from "corrupt". The reader was giving up on that
            # and reporting successful commands as failures. A rename is atomic, so
            # the file either is not there or is whole.
            $finalPath = Join-Path $cmdDir "$id.done.json"
            $tempPath  = Join-Path $cmdDir "$id.done.writing"
            $reply | ConvertTo-Json -Depth 6 | Set-Content $tempPath -Encoding UTF8
            Move-Item -LiteralPath $tempPath -Destination $finalPath -Force
            # Already claimed above; if this delete loses, the .taken name means
            # no poll will pick it up again.
            Remove-Item $taken -Force -ErrorAction SilentlyContinue
        }
    } catch {
        Warn "poll error: $($_.Exception.Message)"
    }
    Beat 'idle'
    if ($running) { Start-Sleep -Seconds $IntervalSeconds }
}
Ok 'agent stopped'
