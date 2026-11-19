# Runs the REAL embedded apply script (as Windows PowerShell 5.1 does in the product) against a fake install folder.
# Run:  powershell.exe -NoProfile -File tests/desktop/apply-update.test.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$script = Join-Path $PSScriptRoot '..\..\apps\desktop\OrbitDrive.Core\Update\apply-update.ps1'
$work = Join-Path ([IO.Path]::GetTempPath()) ('orbit-apply-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $work | Out-Null
$failures = 0
function Check([bool]$condition, [string]$what) { if ($condition) { "ok   $what" } else { "FAIL $what"; $script:failures++ } }

function New-Install([string]$name) {
  $dir = Join-Path $work $name
  New-Item -ItemType Directory $dir | Out-Null
  Set-Content (Join-Path $dir 'OrbitDrive.exe') 'old app'; Set-Content (Join-Path $dir 'orbitdrive-agent.exe') 'old agent'; Set-Content (Join-Path $dir 'old.txt') 'old'
  $dir
}
function New-Zip([string]$name, [string[]]$entries) {
  $path = Join-Path $work $name
  $zip = [IO.Compression.ZipFile]::Open($path, 'Create')
  foreach ($e in $entries) { $entry = $zip.CreateEntry($e); $w = New-Object IO.StreamWriter($entry.Open()); $w.Write("content of $e"); $w.Dispose() }
  $zip.Dispose()
  $path
}
function Run-Apply($install, $zip, $hash, [int]$wait = 20) {
  $result = Join-Path $work ('result-' + [guid]::NewGuid().ToString('N') + '.json')
  $p = Start-Process powershell.exe -ArgumentList @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',$script,'-InstallDir',$install,'-Zip',$zip,'-Sha256',$hash,'-Version','9.9.9','-ResultFile',$result,'-WaitSeconds',$wait) -Wait -PassThru -WindowStyle Hidden
  [pscustomobject]@{ Exit = $p.ExitCode; Result = (Get-Content $result -Raw | ConvertFrom-Json) }
}
$good = @('OrbitDrive/OrbitDrive.exe', 'OrbitDrive/orbitdrive-agent.exe', 'OrbitDrive/new.txt')

# 1. success: new files in place, old ones gone, no leftovers
$install = New-Install 'ok'; $zip = New-Zip 'ok.zip' $good
$r = Run-Apply $install $zip (Get-FileHash $zip -Algorithm SHA256).Hash
Check ($r.Exit -eq 0 -and $r.Result.ok) 'a good update succeeds'
Check ((Test-Path "$install\new.txt") -and -not (Test-Path "$install\old.txt")) 'new files replaced the old ones'
Check (-not (Test-Path "$install.new") -and -not (Test-Path "$install.old")) 'no .new/.old leftovers'
Check ((Get-Content "$install\OrbitDrive.exe" -Raw) -match 'content of') 'the new OrbitDrive.exe is the one from the zip'

# 2. wrong hash: nothing changes
$install = New-Install 'badhash'; $zip = New-Zip 'badhash.zip' $good
$r = Run-Apply $install $zip ('0' * 64)
Check ($r.Exit -ne 0 -and -not $r.Result.ok -and $r.Result.message -match 'checksum') 'a zip that does not match its checksum is refused'
Check ((Test-Path "$install\old.txt") -and -not (Test-Path "$install\new.txt")) '... and the old version is untouched'

# 3. path traversal inside the zip
$install = New-Install 'evil'; $zip = New-Zip 'evil.zip' ($good + '../evil-escaped.txt')
$r = Run-Apply $install $zip (Get-FileHash $zip -Algorithm SHA256).Hash
Check ($r.Exit -ne 0 -and $r.Result.message -match 'Unsafe path') 'a zip entry that climbs out of the folder is refused'
Check (-not (Test-Path (Join-Path $work 'evil-escaped.txt')) -and -not (Test-Path (Join-Path (Split-Path $work) 'evil-escaped.txt'))) '... and nothing was written outside'
Check (Test-Path "$install\old.txt") '... and the old version is untouched'

# 4. zip without the agent, and a folder that is not an OrbitDrive install
$install = New-Install 'noagent'; $zip = New-Zip 'noagent.zip' @('OrbitDrive/OrbitDrive.exe', 'OrbitDrive/x.txt')
$r = Run-Apply $install $zip (Get-FileHash $zip -Algorithm SHA256).Hash
Check ($r.Exit -ne 0 -and $r.Result.message -match 'agent') 'an update without the agent is refused'
Check (Test-Path "$install\old.txt") '... and the old version is untouched'
$stranger = Join-Path $work 'stranger'; New-Item -ItemType Directory $stranger | Out-Null; Set-Content "$stranger\precious.txt" 'mine'
$zip = New-Zip 'any.zip' $good
$r = Run-Apply $stranger $zip (Get-FileHash $zip -Algorithm SHA256).Hash
Check ($r.Exit -ne 0 -and $r.Result.message -match 'does not look like') 'a folder that is not an OrbitDrive install is never touched'
Check (Test-Path "$stranger\precious.txt") '... its files are still there'

# 5. OrbitDrive still running from the folder: never killed, nothing changed
$install = New-Install 'running'; $zip = New-Zip 'running.zip' $good
Copy-Item "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" "$install\OrbitDrive.exe" -Force
$busy = Start-Process "$install\OrbitDrive.exe" -ArgumentList @('-NoProfile','-Command','Start-Sleep 60') -PassThru -WindowStyle Hidden
Start-Sleep -Seconds 1
$r = Run-Apply $install $zip (Get-FileHash $zip -Algorithm SHA256).Hash 3
Check ($r.Exit -ne 0 -and $r.Result.message -match 'still running') 'a running OrbitDrive blocks the update'
Check (-not $busy.HasExited) '... and was not killed'
Check (Test-Path "$install\old.txt") '... and the old version is untouched'
Stop-Process -Id $busy.Id -Force

Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
if ($failures -gt 0) { "$failures check(s) failed"; exit 1 }
'apply-update: all checks passed'
