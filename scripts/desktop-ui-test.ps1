# D1 UI test: drives the REAL WPF owner console through Windows UI Automation against a real agent and gateway.
# Windows PowerShell 5.1, interactive CI desktop. Any failed expectation throws; nothing is simulated.
param([Parameter(Mandatory = $true)][string]$Exe, [Parameter(Mandatory = $true)][string]$GatewayDll, [string]$OutDir = "artifacts")
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
$AE = [System.Windows.Automation.AutomationElement]
$Scope = [System.Windows.Automation.TreeScope]
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path
$Exe = (Resolve-Path $Exe).Path
$GatewayDll = (Resolve-Path $GatewayDll).Path
$AgentExe = Join-Path (Split-Path $Exe) 'orbitdrive-agent.exe'
$work = Join-Path $env:RUNNER_TEMP ("orbit-ui-" + [guid]::NewGuid().ToString('N'))
$state = Join-Path $work 'state'; $share = Join-Path $work 'Holiday Photos'
New-Item -ItemType Directory -Force -Path $state, $share | Out-Null
Set-Content -Path (Join-Path $share 'itinerary.txt') -Value 'Day 1: arrive'
Set-Content -Path (Join-Path $share 'private-diary.txt') -Value 'hidden'
(Get-Item (Join-Path $share 'private-diary.txt')).Attributes = 'Hidden'
$relay = 'http://127.0.0.1:5199'
$script:win = $null; $script:shots = 0; $results = [ordered]@{}

function Check([bool]$condition, [string]$what) { if (-not $condition) { throw "FAILED: $what" }; Write-Host "ok  $what"; $results[$what] = 'passed' }
function Find([string]$id, [int]$timeoutMs = 15000) {
    $condition = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $id)
    $deadline = (Get-Date).AddMilliseconds($timeoutMs)
    do { $element = $script:win.FindFirst($Scope::Descendants, $condition); if ($element) { return $element }; Start-Sleep -Milliseconds 200 } while ((Get-Date) -lt $deadline)
    throw "Element '$id' not found"
}
function WaitText([string]$id, [string]$pattern, [int]$timeoutMs = 30000) {
    $deadline = (Get-Date).AddMilliseconds($timeoutMs); $last = ''
    do { try { $last = (Find $id 1000).Current.Name } catch { $last = '' }; if ($last -match $pattern) { return $last }; Start-Sleep -Milliseconds 250 } while ((Get-Date) -lt $deadline)
    throw "Element '$id' never matched /$pattern/ (last text: '$last')"
}
function Click([string]$id) { (Find $id).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function SetText([string]$id, [string]$value) { (Find $id).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($value) }
function Nav([string]$page) {
    $condition = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $page)
    $item = (Find 'Navigation').FindFirst($Scope::Descendants, $condition)
    if (-not $item) { throw "Navigation item '$page' not found" }
    $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 400
}
function ListItems([string]$id) {
    $condition = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
    @((Find $id).FindAll($Scope::Descendants, $condition) | ForEach-Object { $_.Current.Name })
}
function Shot([string]$name) {
    $script:shots++
    $rect = $script:win.Current.BoundingRectangle
    $bitmap = New-Object System.Drawing.Bitmap([int]$rect.Width, [int]$rect.Height)
    [System.Drawing.Graphics]::FromImage($bitmap).CopyFromScreen([int]$rect.X, [int]$rect.Y, 0, 0, $bitmap.Size)
    $bitmap.Save((Join-Path $OutDir ("console-{0:D2}-{1}.png" -f $script:shots, $name)), [System.Drawing.Imaging.ImageFormat]::Png)
}
function Toggle([string]$id) { (Find $id).GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle(); Start-Sleep -Milliseconds 500 }
function IsOn([string]$id) { (Find $id).GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On }
function RunEntries { $key = Get-Item 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -ErrorAction SilentlyContinue; if (-not $key) { return @() }; @($key.GetValueNames() | ForEach-Object { $key.GetValue($_) } | Where-Object { $_ -like "*$state*" }) }
# Every enabled, on-screen button, field, checkbox and list item of the current page must be reachable by keyboard.
function UnreachableControls {
    $types = @([System.Windows.Automation.ControlType]::Button, [System.Windows.Automation.ControlType]::Edit, [System.Windows.Automation.ControlType]::CheckBox, [System.Windows.Automation.ControlType]::ListItem)
    $bad = @()
    foreach ($type in $types) {
        $condition = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $type)
        foreach ($element in $script:win.FindAll($Scope::Descendants, $condition)) {
            $c = $element.Current
            if ($c.IsEnabled -and -not $c.IsOffscreen -and $c.AutomationId -notmatch '^(Minimize|Maximize|Close|Restore|TitleBar|PART_|SmallDecrease|SmallIncrease|LargeDecrease|LargeIncrease|LineUp|LineDown|PageUp|PageDown|Thumb|PairLink$)' -and -not $c.IsKeyboardFocusable) { $bad += "$($c.ControlType.ProgrammaticName):$($c.AutomationId):$($c.Name)" }
        }
    }
    $bad
}
function AttachWindow([System.Diagnostics.Process]$process) {
    $condition = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $process.Id)
    for ($i = 0; $i -lt 100; $i++) {
        $script:win = $AE::RootElement.FindFirst($Scope::Children, $condition)
        if ($script:win) { return }
        Start-Sleep -Milliseconds 300
    }
    throw "UI Automation could not find the OrbitDrive window"
}
function Api([string]$method, [string]$path, $body = $null, $headers = @{}) {
    try {
        if ($null -ne $body) { return @{ status = 200; body = Invoke-RestMethod -Method $method -Uri ($relay + $path) -Headers $headers -ContentType 'application/json' -Body ($body | ConvertTo-Json -Compress) } }
        return @{ status = 200; body = Invoke-RestMethod -Method $method -Uri ($relay + $path) -Headers $headers }
    } catch { return @{ status = [int]$_.Exception.Response.StatusCode; body = $null } }
}
function Agents { @(Get-Process -Name 'orbitdrive-agent' -ErrorAction SilentlyContinue) }

$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:OrbitDrive__DevIdentity__Enabled = 'true'
$env:OrbitDrive__DevIdentity__Secret = [guid]::NewGuid().ToString('N') + [guid]::NewGuid().ToString('N')
$env:OrbitDrive__AllowedWebOrigins__0 = 'http://localhost:5173'
$gateway = Start-Process -FilePath 'dotnet' -ArgumentList "`"$GatewayDll`"", '--urls', $relay -WorkingDirectory (Split-Path $GatewayDll) -PassThru -WindowStyle Hidden
$console = $null
try {
    $healthy = $false
    for ($i = 0; $i -lt 100 -and -not $healthy; $i++) { try { $healthy = (Invoke-WebRequest -UseBasicParsing "$relay/healthz").StatusCode -eq 200 } catch { Start-Sleep -Milliseconds 200 } }
    Check $healthy 'the real gateway process is running'
    Check ((Agents).Count -eq 0) 'no agent is running before the console starts'

    $console = Start-Process -FilePath $Exe -ArgumentList '--state', "`"$state`"" -PassThru
    AttachWindow $console
    [void](WaitText 'StateText' '^Not set up$')
    Check $true 'fresh install shows "Not set up" (real agent state over the local channel)'
    Check ((Agents).Count -eq 1) 'the console started exactly one agent'
    Shot 'overview-not-set-up'

    $second = Start-Process -FilePath $Exe -ArgumentList '--state', "`"$state`"" -PassThru -Wait
    Check ($second.ExitCode -eq 3) 'a second console instance exits instead of opening'
    Check ((Agents).Count -eq 1) 'still exactly one agent after the second launch attempt'

    Nav 'Shared folders'
    Check ((WaitText 'RootsEmpty' 'Nothing is shared') -ne '') 'nothing is shared by default'
    SetText 'RootName' 'Ghost'; SetText 'RootPath' (Join-Path $work 'no-such-folder'); Click 'AddRootButton'
    [void](WaitText 'RootError' 'Folder not found')
    Check (@(ListItems 'RootsList').Count -eq 0) 'a missing folder is refused with a clear message'
    Shot 'shared-folders-error'
    SetText 'RootName' 'Holiday Photos'; SetText 'RootPath' $share; Click 'AddRootButton'
    for ($i = 0; $i -lt 50 -and @(ListItems 'RootsList').Count -eq 0; $i++) { Start-Sleep -Milliseconds 200 }
    $items = @(ListItems 'RootsList')
    Check ($items.Count -eq 1 -and $items[0] -match 'Holiday Photos' -and $items[0] -match 'read-only') 'the chosen folder is listed as read-only'
    Shot 'shared-folders'

    Nav 'Settings'
    SetText 'GatewayBox' 'http://relay.example.com'; Click 'SaveGatewayButton'
    [void](WaitText 'SettingsError' 'HTTPS')
    Check $true 'a non-HTTPS relay address is refused'
    SetText 'GatewayBox' $relay; SetText 'WebOriginBox' 'http://localhost:5173'; Click 'SaveGatewayButton'
    Shot 'settings'
    # Picture previews: on by default; the switch (also when it is off) and the status line follow what the agent really stored.
    $thumbs = WaitText 'ThumbnailStatus' 'previews|Previews'
    if ($thumbs -match 'not available' -and -not $env:ORBITDRIVE_THUMBNAIL_HELPER) { Check $true 'picture previews report honestly that they are not available on this machine' }
    else {
        Check ($thumbs -match '^Paired devices can see small previews') 'picture previews are on by default'
        Toggle 'ThumbnailsToggle'
        [void](WaitText 'ThumbnailStatus' '^Previews are off')
        Check (-not (IsOn 'ThumbnailsToggle')) 'the previews switch turns previews off, and stays usable while they are off'
        Toggle 'ThumbnailsToggle'
        [void](WaitText 'ThumbnailStatus' '^Paired devices can see small previews')
        Check (IsOn 'ThumbnailsToggle') 'the previews switch turns previews on again'
    }
    Nav 'Overview'
    [void](WaitText 'StateText' '^Online$')
    Check $true 'console shows Online once the agent reaches the relay'
    Shot 'overview-online'

    Nav 'Devices'
    Click 'PairButton'
    $code = WaitText 'PairCode' '^[A-Z2-9]{6}-[A-Z2-9]{10}$'
    Check $true 'a one-time pairing code is shown'
    $qr = Find 'PairQr'
    Check ($qr.Current.BoundingRectangle.Width -ge 200 -and $qr.Current.BoundingRectangle.Height -ge 200) 'a QR code image is shown with the pairing code'
    $signIn = (Api 'Post' '/v1/dev/sign-in' @{ email = 'owner@example.test' }).body
    $auth = @{ Authorization = "Bearer $($signIn.idToken)" }
    $parts = $code.Split('-')
    $claim = (Api 'Post' "/v1/pairing-requests/$($parts[0])/claim" @{ secret = $parts[1]; browserName = 'Pixel 9' } $auth)
    Check ($claim.status -eq 200) 'the browser claimed the code'
    $device = @{ Authorization = $auth.Authorization; 'X-OrbitDrive-Device' = "$($claim.body.browserDeviceId).$($claim.body.deviceSecret)" }
    $prompt = WaitText 'ApprovalText' 'owner@example\.test'
    Check ($prompt -match 'Pixel 9' -and $prompt -match $claim.body.confirmationCode) 'approval prompt shows the account, browser name and the same confirmation code'
    Check ((Api 'Get' '/v1/pcs' $null $device).status -eq 403) 'the browser has no access before the owner approves'
    Shot 'devices-approval'
    Click 'ApproveButton'
    $pcs = $null
    for ($i = 0; $i -lt 50; $i++) { $pcs = Api 'Get' '/v1/pcs' $null $device; if ($pcs.status -eq 200) { break }; Start-Sleep -Milliseconds 200 }
    Check ($pcs.status -eq 200 -and $pcs.body.pcs.Count -eq 1) 'after Approve the browser sees this PC'
    $pcId = $pcs.body.pcs[0].pcId
    $roots = (Api 'Get' "/v1/pcs/$pcId/roots" $null $device).body.roots
    Check ($roots.Count -eq 1 -and $roots[0].name -eq 'Holiday Photos' -and $roots[0].readOnly) 'the browser sees exactly the folder shared in the console'
    $page = (Api 'Post' "/v1/pcs/$pcId/directory-queries" @{ rootId = $roots[0].rootId; pageSize = 50 } $device).body
    Check (@($page.entries | Where-Object { $_.name -eq 'itinerary.txt' }).Count -eq 1) 'the real file inside it is listed'
    # A real download by the paired browser shows up on the PC with what the PC actually sent.
    $itinerary = @($page.entries | Where-Object { $_.name -eq 'itinerary.txt' })[0]
    $prepared = Api 'Post' '/v1/download-sessions' @{ pcId = $pcId; entryId = $itinerary.entryId } $device
    Check ($prepared.status -eq 200 -and $prepared.body.urlPath) 'the browser prepared a download'
    $bytes = (Invoke-WebRequest -UseBasicParsing ($relay + $prepared.body.urlPath)).Content
    Check ($bytes.Length -eq [int]$prepared.body.size) 'the file arrived through the relay'
    Nav 'Transfers'
    $sent = $null
    for ($i = 0; $i -lt 40; $i++) { $sent = @(ListItems 'TransfersList') | Where-Object { $_ -match 'itinerary\.txt' -and $_ -match 'sent to the relay' }; if ($sent) { break }; Start-Sleep -Milliseconds 250 }
    Check ($null -ne $sent) 'the PC console lists the transfer with the bytes it sent to the relay'
    Check (-not (@(ListItems 'TransfersList') -join ' ' -match 'saved|downloaded|complete')) 'the PC never claims the file was saved on the phone'
    Shot 'transfers'
    # Hidden items: private unless the owner opts in for that folder, from the console.
    Check (@($page.entries | Where-Object { $_.name -eq 'private-diary.txt' }).Count -eq 0) 'a hidden file in the shared folder is not listed by default'
    Nav 'Shared folders'
    $condition = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
    (Find 'RootsList').FindFirst($Scope::Descendants, $condition).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Click 'ToggleHiddenButton'
    $withHidden = $null
    for ($i = 0; $i -lt 40; $i++) { $withHidden = (Api 'Post' "/v1/pcs/$pcId/directory-queries" @{ rootId = $roots[0].rootId; pageSize = 50 } $device).body; if (@($withHidden.entries | Where-Object { $_.name -eq 'private-diary.txt' -and $_.hidden }).Count -eq 1) { break }; Start-Sleep -Milliseconds 250 }
    Check (@($withHidden.entries | Where-Object { $_.name -eq 'private-diary.txt' -and $_.hidden }).Count -eq 1) 'after the owner opts in, the hidden file is listed and marked hidden'
    Check ((@(ListItems 'RootsList'))[0] -match 'hidden items included') 'the console shows that hidden items are included for that folder'
    Shot 'shared-folders-hidden-included'
    (Find 'RootsList').FindFirst($Scope::Descendants, $condition).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Click 'ToggleHiddenButton'
    for ($i = 0; $i -lt 40; $i++) { $withHidden = (Api 'Post' "/v1/pcs/$pcId/directory-queries" @{ rootId = $roots[0].rootId; pageSize = 50 } $device).body; if (@($withHidden.entries | Where-Object { $_.name -eq 'private-diary.txt' }).Count -eq 0) { break }; Start-Sleep -Milliseconds 250 }
    Check (@($withHidden.entries | Where-Object { $_.name -eq 'private-diary.txt' }).Count -eq 0) 'turning it off hides the file again'
    Nav 'Overview'

    Nav 'Overview'
    Click 'PauseButton'
    [void](WaitText 'StateText' '^Paused$')
    Check ((Api 'Get' "/v1/pcs/$pcId/roots" $null $device).status -eq 503) 'Pause Remote Access cuts access immediately'
    Shot 'overview-paused'
    Click 'PauseButton'
    [void](WaitText 'StateText' '^Online$')
    Check ((Api 'Get' "/v1/pcs/$pcId/roots" $null $device).status -eq 200) 'Resume restores access'
    Nav 'Activity'
    Check (@(ListItems 'ActivityList').Count -ge 4) 'activity history recorded the session'
    # The stored history (kept by the agent, shown in plain words) appears on the same page. The list is virtualised and newest-first, so the
    # entries the window can see are the most recent ones: the ones this very test caused (pairing, a file sent, folders changed).
    $historyText = ''
    for ($i = 0; $i -lt 30 -and $historyText -notmatch 'Pairing finished'; $i++) { Start-Sleep -Milliseconds 500; $historyText = (@(ListItems 'HistoryList') -join "`n") }
    Check ($historyText -match 'Pairing finished') 'stored history lists the pairing this test did'
    Check ($historyText -match 'A file was sent to a browser') 'stored history lists the file this test downloaded'
    Check ($historyText -match 'Shared folders changed') 'stored history lists the shared folder change'
    Check (@(ListItems 'HistoryList').Count -ge 3) 'stored history has several entries'
    Check ($historyText -notmatch '[A-Za-z]:\\') 'stored history shows no file system paths'
    Shot 'activity'
    Nav 'Diagnostics'
    $diagnostics = WaitText 'DiagnosticsText' 'PC id'
    Shot 'diagnostics'

    Nav 'Overview'
    Click 'QuitButton'
    Check ($console.WaitForExit(15000)) 'Quit closes the console'
    for ($i = 0; $i -lt 50 -and (Agents).Count -gt 0; $i++) { Start-Sleep -Milliseconds 200 }
    Check ((Agents).Count -eq 0) 'Quit leaves no orphan agent process'
    Check ((Api 'Get' '/v1/pcs' $null $device).body.pcs[0].online -eq $false) 'after Quit the PC is offline to the browser'

    $console = Start-Process -FilePath $Exe -ArgumentList '--state', "`"$state`"" -PassThru
    AttachWindow $console
    [void](WaitText 'StateText' '^Online$')
    Nav 'Shared folders'
    Check (@(ListItems 'RootsList').Count -eq 1) 'relay address, identity and shared folder persist across a restart'
    Check ((Api 'Get' "/v1/pcs/$pcId/roots" $null $device).status -eq 200) 'the paired browser works again without re-pairing'
    $script:win.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    Start-Sleep -Seconds 2
    Check (-not $console.HasExited -and (Agents).Count -eq 1) 'closing the window keeps the console (tray) and agent running'
    Check ((Api 'Get' "/v1/pcs/$pcId/roots" $null $device).status -eq 200) 'remote access continues while the window is closed'
    $console.Kill(); $console.WaitForExit()
    $console = Start-Process -FilePath $Exe -ArgumentList '--state', "`"$state`"" -PassThru
    AttachWindow $console
    Check ((Agents).Count -eq 1) 'a reopened console attaches to the running agent instead of starting another'
    Nav 'Devices'
    for ($i = 0; $i -lt 60 -and @(ListItems 'DevicesList').Count -eq 0; $i++) { Start-Sleep -Milliseconds 250 }
    $paired = @(ListItems 'DevicesList')
    Check ($paired.Count -eq 1 -and $paired[0] -eq 'Pixel 9') 'the paired browser is listed on the PC'
    $condition = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, 'Pixel 9')
    (Find 'DevicesList').FindFirst($Scope::Descendants, $condition).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Shot 'devices-paired'
    Click 'RevokeDeviceButton'
    $after = $null
    for ($i = 0; $i -lt 50; $i++) { $after = Api 'Get' "/v1/pcs/$pcId/roots" $null $device; if ($after.status -eq 404) { break }; Start-Sleep -Milliseconds 200 }
    Check ($after.status -eq 404) 'removing the device on the PC ends that browser''s access'

    # The agent is stopped from outside; the console must not relaunch it in a loop.
    & $AgentExe ctl quit --state $state | Out-Null
    Start-Sleep -Seconds 5
    Check ((Agents).Count -eq 0) 'an agent stopped elsewhere is not silently relaunched by the console'

    Nav 'Overview'
    [void](WaitText 'StateText' '^Stopped$')
    Check $true 'the console says the agent was stopped (a deliberate stop is not treated as a crash)'
    Click 'StartAgentButton'
    [void](WaitText 'StateText' '^Online$' 60000)
    Check ((Agents).Count -eq 1) 'Start brings the agent back'

    # Crash recovery: an agent that dies unexpectedly is restarted, but only a bounded number of times.
    for ($crash = 1; $crash -le 3; $crash++) {
        $old = (Agents)[0].Id
        Stop-Process -Id $old -Force
        $back = $false
        for ($i = 0; $i -lt 160 -and -not $back; $i++) { Start-Sleep -Milliseconds 250; $now = Agents; $back = ($now.Count -eq 1 -and $now[0].Id -ne $old) }
        Check $back "crash $crash of 3: the console restarted the agent by itself"
        [void](WaitText 'StateText' '^Online$' 60000)
    }
    Check ((Agents).Count -eq 1) 'exactly one agent after three recoveries (no duplicates)'
    Stop-Process -Id (Agents)[0].Id -Force
    [void](WaitText 'StateText' '^Agent keeps stopping$' 30000)
    Start-Sleep -Seconds 6
    Check ((Agents).Count -eq 0) 'a fourth crash in a row is NOT restarted (no launch loop)'
    Shot 'overview-agent-keeps-stopping'
    Click 'StartAgentButton'
    [void](WaitText 'StateText' '^Online$' 60000)
    Check ((Agents).Count -eq 1) 'the owner can start it again explicitly'

    # A leftover agent of this installation holds the agent lock but no longer answers (as after an interrupted update): Start must replace it.
    $lockFile = Join-Path $state 'agent.lock'
    & $AgentExe ctl quit --state $state | Out-Null
    for ($i = 0; $i -lt 80 -and (Agents).Count -ne 0; $i++) { Start-Sleep -Milliseconds 250 }
    [void](WaitText 'StateText' '^Stopped$')
    $stuck = Start-Process -FilePath $AgentExe -ArgumentList @('run', '--state', ('"' + $state + '"')) -PassThru -WindowStyle Hidden
    # Freeze it the moment it holds the lock, before it can answer: if the console attached to it while it still worked, this would not be the case under test.
    Add-Type -Namespace Native -Name Proc -MemberDefinition '[DllImport("ntdll.dll")] public static extern int NtSuspendProcess(System.IntPtr handle);'
    $held = $false
    for ($i = 0; $i -lt 1500 -and -not $held; $i++) {
        try { $probe = [IO.File]::Open($lockFile, 'OpenOrCreate', 'ReadWrite', 'None'); $probe.Close(); Start-Sleep -Milliseconds 10 } catch [System.IO.IOException] { $held = $true }
    }
    [void][Native.Proc]::NtSuspendProcess($stuck.Handle)
    Check $held 'the stand-in agent was frozen while holding the lock'
    # Two routes, both must end the same way: the console never reached the frozen agent (Start is shown and pressed), or it attached just before the
    # freeze and has to notice the silence itself (about a minute) and replace it.
    try { Click 'StartAgentButton' } catch { Write-Host 'console had attached to the frozen agent; waiting for it to notice the silence' }
    for ($i = 0; $i -lt 600 -and (Get-Process -Id $stuck.Id -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 250 }
    try { [void](WaitText 'StateText' '^Online$' 90000) }
    catch {
        Nav 'Activity'
        Write-Host "DIAGNOSTIC stand-in alive: $($null -ne (Get-Process -Id $stuck.Id -ErrorAction SilentlyContinue)); agents: $((Agents).Count)"
        Write-Host ("DIAGNOSTIC activity: " + ((@(ListItems 'ActivityList')) -join ' | '))
        throw
    }
    Check ($null -eq (Get-Process -Id $stuck.Id -ErrorAction SilentlyContinue)) 'a frozen leftover agent of this installation was ended'
    Check ((Agents).Count -eq 1) 'exactly one agent runs after replacing the frozen one'
    Nav 'Activity'
    $note = $false
    for ($i = 0; $i -lt 20 -and -not $note; $i++) { $note = ((@(ListItems 'ActivityList') -join "`n") -match 'was not answering and was stopped'); if (-not $note) { Start-Sleep -Milliseconds 500 } }
    Check $note 'the console says plainly that it stopped a leftover agent'
    Nav 'Overview'

    # A different program holding the lock is NOT this installation's agent: it must never be killed.
    & $AgentExe ctl quit --state $state | Out-Null
    for ($i = 0; $i -lt 80 -and (Agents).Count -ne 0; $i++) { Start-Sleep -Milliseconds 250 }
    [void](WaitText 'StateText' '^Stopped$')
    $holderScript = '$f = [IO.File]::Open(''' + $lockFile + ''', ''OpenOrCreate'', ''ReadWrite'', ''None''); Start-Sleep -Seconds 120'
    $holder = Start-Process -FilePath 'powershell.exe' -PassThru -WindowStyle Hidden -ArgumentList @('-NoProfile', '-EncodedCommand', [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($holderScript)))
    for ($i = 0; $i -lt 80; $i++) { try { $probe = [IO.File]::Open($lockFile, 'OpenOrCreate', 'ReadWrite', 'None'); $probe.Close(); Start-Sleep -Milliseconds 250 } catch [System.IO.IOException] { break } }
    Check ($null -ne (Get-Process -Id $holder.Id -ErrorAction SilentlyContinue)) 'the unrelated stand-in program is running and holds the lock'
    Start-Sleep -Seconds 4
    Click 'StartAgentButton'
    Start-Sleep -Seconds 14
    Check ($null -ne (Get-Process -Id $holder.Id -ErrorAction SilentlyContinue)) 'an unrelated program holding the lock is left alone'
    Check ((Agents).Count -eq 0) 'no agent was started while something else holds the lock'
    Nav 'Activity'
    $unknown = $false
    for ($i = 0; $i -lt 20 -and -not $unknown; $i++) { $unknown = ((@(ListItems 'ActivityList') -join "`n") -match 'could not be identified'); if (-not $unknown) { Start-Sleep -Milliseconds 500 } }
    Check $unknown 'the console says it could not identify the program holding the lock'
    Nav 'Overview'
    Stop-Process -Id $holder.Id -Force
    Start-Sleep -Seconds 1
    Click 'StartAgentButton'
    [void](WaitText 'StateText' '^Online$' 60000)
    Check ((Agents).Count -eq 1) 'once the lock is free the agent starts again'
    Shot 'overview-online-final'

    # Keyboard reachability on every page.
    foreach ($page in 'Overview', 'Shared folders', 'Devices', 'Transfers', 'Activity', 'Settings', 'Diagnostics') {
        Nav $page
        $bad = @(UnreachableControls)
        Check ($bad.Count -eq 0) "every control on '$page' is reachable by keyboard $($bad -join ', ')"
    }
    Nav 'Overview'
    $script:win.SetFocus(); Start-Sleep -Milliseconds 300
    [System.Windows.Forms.SendKeys]::SendWait('^3'); Start-Sleep -Milliseconds 800
    Check (-not (Find 'PairButton').Current.IsOffscreen) 'Ctrl+3 jumps to Devices from the keyboard'
    [System.Windows.Forms.SendKeys]::SendWait('^6'); Start-Sleep -Milliseconds 800
    Check (-not (Find 'SaveGatewayButton').Current.IsOffscreen) 'Ctrl+6 jumps to Settings from the keyboard'

    # Optional start at sign-in: off by default, per-user, removable.
    Check (-not (IsOn 'StartAtSignIn') -and @(RunEntries).Count -eq 0) 'start at sign-in is off by default and nothing is registered'
    Toggle 'StartAtSignIn'
    $entries = @(RunEntries)
    Check ((IsOn 'StartAtSignIn') -and $entries.Count -eq 1 -and $entries[0] -like "*$Exe*" -and $entries[0] -like '*--background*') 'turning it on registers this console for the current user only (HKCU Run, hidden start)'
    # Sending limits set by the owner in the console reach the agent and are validated.
    Check ((Find 'MaxSpeedBox').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -eq '0' -and (Find 'MaxFilesBox').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -eq '4') 'limits start as: no speed limit, four files at once'
    SetText 'MaxSpeedBox' '750'; SetText 'MaxFilesBox' '2'
    Click 'SaveLimitsButton'
    $limits = $null
    for ($i = 0; $i -lt 40; $i++) { $limits = (& $AgentExe ctl status --state $state | ConvertFrom-Json); if ($limits.maxUploadKBps -eq 750) { break }; Start-Sleep -Milliseconds 250 }
    Check ($limits.maxUploadKBps -eq 750 -and $limits.maxConcurrentTransfers -eq 2) 'the agent stored the limits chosen in the console'
    SetText 'MaxFilesBox' '99'
    Click 'SaveLimitsButton'
    Check ((WaitText 'LimitsError' '1 and 16') -ne '') 'an out-of-range limit is refused with a clear message'
    Check ((& $AgentExe ctl status --state $state | ConvertFrom-Json).maxConcurrentTransfers -eq 2) '...and the stored limit is unchanged'
    SetText 'MaxSpeedBox' '0'; SetText 'MaxFilesBox' '4'
    Click 'SaveLimitsButton'
    for ($i = 0; $i -lt 40; $i++) { if ((& $AgentExe ctl status --state $state | ConvertFrom-Json).maxUploadKBps -eq 0) { break }; Start-Sleep -Milliseconds 250 }
    Check (-not (IsOn 'PauseOnMetered')) 'pause-on-metered is off by default'
    Check (-not (IsOn 'PauseWhenLocked')) 'pause-while-locked is off by default'
    Toggle 'PauseWhenLocked'
    Check ((Get-Content (Join-Path $state 'console.json') -Raw) -match '"pauseWhenLocked":\s*true') 'the lock setting is saved'
    Shot 'settings-options'
    Toggle 'StartAtSignIn'
    Check (-not (IsOn 'StartAtSignIn') -and @(RunEntries).Count -eq 0) 'turning it off removes the registration'

    Nav 'Overview'
    Click 'QuitButton'
    Check ($console.WaitForExit(15000)) 'Quit closes the console again'
    for ($i = 0; $i -lt 50 -and (Agents).Count -gt 0; $i++) { Start-Sleep -Milliseconds 200 }
    Check ((Agents).Count -eq 0) 'no agent is left after Quit'

    # Started the way Windows starts it at sign-in: no window, agent running; launching it again shows the window.
    $console = Start-Process -FilePath $Exe -ArgumentList '--background', '--state', "`"$state`"" -PassThru
    for ($i = 0; $i -lt 80 -and (Agents).Count -eq 0; $i++) { Start-Sleep -Milliseconds 250 }
    Start-Sleep -Seconds 3
    $console.Refresh()
    $visible = $AE::RootElement.FindFirst($Scope::Children, (New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $console.Id)))
    Check ((Agents).Count -eq 1 -and -not $console.HasExited -and $null -eq $visible) 'a background (sign-in) start runs the agent without opening a window'
    $again = Start-Process -FilePath $Exe -ArgumentList '--state', "`"$state`"" -PassThru -Wait
    Check ($again.ExitCode -eq 3) 'launching OrbitDrive again does not start a second copy'
    AttachWindow $console
    [void](WaitText 'StateText' '^Online$' 60000)
    Check ((Agents).Count -eq 1) '...and instead brings the running console''s window forward'
    Nav 'Settings'
    Check (IsOn 'PauseWhenLocked') 'the lock setting persisted across a restart'
    Nav 'Overview'
    Click 'QuitButton'
    [void]$console.WaitForExit(15000)
    for ($i = 0; $i -lt 50 -and (Agents).Count -gt 0; $i++) { Start-Sleep -Milliseconds 200 }

    # Dark theme: same real console, forced dark, for visual review.
    $console = Start-Process -FilePath $Exe -ArgumentList '--theme', 'dark', '--state', "`"$state`"" -PassThru
    AttachWindow $console
    [void](WaitText 'StateText' '^Online$' 60000)
    Shot 'dark-overview'
    Nav 'Shared folders'; Shot 'dark-shared-folders'
    Nav 'Devices'; Click 'PairButton'; [void](WaitText 'PairCode' '^[A-Z0-9]{6}-[A-Z0-9]{10}$'); Start-Sleep -Milliseconds 600; Shot 'dark-devices'
    Nav 'Settings'; Shot 'dark-settings'
    Nav 'Diagnostics'
    Check ((WaitText 'DiagnosticsText' 'Theme: Dark') -ne '') 'the console runs with the dark theme when asked'
    $diagnostics = (Find 'DiagnosticsText').Current.Name
    Check ($diagnostics -match 'Network cost: (unrestricted|metered|unknown)') "Windows answered the network-cost query: $([regex]::Match($diagnostics, 'Network cost: [^\r\n]*').Value)"
    Shot 'dark-diagnostics'
    Nav 'Overview'
    Click 'QuitButton'
    [void]$console.WaitForExit(15000)
    for ($i = 0; $i -lt 50 -and (Agents).Count -gt 0; $i++) { Start-Sleep -Milliseconds 200 }
    Check ((Agents).Count -eq 0) 'no orphan agent at the end of the run'

    $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $elevated = (New-Object System.Security.Principal.WindowsPrincipal($identity)).IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
    $os = Get-CimInstance Win32_OperatingSystem
    @{ checks = $results; screenshots = $script:shots; os = $os.Caption; osVersion = $os.Version; elevated = $elevated; diagnostics = $diagnostics
       note = 'GitHub-hosted runner: Windows Server, elevated account. Not Windows 11, standard-user, multi-DPI, high-contrast, lock/unlock or tray-interaction evidence.' } |
        ConvertTo-Json -Depth 4 | Set-Content (Join-Path $OutDir 'desktop-ui-test.json')
    Write-Host "ALL $($results.Count) CHECKS PASSED; $($script:shots) screenshots"
}
catch {
    # Keep a picture of the whole desktop at the moment of failure, then fail the run as before.
    try {
        $screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
        $bitmap = New-Object System.Drawing.Bitmap($screen.Width, $screen.Height)
        [System.Drawing.Graphics]::FromImage($bitmap).CopyFromScreen(0, 0, 0, 0, $bitmap.Size)
        $bitmap.Save((Join-Path $OutDir 'console-99-failure-desktop.png'), [System.Drawing.Imaging.ImageFormat]::Png)
    } catch { }
    throw
}
finally {
    Get-Item 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -ErrorAction SilentlyContinue | ForEach-Object { $k = $_; $k.GetValueNames() | Where-Object { $_ -like 'OrbitDrive-*' } | ForEach-Object { Remove-ItemProperty -Path $k.PSPath -Name $_ -ErrorAction SilentlyContinue } }
    if ($console -and -not $console.HasExited) { $console.Kill() }
    Agents | ForEach-Object { $_.Kill() }
    if (-not $gateway.HasExited) { $gateway.Kill() }
}
