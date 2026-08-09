<#
.SYNOPSIS
    Run this ON THE CLIENT BOX (PC-B). Pulls the mod from a share on the host
    box and pushes this box's Player.log back to it.

.DESCRIPTION
    deploy-to-peer.ps1 / fetch-peer-logs.ps1 drive everything from the host box
    and need either WinRM or a path the host can write to. When the client's
    credentials are unusable (e.g. a passwordless Microsoft account has no hash
    for network auth), neither works and the direction has to flip: the client
    reaches out to the host instead.

    Rule 2 is unchanged - this never builds anything. It copies the host's
    binary and fails loudly if the sha256 does not match, so the pair stays
    byte-identical.

    A passwordless Microsoft account has no NTLM hash, so it cannot authenticate
    over SMB at all - PIN/Hello is local-only. Both accounts here are MSA-backed,
    so the share needs a real local account with a password on the host box, and
    the share must live outside a user profile or NTFS will deny it.

    Host-side setup (PC-A), once, elevated:
        $share = 'C:\ONI_MP_Share'
        New-Item -ItemType Directory -Force -Path "$share\mod","$share\drop"
        $pw = Read-Host -AsSecureString 'password for onishare'
        New-LocalUser -Name onishare -Password $pw -PasswordNeverExpires -AccountNeverExpires
        Add-LocalGroupMember -SID 'S-1-5-32-545' -Member onishare
        New-SmbShare -Name onimp -Path $share -ChangeAccess "$env:COMPUTERNAME\onishare" `
                     -FullAccess "$env:COMPUTERNAME\$env:USERNAME"
        icacls $share /grant "onishare:(OI)(CI)M"

    Host-side, after every build (this is the existing script, Path mode):
        .\deploy-to-peer.ps1 -PeerPath "$share\mod"

.EXAMPLE
    # after the host published a fresh build
    .\peer-sync.ps1 -Share \\KYLE\onimp -Mode pull

.EXAMPLE
    # after running a scenario
    .\peer-sync.ps1 -Share \\KYLE\onimp -Mode push -Label S1
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Share,
    [ValidateSet('pull', 'push')][string]$Mode = 'pull',
    [string]$Label
)

$ErrorActionPreference = 'Stop'
function Info($m) { Write-Host "[peer-sync] $m" -ForegroundColor Cyan }
function Ok($m)   { Write-Host "[peer-sync] OK   $m" -ForegroundColor Green }
function Warn($m) { Write-Host "[peer-sync] WARN $m" -ForegroundColor Yellow }
function Die($m)  { Write-Host "[peer-sync] FAIL $m" -ForegroundColor Red; exit 1 }

if (-not (Test-Path $Share)) {
    Die @"
cannot reach $Share
Authenticate to the host box first, as its LOCAL share account - a passwordless
Microsoft account cannot authenticate over SMB:
  net use $Share /user:<PC-A컴퓨터명>\onishare
See the .DESCRIPTION above for the one-time host-side setup.
Note: on Windows 11 the inbound SMB rule may already be enabled even on a Public
profile ('File and Printer Sharing (Restrictive) (SMB-In)') - check before
changing firewall rules. The localized DisplayGroup will not match the English
name; use -Group '@FirewallAPI.dll,-28502' if a rule really has to be enabled.
"@
}

# Same folder both scripts use. OneDrive redirection is handled by GetFolderPath.
$localDev = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Klei\OxygenNotIncluded\mods\dev'

if ($Mode -eq 'pull') {
    $srcMod = Join-Path $Share 'mod\ONI_Together_dev'
    $srcDll = Join-Path $srcMod 'ONI_Together.dll'
    if (-not (Test-Path $srcDll)) {
        Die "no published build at $srcDll - on PC-A run: .\deploy-to-peer.ps1 -PeerPath <share>\mod"
    }
    $srcHash = (Get-FileHash $srcDll -Algorithm SHA256).Hash
    Info "source: $srcMod"
    Info "sha256: $srcHash"

    New-Item -ItemType Directory -Force -Path $localDev | Out-Null
    $dstMod = Join-Path $localDev 'ONI_Together_dev'
    # Wipe first so files deleted upstream do not linger and get loaded.
    if (Test-Path $dstMod) { Remove-Item $dstMod -Recurse -Force }
    Copy-Item -Path $srcMod -Destination $localDev -Recurse -Force

    $dstDll = Join-Path $dstMod 'ONI_Together.dll'
    if (-not (Test-Path $dstDll)) { Die "copy did not land at $dstDll" }
    $dstHash = (Get-FileHash $dstDll -Algorithm SHA256).Hash
    if ($dstHash -ne $srcHash) { Die "hash mismatch after copy (host $srcHash vs local $dstHash)" }

    Ok "mod at $dstMod (hash verified)"
    Write-Host ''
    Write-Host '  Restart ONI on this box - mods load at startup.' -ForegroundColor Yellow
    Write-Host '  Mods menu: dev version ENABLED, Workshop version DISABLED.' -ForegroundColor Yellow
    exit 0
}

# --- push -------------------------------------------------------------------
if (-not $Label) { Die '-Label is required for -Mode push (use the scenario name, e.g. S1)' }

$player = Join-Path $env:USERPROFILE 'AppData\LocalLow\Klei\Oxygen Not Included\Player.log'
if (-not (Test-Path $player)) { Die "no Player.log at $player - has ONI been launched on this box?" }

$dest = Join-Path (Join-Path $Share 'drop') $Label
New-Item -ItemType Directory -Force -Path $dest | Out-Null

# ONI holds the handle; copy locally first, then ship the copy.
$tmp = Join-Path $env:TEMP "oni-together-client-$PID.log"
Copy-Item -Path $player -Destination $tmp -Force
Copy-Item -Path $tmp -Destination (Join-Path $dest 'client.log') -Force

$size     = [math]::Round((Get-Item $tmp).Length / 1MB, 2)
$modLines = (Select-String -Path $tmp -Pattern '\[ONI_Together\]' -AllMatches | Measure-Object).Count

$meta = [ordered]@{
    role         = 'client'
    label        = $Label
    collectedUtc = (Get-Date).ToUniversalTime().ToString('o')
    machine      = $env:COMPUTERNAME
    lanIPv4      = @((Get-NetIPAddress -AddressFamily IPv4 |
                      Where-Object { $_.IPAddress -notlike '127.*' -and
                                     $_.IPAddress -notlike '169.254.*' }).IPAddress)
    playerLogMB  = $size
    modLogLines  = $modLines
}
$dll = Join-Path $localDev 'ONI_Together_dev\ONI_Together.dll'
if (Test-Path $dll) {
    $f = Get-Item $dll
    $meta.modDllUtc    = $f.LastWriteTimeUtc.ToString('o')
    $meta.modDllBytes  = $f.Length
    $meta.modDllSha256 = (Get-FileHash $f.FullName -Algorithm SHA256).Hash
}
$meta | ConvertTo-Json -Depth 4 | Set-Content -Path (Join-Path $dest 'client.meta.json') -Encoding UTF8
Remove-Item $tmp -Force -ErrorAction SilentlyContinue

Ok "$dest\client.log  (${size} MB, $modLines mod lines)"
if ($modLines -eq 0) { Warn 'zero [ONI_Together] lines - the mod did not load on this box. Check the Mods menu.' }
Ok 'client.meta.json written (mod dll hash recorded - both boxes must match)'
