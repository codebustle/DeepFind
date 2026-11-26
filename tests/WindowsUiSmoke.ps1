param([Parameter(Mandatory=$true)][string]$Application)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or -not $env:RUNNER_TEMP) {
    throw 'This test requires a disposable GitHub Actions Windows runner.'
}
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class DeepFindTestPointer {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
}
'@
$ae = [System.Windows.Automation.AutomationElement]
$scope = [System.Windows.Automation.TreeScope]::Descendants
$fixture = Join-Path $env:RUNNER_TEMP ('deepfind-ui-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
for ($i = 0; $i -lt 51; $i++) {
    Set-Content (Join-Path $fixture ('document-{0:D3}.txt' -f $i)) 'smokeneedle'
}
Set-Content (Join-Path $fixture 'document-050.txt') 'smokeneedle uniqueuiword'
$settingsPath = Join-Path $env:LOCALAPPDATA 'DeepFind\settings.json'
New-Item -ItemType Directory -Path (Split-Path $settingsPath) -Force | Out-Null
@{Folders=@($fixture); PageSize=50; Theme='Dark'} | ConvertTo-Json | Set-Content $settingsPath -Encoding UTF8
# Windows PowerShell emits a BOM when writing UTF8, which File.ReadAllText handles.

function Wait-For([string]$Description, [scriptblock]$Condition, [int]$Seconds=45) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    do {
        $value = & $Condition
        if ($value) { return $value }
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)
    throw "Timed out: $Description"
}
function By-Id($Root, [string]$Id) {
    $Root.FindFirst($scope, ([System.Windows.Automation.PropertyCondition]::new($ae::AutomationIdProperty, $Id)))
}
function By-Name($Root, [string]$Name) {
    $Root.FindFirst($scope, ([System.Windows.Automation.PropertyCondition]::new($ae::NameProperty, $Name)))
}
function Require-Pattern($Element, $Pattern) {
    if (-not $Element) { throw "Missing control for pattern $($Pattern.ProgrammaticName)." }
    $value = $null
    if ($Element.TryGetCurrentPattern($Pattern, [ref]$value)) { return $value }
    $actual = ($Element.GetSupportedPatterns() | ForEach-Object ProgrammaticName) -join ', '
    throw "Control '$($Element.Current.Name)' ($($Element.Current.ControlType.ProgrammaticName), id=$($Element.Current.AutomationId)) does not expose $($Pattern.ProgrammaticName). Actual: $actual."
}
function Invoke-Control($Element) {
    (Require-Pattern $Element ([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
}
function Set-Text($Element, [string]$Value) {
    (Require-Pattern $Element ([System.Windows.Automation.ValuePattern]::Pattern)).SetValue($Value)
}
function Select-Tab($Element) {
    (Require-Pattern $Element ([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
}
function Assert-Page([string]$Expected) {
    $label = By-Id $script:main PageLabel
    $label -and $label.Current.Name -eq $Expected
}
function Find-AppControl([string]$Name, $ControlType) {
    $root = $ae::RootElement
    $conditions = @(
        ([System.Windows.Automation.PropertyCondition]::new($ae::NameProperty, $Name)),
        ([System.Windows.Automation.PropertyCondition]::new($ae::ProcessIdProperty, $script:process.Id)),
        ([System.Windows.Automation.PropertyCondition]::new($ae::ControlTypeProperty, $ControlType))
    )
    $root.FindFirst($scope, ([System.Windows.Automation.AndCondition]::new([System.Windows.Automation.Condition[]]$conditions)))
}
function Find-AppElement([string]$Name) {
    Find-AppControl $Name ([System.Windows.Automation.ControlType]::Window)
}
function Click-TestControl($Element) {
    if ($Element.Current.ProcessId -ne $script:process.Id -or $Element.Current.IsOffscreen) {
        throw 'Refusing to click outside the visible disposable DeepFind window.'
    }
    $rect = $Element.Current.BoundingRectangle
    if ($rect.Width -le 0 -or $rect.Height -le 0) { throw 'Control has empty click bounds.' }
    $x = [int]($rect.X + $rect.Width / 2); $y = [int]($rect.Y + $rect.Height / 2)
    if (-not [DeepFindTestPointer]::SetCursorPos($x, $y)) { throw 'Could not move test pointer.' }
    [DeepFindTestPointer]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
    [DeepFindTestPointer]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
}
function Select-Combo($Root, [string]$Id, [string]$Label) {
    $combo = By-Id $Root $Id
    Write-Host "Selecting $Id option '$Label'."
    $expand = Require-Pattern $combo ([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    $expand.Expand()
    $item = Wait-For "combo option $Label" {
        Find-AppControl $Label ([System.Windows.Automation.ControlType]::ListItem)
    }
    $selection = $null
    if ($item.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$selection)) {
        $selection.Select()
    } else {
        # WPF popup's rendered container may expose only synchronized input.
        # Exercise the actual visible item with the runner's pointer in that case.
        Click-TestControl $item
    }
    $null = Wait-For "combo $Id closed after selection" {
        $expand.Current.ExpandCollapseState -eq [System.Windows.Automation.ExpandCollapseState]::Collapsed
    }
}
function Read-Settings {
    Get-Content $settingsPath -Raw | ConvertFrom-Json
}
function Capture-Window($Element, [string]$Name) {
    Add-Type -AssemblyName System.Drawing
    $rect = $Element.Current.BoundingRectangle
    $width = [int]$rect.Width; $height = [int]$rect.Height
    if ($width -lt 100 -or $height -lt 100) { throw "Invalid screenshot bounds: $width x $height" }
    $bmp = [System.Drawing.Bitmap]::new($width, $height)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.CopyFromScreen([int]$rect.X, [int]$rect.Y, 0, 0, $bmp.Size)
        $bmp.Save((Join-Path $env:RUNNER_TEMP "deepfind-ui-$Name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    } finally { $g.Dispose(); $bmp.Dispose() }
}
function Toggle-ResultOption([string]$Name) {
    $item = Wait-For "view menu $Name" { Find-AppControl $Name ([System.Windows.Automation.ControlType]::MenuItem) }
    $pattern = $null
    if ($item.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$pattern)) {
        $pattern.Toggle()
    } else {
        $invoke = $null
        if ($item.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$invoke)) { $invoke.Invoke() }
        else { Click-TestControl $item }
    }
}
$process = Start-Process $Application -PassThru
try {
    $main = Wait-For 'main window' {
        $process.Refresh()
        if ($process.HasExited) { throw "Application exited: $($process.ExitCode)" }
        if ($process.MainWindowHandle -ne 0) { $ae::FromHandle($process.MainWindowHandle) }
    }
    $null = Wait-For '51 indexed files on two pages' { Assert-Page 'Page 1 / 2' } 90
    if (By-Name $main 'Run search') { throw 'Redundant Search button still exists.' }
    $next = By-Id $main NextPage
    $null = Wait-For 'Next enabled' { $next.Current.IsEnabled }
    Invoke-Control $next
    $null = Wait-For 'Second page' { Assert-Page 'Page 2 / 2' }
    $null = Wait-For 'Next disabled on last page' { -not (By-Id $main NextPage).Current.IsEnabled }
    Invoke-Control (By-Id $main FirstPage)
    $null = Wait-For 'First page' { Assert-Page 'Page 1 / 2' }
    $null = Wait-For 'Previous disabled' { -not (By-Id $main PreviousPage).Current.IsEnabled }
    Set-Text (By-Id $main SearchBox) 'uniqueuiword'
    $null = Wait-For 'live query yields one page' { Assert-Page 'Page 1 / 1' }
    Set-Text (By-Id $main SearchBox) 'zznonexistentuiword'
    $null = Wait-For 'no match' { (Assert-Page 'Page 0 / 0') -and (By-Name $main 'No matching files.') }
    Invoke-Control (By-Id $main ClearQueryButton)
    $null = Wait-For 'clear restores result pages' { Assert-Page 'Page 1 / 2' }
    Select-Combo $main PageSize '100'
    $null = Wait-For '100 per page' { (Assert-Page 'Page 1 / 1') -and (Read-Settings).PageSize -eq 100 }
    Select-Combo $main PageSize '50'
    $null = Wait-For '50 per page' { (Assert-Page 'Page 1 / 2') -and (Read-Settings).PageSize -eq 50 }
    Select-Combo $main SortBox 'Name — Z to A'
    $null = Wait-For 'name descending renders expected first file' {
        By-Name (By-Id $main ResultsList) 'document-050.txt'
    }
    Invoke-Control (By-Id $main ViewOptions)
    foreach ($option in @('File path','Match location','Content excerpt','Modified date')) { Toggle-ResultOption $option }
    $null = Wait-For 'all four view flags persisted' {
        $s = Read-Settings
        -not $s.ShowPath -and -not $s.ShowLocation -and -not $s.ShowExcerpt -and -not $s.ShowModified
    }
    $restore = Wait-For 'Restore defaults menu item' { Find-AppControl 'Restore defaults' ([System.Windows.Automation.ControlType]::MenuItem) }
    Invoke-Control $restore
    $null = Wait-For 'view defaults restored' { $s = Read-Settings; $s.ShowPath -and $s.ShowLocation -and $s.ShowExcerpt -and $s.ShowModified }
    Capture-Window $main 'dark-results'
    # UI Automation opens the settings dialog and exercises every tab.
    Invoke-Control (By-Id $main IndexSettingsButton)
    $settings = Wait-For 'Index settings dialog' { Find-AppElement 'Index settings' }
    foreach ($tab in @('Status','Preferences','Folders')) {
        $item = Wait-For "settings tab $tab" { By-Name $settings $tab }
        Select-Tab $item
        $null = Wait-For "$tab selected" {
            $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected
        }
    }
    Select-Tab (By-Name $settings 'Preferences')
    $null = Wait-For 'Preferences input' { By-Id $settings MaxSize }
    Set-Text (By-Id $settings MaxSize) '0'
    Invoke-Control (By-Name $settings 'Apply changes')
    $null = Wait-For 'invalid maximum size shows feedback' { By-Name $settings 'Enter a file size from 1–1024 MB and check intervals from 1–720 hours.' }
    Set-Text (By-Id $settings MaxSize) '100'
    Select-Combo $settings ThemeChoice 'Light'
    Invoke-Control (By-Name $settings 'Apply changes')
    $null = Wait-For 'theme persisted' { (Read-Settings).Theme -eq 'Light' }
    Capture-Window $settings 'light-preferences'
    Select-Tab (By-Name $settings 'Status')
    Invoke-Control (By-Name $settings 'Pause')
    $null = Wait-For 'pause persisted' { (Read-Settings).Paused -eq $true }
    $null = Wait-For 'Resume control' { By-Name $settings 'Resume' }
    Invoke-Control (By-Name $settings 'Update now')
    $null = Wait-For 'paused update feedback' { By-Name $settings 'Resume indexing before starting an update.' }
    Invoke-Control (By-Name $settings 'Resume')
    $null = Wait-For 'resume persisted' { (Read-Settings).Paused -eq $false }
    Invoke-Control (By-Name $settings 'Clear index')
    $no = Wait-For 'clear confirmation No' { Find-AppControl 'No' ([System.Windows.Automation.ControlType]::Button) }
    Invoke-Control $no
    $null = Wait-For 'confirmation dismissed' { -not (Find-AppControl 'No' ([System.Windows.Automation.ControlType]::Button)) }
    if ((Read-Settings).Paused) { throw 'Declining Clear unexpectedly paused indexing.' }
    Invoke-Control (By-Name $settings 'Close')
    $null = Wait-For 'settings dialog closes' { -not (Find-AppElement 'Index settings') }
    $null = Wait-For 'declined Clear retains result set' { Assert-Page 'Page 1 / 2' }
    Capture-Window $main 'light-results'
    Write-Host 'UI smoke: live search, paging boundaries and sizes, name sorting, View flags and restore, settings tabs, invalid input, theme, pause/resume and cancelled Clear passed.'
    if (-not $process.CloseMainWindow() -or -not $process.WaitForExit(15000) -or $process.ExitCode -ne 0) {
        throw 'App did not close cleanly after UI scenario.'
    }
} catch {
    Write-Host "UI failure: $($_.ScriptStackTrace)"
    if ($main) { try { Capture-Window $main 'failure' } catch { Write-Host "Screenshot unavailable: $_" } }
    throw
} finally {
    if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }
}
