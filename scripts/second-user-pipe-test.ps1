# Security test on real Windows: the agent's local control pipe must accept its own user and refuse a DIFFERENT local Windows user.
# A throwaway local account is created and removed. Windows PowerShell 5.1, CI runner (administrator). Any failed expectation throws.
param([Parameter(Mandatory = $true)][string]$AgentExe)
$ErrorActionPreference = 'Stop'
$AgentExe = (Resolve-Path $AgentExe).Path
$shared = 'C:\orbit-pipe-test'
$state = Join-Path $env:RUNNER_TEMP ('orbit-pipe-state-' + [guid]::NewGuid().ToString('N'))
$user = 'orbitother' + (Get-Random -Maximum 9999)
$agent = $null; $userCreated = $false
function Check([bool]$condition, [string]$what) { if (-not $condition) { throw "FAILED: $what" }; Write-Host "ok  $what" }
try {
  New-Item -ItemType Directory -Force -Path $shared, $state | Out-Null
  icacls $shared /grant 'Everyone:(OI)(CI)F' | Out-Null
  # The probe: connect to the pipe and write the outcome to a file.
  Set-Content -Path (Join-Path $shared 'probe.ps1') -Value @'
param([string]$Pipe, [string]$Out)
try {
  $c = New-Object System.IO.Pipes.NamedPipeClientStream('.', $Pipe, [System.IO.Pipes.PipeDirection]::InOut)
  $c.Connect(8000)
  Set-Content -Path $Out -Value 'CONNECTED'
  $c.Dispose()
} catch {
  $e = $_.Exception; if ($e.InnerException) { $e = $e.InnerException }
  Set-Content -Path $Out -Value ('FAILED ' + $e.GetType().Name + ': ' + $e.Message)
}
'@

  $agent = Start-Process -FilePath $AgentExe -ArgumentList @('run', '--state', "`"$state`"") -PassThru -WindowStyle Hidden
  $pipe = $null
  for ($i = 0; $i -lt 60 -and -not $pipe; $i++) {
    Start-Sleep -Milliseconds 500
    $pipe = Get-ChildItem '\\.\pipe\' | Where-Object { $_.Name -like 'orbitdrive-agent-*' } | Select-Object -First 1 -ExpandProperty Name
  }
  Check ([bool]$pipe) 'the agent opened its control pipe'

  # 1. The owner (this runner account) can connect: proves the probe and the pipe name are right.
  $ownerOut = Join-Path $shared 'owner.txt'
  & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $shared 'probe.ps1') -Pipe $pipe -Out $ownerOut | Out-Null
  $owner = (Get-Content $ownerOut -Raw).Trim()
  Check ($owner -eq 'CONNECTED') "the agent's own user can connect ($owner)"

  # 2. A different local user cannot.
  # 14 characters or fewer, or `net user` stops to ask a question; upper, lower, digit and symbol satisfy the complexity rule.
  $password = 'Zq9!' + [guid]::NewGuid().ToString('N').Substring(0, 8)
  $made = (& net.exe user $user $password /add 2>&1 | Out-String).Trim()
  if ($LASTEXITCODE -ne 0) { throw "could not create the throwaway account $user (exit $LASTEXITCODE): $made" }
  $userCreated = $true
  $credential = New-Object System.Management.Automation.PSCredential(".\$user", (ConvertTo-SecureString $password -AsPlainText -Force))
  $otherOut = Join-Path $shared 'other.txt'
  $probe = Start-Process -FilePath 'powershell.exe' -Credential $credential -LoadUserProfile -WorkingDirectory $shared -PassThru -Wait -WindowStyle Hidden `
    -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $shared 'probe.ps1'), '-Pipe', $pipe, '-Out', $otherOut)
  Check (Test-Path $otherOut) "the other user's probe ran and reported (exit code $($probe.ExitCode))"
  $other = (Get-Content $otherOut -Raw).Trim()
  Write-Host "other user result: $other"
  Check ($other -like 'FAILED UnauthorizedAccessException*') 'a different local Windows user is refused with access denied (not a timeout, not connected)'

  # 3. The refusal changed nothing: the agent still runs and the owner still connects.
  Check (-not $agent.HasExited) 'the agent is still running after the refused attempt'
  & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $shared 'probe.ps1') -Pipe $pipe -Out $ownerOut | Out-Null
  Check ((Get-Content $ownerOut -Raw).Trim() -eq 'CONNECTED') 'the owner can still connect afterwards'
}
finally {
  if ($agent -and -not $agent.HasExited) { Stop-Process -Id $agent.Id -Force -ErrorAction SilentlyContinue }
  if ($userCreated) { net user $user /delete | Out-Null }
}
Write-Host 'second-user pipe test passed'
