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
    [ValidateSet('ping', 'pull-mod', 'push-log', 'run-tests', 'scenario', 'stop-oni', 'start-oni', 'quit')]
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
$req | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $cmdDir "$id.req.json") -Encoding UTF8

$donePath = Join-Path $cmdDir "$id.done.json"
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
while ((Get-Date) -lt $deadline) {
    if (Test-Path $donePath) {
        # The agent may still be writing it; the file appears before it is closed.
        $reply = $null
        foreach ($attempt in 1..10) {
            try { $reply = Get-Content $donePath -Raw -ErrorAction Stop | ConvertFrom-Json; break }
            catch { Start-Sleep -Milliseconds 300 }
        }
        if (-not $reply) {
            Write-Host "[peer-cmd] FAIL reply file never became readable: $donePath" -ForegroundColor Red
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
Write-Host "[peer-cmd] FAIL no reply within ${TimeoutSeconds}s - is peer-agent.ps1 running on PC-B?" -ForegroundColor Red
exit 1
