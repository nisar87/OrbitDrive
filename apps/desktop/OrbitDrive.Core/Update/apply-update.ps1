# OrbitDrive portable update (embedded in the app, written to a temp folder and run by Windows PowerShell as the current user; never elevated).
# It never kills anything, never touches anything outside <InstallDir>, <InstallDir>.new and <InstallDir>.old, and leaves the old version in place on any failure.
param(
  [Parameter(Mandatory)][string]$InstallDir,
  [Parameter(Mandatory)][string]$Zip,
  [Parameter(Mandatory)][string]$Sha256,
  [Parameter(Mandatory)][string]$Version,
  [Parameter(Mandatory)][string]$ResultFile,
  [string]$StateDir = '',
  [int]$WaitSeconds = 60,
  [switch]$Relaunch
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$install = [IO.Path]::GetFullPath($InstallDir).TrimEnd('\')
$new = "$install.new"; $old = "$install.old"
function Done([bool]$ok, [string]$message) {
  $json = @{ ok = $ok; version = $Version; message = $message; at = (Get-Date).ToUniversalTime().ToString('o') } | ConvertTo-Json -Compress
  [IO.File]::WriteAllText($ResultFile, $json)
}
try {
  if (-not (Test-Path -LiteralPath $Zip -PathType Leaf)) { throw 'The downloaded update is missing.' }
  if ((Get-FileHash -LiteralPath $Zip -Algorithm SHA256).Hash.ToLowerInvariant() -ne $Sha256.ToLowerInvariant()) { throw 'The downloaded update does not match its checksum; nothing was changed.' }
  if (-not (Test-Path -LiteralPath (Join-Path $install 'OrbitDrive.exe'))) { throw 'This folder does not look like an OrbitDrive installation; nothing was changed.' }

  # Wait for OrbitDrive to finish quitting (never forced): every program that runs from the install folder must be gone.
  $deadline = (Get-Date).AddSeconds($WaitSeconds)
  do {
    $running = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { try { $_.Path -and $_.Path.StartsWith($install + '\', [StringComparison]::OrdinalIgnoreCase) } catch { $false } })
    if ($running.Count -eq 0) { break }
    Start-Sleep -Milliseconds 500
  } while ((Get-Date) -lt $deadline)
  if ($running.Count -gt 0) { throw 'OrbitDrive was still running, so the update was not applied. Quit OrbitDrive and try again.' }

  # Unpack beside the install folder and check every entry stays inside it (a hostile archive cannot write elsewhere).
  foreach ($leftover in @($new, $old)) { if (Test-Path -LiteralPath $leftover) { Remove-Item -LiteralPath $leftover -Recurse -Force } }
  New-Item -ItemType Directory -Path $new | Out-Null
  $root = [IO.Path]::GetFullPath($new).TrimEnd('\') + '\'
  $archive = [IO.Compression.ZipFile]::OpenRead($Zip)
  try {
    foreach ($entry in $archive.Entries) {
      $target = [IO.Path]::GetFullPath((Join-Path $new $entry.FullName))
      if (-not $target.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { throw "Unsafe path in the update: $($entry.FullName)" }
      if ($entry.FullName.EndsWith('/')) { New-Item -ItemType Directory -Force -Path $target | Out-Null; continue }
      New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
      [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
    }
  } finally { $archive.Dispose() }
  $payload = Join-Path $new 'OrbitDrive'
  if (-not (Test-Path -LiteralPath (Join-Path $payload 'OrbitDrive.exe'))) { throw 'The update does not contain OrbitDrive.exe.' }
  if (-not (Test-Path -LiteralPath (Join-Path $payload 'orbitdrive-agent.exe'))) { throw 'The update does not contain the OrbitDrive agent.' }

  # Swap: old -> .old, new -> install. If the second step fails the old version is put back.
  $moved = $false
  for ($i = 0; $i -lt 20 -and -not $moved; $i++) {
    try { Move-Item -LiteralPath $install -Destination $old -ErrorAction Stop; $moved = $true } catch { Start-Sleep -Milliseconds 500 }
  }
  if (-not $moved) { throw 'Windows would not let the OrbitDrive folder be replaced (is it open in a window or scanned by another program?). Nothing was changed.' }
  try { Move-Item -LiteralPath $payload -Destination $install -ErrorAction Stop }
  catch { Move-Item -LiteralPath $old -Destination $install -ErrorAction SilentlyContinue; throw "Could not put the new version in place; the old version was restored. $($_.Exception.Message)" }
  Remove-Item -LiteralPath $new -Recurse -Force -ErrorAction SilentlyContinue
  Remove-Item -LiteralPath $old -Recurse -Force -ErrorAction SilentlyContinue
  Done $true "Updated to $Version."
  if ($Relaunch) {
    $args = @(); if ($StateDir) { $args = @('--state', $StateDir) }
    Start-Process -FilePath (Join-Path $install 'OrbitDrive.exe') -ArgumentList $args -WorkingDirectory $install | Out-Null
  }
} catch {
  Done $false $_.Exception.Message
  if ($Relaunch -and (Test-Path -LiteralPath (Join-Path $install 'OrbitDrive.exe'))) {
    $args = @(); if ($StateDir) { $args = @('--state', $StateDir) }
    Start-Process -FilePath (Join-Path $install 'OrbitDrive.exe') -ArgumentList $args -WorkingDirectory $install | Out-Null   # the old version keeps working
  }
  exit 1
}
