# D0 spike: launch the real WPF window, inspect it through Windows UI Automation and capture real screen pixels.
# Runs in Windows PowerShell 5.1 on an interactive CI desktop. Fails (never fakes) if the window cannot be automated.
param([Parameter(Mandatory = $true)][string]$Exe, [string]$OutDir = "artifacts")
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$process = Start-Process -FilePath $Exe -ArgumentList '--exit-after-ms', '45000' -PassThru
try {
    $window = $null
    for ($i = 0; $i -lt 100 -and -not $window; $i++) {
        Start-Sleep -Milliseconds 300
        $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)
        $window = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $condition)
    }
    if (-not $window) { throw "UI Automation could not find the OrbitDrive window (no interactive desktop?)" }
    $found = @{}
    foreach ($id in 'ProductTitle', 'ConnectionState', 'SharedFolders', 'RuntimeInfo') {
        $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
        $element = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        if (-not $element) { throw "Automation element '$id' not found" }
        $found[$id] = $element.Current.Name
        Write-Host "UIA $id = $($element.Current.Name)"
    }
    if ($found['ProductTitle'] -ne 'OrbitDrive for Windows') { throw "Unexpected title text" }
    if ($found['SharedFolders'] -notmatch 'none') { throw "A fresh install must report no shared folders" }
    $rect = $window.Current.BoundingRectangle
    $bitmap = New-Object System.Drawing.Bitmap([int]$rect.Width, [int]$rect.Height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.CopyFromScreen([int]$rect.X, [int]$rect.Y, 0, 0, $bitmap.Size)
    $bitmap.Save((Join-Path $OutDir 'wpf-spike-window.png'), [System.Drawing.Imaging.ImageFormat]::Png)
    $colors = New-Object 'System.Collections.Generic.HashSet[int]'
    for ($x = 0; $x -lt $bitmap.Width; $x += 8) { for ($y = 0; $y -lt $bitmap.Height; $y += 8) { [void]$colors.Add($bitmap.GetPixel($x, $y).ToArgb()) } }
    Write-Host "Screenshot $($bitmap.Width)x$($bitmap.Height), distinct sampled colours: $($colors.Count)"
    if ($colors.Count -lt 16) { throw "Screenshot looks blank ($($colors.Count) colours) - not accepting it as evidence" }
    $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $elevated = (New-Object System.Security.Principal.WindowsPrincipal($identity)).IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
    $os = Get-CimInstance Win32_OperatingSystem
    @{ uia = $found; screenshot = "$($bitmap.Width)x$($bitmap.Height)"; distinctColours = $colors.Count; os = $os.Caption; osVersion = $os.Version; elevated = $elevated; note = 'GitHub-hosted runner: Windows Server, elevated. Not Windows 11 or standard-user evidence.' } |
        ConvertTo-Json | Set-Content (Join-Path $OutDir 'wpf-spike.json')
    Get-Content (Join-Path $OutDir 'wpf-spike.json')
}
finally { if (-not $process.HasExited) { $process.Kill() } }
