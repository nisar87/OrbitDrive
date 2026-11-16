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
    Nav 'Overview'
    [void](WaitText 'StateText' '^Online$')
    Check $true 'console shows Online once the agent reaches the relay'
    Shot 'overview-online'

    Nav 'Devices'
    Click 'PairButton'
    $code = WaitText 'PairCode' '^[A-Z2-9]{6}-[A-Z2-9]{10}$'
    Check $true 'a one-time pairing code is shown'
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

    # The agent is stopped from outside; the console must not relaunch it in a loop.
    & $AgentExe ctl quit --state $state | Out-Null
    Start-Sleep -Seconds 5
    Check ((Agents).Count -eq 0) 'an agent stopped elsewhere is not silently relaunched by the console'

    $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $elevated = (New-Object System.Security.Principal.WindowsPrincipal($identity)).IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
    $os = Get-CimInstance Win32_OperatingSystem
    @{ checks = $results; screenshots = $script:shots; os = $os.Caption; osVersion = $os.Version; elevated = $elevated; diagnostics = $diagnostics
       note = 'GitHub-hosted runner: Windows Server, elevated account. Not Windows 11, standard-user, multi-DPI or tray-interaction evidence.' } |
        ConvertTo-Json -Depth 4 | Set-Content (Join-Path $OutDir 'desktop-ui-test.json')
    Write-Host "ALL $($results.Count) CHECKS PASSED; $($script:shots) screenshots"
}
finally {
    if ($console -and -not $console.HasExited) { $console.Kill() }
    Agents | ForEach-Object { $_.Kill() }
    if (-not $gateway.HasExited) { $gateway.Kill() }
}
