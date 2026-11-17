# Builds OrbitDriveShell.dll (the Explorer command) and ShellExtensionTest.exe with the MSVC toolset found by vswhere.
# Usage: pwsh build.ps1 [-Out <folder>]
param([string]$Out = (Join-Path $PSScriptRoot 'bin'))
$ErrorActionPreference = 'Stop'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'MSVC (Desktop development with C++) is not installed.' }
$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'
New-Item -ItemType Directory -Force $Out | Out-Null
$common = '/nologo /std:c++17 /EHsc /W4 /WX /O2 /GS /guard:cf /DUNICODE /D_UNICODE'
$dll = "cl $common /LD `"$PSScriptRoot\OrbitDriveCommand.cpp`" /Fo`"$Out\\`" /Fe`"$Out\OrbitDriveShell.dll`" /link /DEF:`"$PSScriptRoot\OrbitDriveShell.def`" /DYNAMICBASE /NXCOMPAT /guard:cf ole32.lib shell32.lib shlwapi.lib uuid.lib"
$test = "cl $common `"$PSScriptRoot\ShellExtensionTest.cpp`" /Fo`"$Out\\`" /Fe`"$Out\ShellExtensionTest.exe`" /link ole32.lib shell32.lib uuid.lib"
foreach ($command in @($dll, $test)) {
  cmd /c "`"$vcvars`" >nul && $command"
  if ($LASTEXITCODE -ne 0) { throw "build failed: $command" }
}
Get-ChildItem (Join-Path $Out '*') -Include *.dll,*.exe | Select-Object Name, Length | Format-Table
