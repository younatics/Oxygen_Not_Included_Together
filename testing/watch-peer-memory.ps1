<#
.SYNOPSIS
    Samples the client's process state on a timer and writes one row per sample.

.DESCRIPTION
    The client has stopped mid-session twice with the process still listed, and
    exited cleanly six or seven times with no exception, no Windows error and no
    crash dump. A clean exit with no complaint is what a Unity game does when it
    runs out of address space, and nothing in this harness has ever looked at the
    number over time - only single readings, which cannot show a trend.

    This runs alongside a scenario and records memory, the log's age, and whether
    the process is still answering. A log that stops while memory keeps climbing
    is a different bug from one that stops while memory is flat.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Label,
    [int]$IntervalSeconds = 15,
    [int]$Minutes = 8,
    [string]$Share = 'C:\ONI_MP_Share'
)

$ErrorActionPreference = 'Continue'
$out = Join-Path $PSScriptRoot "runs\$Label-peer-memory.tsv"
New-Item -ItemType Directory -Force -Path (Split-Path $out) | Out-Null
"utc`trunning`tworkingMB`tprivateMB`tfreeRamMB`tlogAgeSec`tresponding`tcpuSec" | Set-Content $out -Encoding UTF8

$deadline = (Get-Date).AddMinutes($Minutes)
while ((Get-Date) -lt $deadline) {
    $row = $null
    try {
        $p = & (Join-Path $PSScriptRoot 'peer-cmd.ps1') -Verb ping -Share $Share -TimeoutSeconds 45 | ConvertFrom-Json
        $r = $p.result
        $age = if ($null -ne $r.playerLogAgeSec) { $r.playerLogAgeSec }
               elseif ($r.playerLogUtc) { [math]::Round(((Get-Date).ToUniversalTime() - [datetime]$r.playerLogUtc).TotalSeconds, 0) }
               else { '' }
        $row = "{0}`t{1}`t{2}`t{3}`t{4}`t{5}`t{6}`t{7}" -f `
            (Get-Date).ToUniversalTime().ToString('HH:mm:ss'),
            $r.oniRunning, $r.workingSetMB, $r.privateMB, $r.freeRamMB, $age,
            $(if ($null -ne $r.responding) { $r.responding } else { '?' }),
            $(if ($null -ne $r.cpuSeconds) { $r.cpuSeconds } else { '?' })
    } catch {
        # The agent not answering is itself a data point, and a silent gap in the
        # table would read as a healthy sample.
        $row = "{0}`tAGENT-NO-REPLY`t`t`t`t`t`t" -f (Get-Date).ToUniversalTime().ToString('HH:mm:ss')
    }
    Add-Content $out $row -Encoding UTF8
    Write-Host $row
    Start-Sleep -Seconds $IntervalSeconds
}

Write-Host "wrote $out"
