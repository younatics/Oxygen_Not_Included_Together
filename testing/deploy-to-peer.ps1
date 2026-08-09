<#
.SYNOPSIS
    Push the freshly built mod from this (host/dev) box to the client box.

.DESCRIPTION
    Both machines MUST run byte-identical mod binaries. A mismatched pair produces
    desyncs that look exactly like the bugs under investigation, and there is no
    version handshake that would catch a stale client build.

    Transport options, in order of preference:
      -Session   : an existing PSSession you created (most control)
      -PeerHost  : script opens a PSSession over WinRM (needs WinRM enabled on peer)
      -PeerPath  : plain UNC/mapped path, e.g. \\PC-B\Users\me\Documents\... or Z:\...

.EXAMPLE
    # WinRM (run once on the CLIENT, elevated:  Enable-PSRemoting -Force)
    .\deploy-to-peer.ps1 -PeerHost 192.168.0.42 -PeerUser PC-B\kyle

    # Shared folder / mapped drive - no WinRM needed
    .\deploy-to-peer.ps1 -PeerPath '\\PC-B\Documents\Klei\OxygenNotIncluded\mods\dev'
#>
[CmdletBinding(DefaultParameterSetName = 'WinRM')]
param(
    [Parameter(ParameterSetName = 'WinRM', Mandatory = $true)][string]$PeerHost,
    [Parameter(ParameterSetName = 'WinRM')][string]$PeerUser,
    [Parameter(ParameterSetName = 'Session', Mandatory = $true)]
    [System.Management.Automation.Runspaces.PSSession]$Session,
    [Parameter(ParameterSetName = 'Path', Mandatory = $true)][string]$PeerPath,
    [string]$PeerDocuments = 'C:\Users\$env:USERNAME\Documents'
)

$ErrorActionPreference = 'Stop'
function Ok($m)   { Write-Host "[deploy] OK   $m" -ForegroundColor Green }
function Info($m) { Write-Host "[deploy] $m" -ForegroundColor Cyan }
function Die($m)  { Write-Host "[deploy] FAIL $m" -ForegroundColor Red; exit 1 }

$srcRoot = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Klei\OxygenNotIncluded\mods\dev'
$src = Join-Path $srcRoot 'ONI_Together_dev'
if (-not (Test-Path (Join-Path $src 'ONI_Together.dll'))) {
    Die "no built mod at $src - run bootstrap.ps1 -Role host first"
}
$srcHash = (Get-FileHash (Join-Path $src 'ONI_Together.dll') -Algorithm SHA256).Hash
Info "source: $src"
Info "sha256: $srcHash"

switch ($PSCmdlet.ParameterSetName) {
    'Path' {
        New-Item -ItemType Directory -Force -Path $PeerPath | Out-Null
        Copy-Item -Path $src -Destination $PeerPath -Recurse -Force
        $remoteDll = Join-Path $PeerPath 'ONI_Together_dev\ONI_Together.dll'
        if (-not (Test-Path $remoteDll)) { Die "copy did not land at $remoteDll" }
        $dstHash = (Get-FileHash $remoteDll -Algorithm SHA256).Hash
        if ($dstHash -ne $srcHash) { Die "hash mismatch after copy (src $srcHash vs dst $dstHash)" }
        Ok "copied to $PeerPath (hash verified)"
    }
    default {
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
On this box, if the peer is not domain-joined:
  Set-Item WSMan:\localhost\Client\TrustedHosts -Value '$PeerHost' -Concatenate -Force
Or skip WinRM entirely and use -PeerPath with a shared folder.
"@
            }
        }
        try {
            $remoteDev = Invoke-Command -Session $sess -ScriptBlock {
                $d = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Klei\OxygenNotIncluded\mods\dev'
                New-Item -ItemType Directory -Force -Path $d | Out-Null
                $d
            }
            Info "peer dev folder: $remoteDev"

            # Remove the old copy first so deleted files do not linger.
            Invoke-Command -Session $sess -ArgumentList $remoteDev -ScriptBlock {
                param($d)
                $t = Join-Path $d 'ONI_Together_dev'
                if (Test-Path $t) { Remove-Item $t -Recurse -Force }
            }
            Copy-Item -Path $src -Destination $remoteDev -ToSession $sess -Recurse -Force

            $dstHash = Invoke-Command -Session $sess -ArgumentList $remoteDev -ScriptBlock {
                param($d)
                $f = Join-Path $d 'ONI_Together_dev\ONI_Together.dll'
                if (Test-Path $f) { (Get-FileHash $f -Algorithm SHA256).Hash } else { $null }
            }
            if (-not $dstHash) { Die 'mod dll missing on peer after copy' }
            if ($dstHash -ne $srcHash) { Die "hash mismatch (src $srcHash vs peer $dstHash)" }
            Ok "deployed to $PeerHost and hash-verified"
        } finally {
            if (-not $Session -and $sess) { Remove-PSSession $sess }
        }
    }
}

Write-Host ''
Ok 'both boxes now run the same binary.'
Write-Host '  Restart ONI on the client so the new DLL is loaded (mods load at startup).' -ForegroundColor Yellow
