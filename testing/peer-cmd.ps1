<#
.SYNOPSIS
    Host-side counterpart to peer-agent.ps1. Sends one verb to the client box
    and waits for its reply.

.DESCRIPTION
    peer-agent.ps1 runs on PC-B and polls <share>\cmd. This writes a request
    there and blocks until the matching .done.json appears, so the host side
    reads like a normal remote call even though the transport is a folder.

    Requires the agent to be running on PC-B. If it is not, this times out -
    that is the intended failure, not a silent no-op.

.EXAMPLE
    .\peer-cmd.ps1 -Verb ping
    .\peer-cmd.ps1 -Verb push-log -Label S1
    .\peer-cmd.ps1 -Verb stop-oni; .\peer-cmd.ps1 -Verb pull-mod; .\peer-cmd.ps1 -Verb start-oni
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('ping', 'pull-mod', 'push-log', 'why-exit', 'run-tests', 'scenario', 'stop-oni', 'start-oni', 'quit')]
    [string]$Verb,
    [string]$Label,
    [string]$Share = 'C:\ONI_MP_Share',
    [int]$TimeoutSeconds = 120
)

$ErrorActionPreference = 'Stop'
$cmdDir = Join-Path $Share 'cmd'
New-Item -ItemType Directory -Force -Path $cmdDir | Out-Null

# Ticks rather than a random id: sortable, and the agent processes oldest first.
$id  = "$Verb-$([DateTime]::UtcNow.ToString('yyyyMMddHHmmssfff'))"
$req = [ordered]@{ id = $id; verb = $Verb; label = $Label; issuedUtc = (Get-Date).ToUniversalTime().ToString('o') }
# Written aside and renamed, so the agent never picks up a half-written request.
# The agent polls for *.req.json and parses whatever it finds; a file that exists
# before it is complete parses as nothing and comes back as a spurious failure.
# A rename is atomic - the file either is not there or is whole.
$reqTemp  = Join-Path $cmdDir "$id.req.writing"
$reqFinal = Join-Path $cmdDir "$id.req.json"
$req | ConvertTo-Json -Depth 4 | Set-Content $reqTemp -Encoding UTF8
Move-Item -LiteralPath $reqTemp -Destination $reqFinal -Force

$donePath = Join-Path $cmdDir "$id.done.json"
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
while ((Get-Date) -lt $deadline) {
    if (Test-Path $donePath) {
        # The agent may still be writing it; the file appears before it is closed.
        #
        # This used to try ten times over three seconds and then declare permanent
        # failure, with minutes still left on the caller's own timeout. Over a
        # share, a file being written stays unreadable for longer than that, and
        # the cost of giving up early is not a retry - it is reporting a command
        # that succeeded as failed. A push-log did exactly that: the caller was
        # told it had failed, went on to collect, and found nothing, while the
        # agent finished writing client.log and client.meta.json seventeen seconds
        # later. Both files were there the whole time; the run was thrown away for
        # a read that stopped waiting.
        #
        # Shared read access as well, because exclusive access against a live
        # writer is the same fight in a different place.
        $reply = $null
        while ($null -eq $reply -and (Get-Date) -lt $deadline) {
            try {
                $fs = [System.IO.File]::Open($donePath, [System.IO.FileMode]::Open,
                        [System.IO.FileAccess]::Read,
                        [System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete)
                try {
                    $sr = New-Object System.IO.StreamReader($fs)
                    try { $text = $sr.ReadToEnd() } finally { $sr.Dispose() }
                } finally { $fs.Dispose() }

                # A truncated read parses as nothing, which is indistinguishable
                # from a write still in progress - so treat it as "not yet".
                if ($text) { $reply = $text | ConvertFrom-Json }
            } catch {
                $reply = $null
            }
            if ($null -eq $reply) { Start-Sleep -Milliseconds 400 }
        }
        if (-not $reply) {
            Write-Host "[peer-cmd] FAIL reply never became readable within ${TimeoutSeconds}s: $donePath" -ForegroundColor Red
            exit 1
        }
        Remove-Item $donePath -Force -ErrorAction SilentlyContinue
        $reply | ConvertTo-Json -Depth 6
        if (-not $reply.ok) { exit 1 }
        exit 0
    }
    Start-Sleep -Milliseconds 700
}

Remove-Item (Join-Path $cmdDir "$id.req.json") -Force -ErrorAction SilentlyContinue
# Report what was measured, then what it implies - in that order.
#
# This line used to read "is peer-agent.ps1 running on PC-B?" and nothing else. That
# is a guess, it was quoted back as a finding at least once while the agent was alive
# and merely busy, and it also happens to be true sometimes. The agent now writes a
# heartbeat every poll, so the two cases can be told apart instead of guessed at.
$beat = Join-Path $cmdDir 'agent-heartbeat.json'
$diagnosis = 'no heartbeat file at all - the agent has never run against this share'
if (Test-Path $beat) {
    $age = [int]((Get-Date) - (Get-Item $beat).LastWriteTime).TotalSeconds
    if ($age -le ($TimeoutSeconds + 30)) {
        $diagnosis = "the agent wrote its heartbeat ${age}s ago, so it is alive and did not " +
                     "answer - it is busy, or this verb failed inside it (check its console)"
    } else {
        $diagnosis = "its last heartbeat is ${age}s old, so the agent stopped - restart it on PC-B"
    }
}

Write-Host "[peer-cmd] FAIL no reply within ${TimeoutSeconds}s" -ForegroundColor Red
Write-Host "[peer-cmd]      $diagnosis" -ForegroundColor Red
exit 1
