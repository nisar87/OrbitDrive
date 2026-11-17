# Registers the sparse package for DEVELOPMENT on a Windows 11 PC so the modern-menu command can be tried by hand.
# Needs: Developer Mode on, the built OrbitDriveShell.dll and OrbitDrive.exe in -AppFolder, and the placeholder images in Assets\.
# Unsigned loose registration is only for development machines. Remove it with:  Get-AppxPackage OrbitDrive.Desktop.Dev | Remove-AppxPackage
param([Parameter(Mandatory)][string]$AppFolder)
$ErrorActionPreference = 'Stop'
foreach ($needed in 'OrbitDriveShell.dll', 'OrbitDrive.exe') { if (-not (Test-Path (Join-Path $AppFolder $needed))) { throw "$needed is missing from $AppFolder" } }
$manifest = Join-Path $PSScriptRoot 'AppxManifest.xml'
Add-AppxPackage -Register $manifest -ExternalLocation (Resolve-Path $AppFolder)
Write-Host 'Registered. Restart Explorer (or sign out and in), then right-click a file: "Send to my phone via OrbitDrive".'
