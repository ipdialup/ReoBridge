#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Installs (or updates) ReoBridge as a Windows service.
.DESCRIPTION
  Copies the published files to C:\Program Files\ReoBridge, keeps the config in
  C:\ProgramData\ReoBridge\reobridge.json (created from the example if missing),
  opens the RTSP and status ports in Windows Firewall and starts the service.
.PARAMETER Source
  Folder containing the published ReoBridge.exe (dotnet publish output).
#>
param(
    [Parameter(Mandatory)] [string] $Source,
    [string] $InstallDir = "$env:ProgramFiles\ReoBridge",
    [string] $DataDir = "$env:ProgramData\ReoBridge"
)
$ErrorActionPreference = 'Stop'

$svc = Get-Service ReoBridge -ErrorAction SilentlyContinue
if ($svc -and $svc.Status -ne 'Stopped') { Stop-Service ReoBridge; $svc.WaitForStatus('Stopped', '00:00:20') }

New-Item -ItemType Directory -Force $InstallDir, $DataDir | Out-Null
Copy-Item "$Source\*" $InstallDir -Recurse -Force

$config = Join-Path $DataDir 'reobridge.json'
if (-not (Test-Path $config)) {
    Copy-Item (Join-Path $PSScriptRoot '..\reobridge.example.json') $config
    Write-Warning "Created $config from the example - edit it with your cameras, then run this script again."
}
# The config holds passwords: only Administrators and SYSTEM may read it.
icacls $config /inheritance:r /grant:r 'Administrators:F' 'SYSTEM:F' | Out-Null

if (-not $svc) {
    New-Service -Name ReoBridge -DisplayName 'ReoBridge' -BinaryPathName "`"$InstallDir\ReoBridge.exe`"" `
        -Description 'Serves Reolink cameras as standard RTSP streams' -StartupType Automatic | Out-Null
    sc.exe failure ReoBridge reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null
}

$cfg = Get-Content $config -Raw | ConvertFrom-Json
$rtspPort = if ($cfg.ReoBridge.Rtsp.Port) { [int]$cfg.ReoBridge.Rtsp.Port } else { 8554 }
$statusPort = if ($null -ne $cfg.ReoBridge.StatusPort) { [int]$cfg.ReoBridge.StatusPort } else { 8555 }
$ports = @($rtspPort)
if ($statusPort -gt 0) { $ports += $statusPort }
foreach ($cam in @($cfg.ReoBridge.Cameras)) { if ($cam.OnvifPort -gt 0) { $ports += [int]$cam.OnvifPort } }
Get-NetFirewallRule -DisplayName 'ReoBridge*' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
New-NetFirewallRule -DisplayName 'ReoBridge (TCP)' -Direction Inbound -Protocol TCP -LocalPort $ports -Action Allow | Out-Null
New-NetFirewallRule -DisplayName 'ReoBridge (RTP over UDP)' -Direction Inbound -Program "$InstallDir\ReoBridge.exe" -Protocol UDP -Action Allow | Out-Null

Start-Service ReoBridge
Write-Host "ReoBridge running. Status: http://localhost:$statusPort/  Logs: $DataDir\logs"
