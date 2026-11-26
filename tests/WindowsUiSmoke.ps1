param([Parameter(Mandatory=$true)][string]$Application)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or -not $env:RUNNER_TEMP) {
    throw 'This test requires a disposable GitHub Actions Windows runner.'
}
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
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
function Invoke-Control($Element) {
    if (-not $Element) { throw 'Cannot invoke missing control.' }
    $Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}
function Set-Text($Element, [string]$Value) {
    $Element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Value)
}
function Select-Tab($Element) {
    $Element.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
}
function Assert-Page([string]$Expected) {
    $label = By-Id $script:main PageLabel
    $label -and $label.Current.Name -eq $Expected
}
function Find-AppElement([string]$Name) {
    $root = $ae::RootElement
    $conditions = @(
        ([System.Windows.Automation.PropertyCondition]::new($ae::NameProperty, $Name)),
        ([System.Windows.Automation.PropertyCondition]::new($ae::ProcessIdProperty, $script:process.Id)),
        ([System.Windows.Automation.PropertyCondition]::new($ae::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window))
    )
    $root.FindFirst($scope, ([System.Windows.Automation.AndCondition]::new([System.Windows.Automation.Condition[]]$conditions)))
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
    Invoke-Control (By-Name $settings 'Close')
    $null = Wait-For 'settings dialog closes' { -not (Find-AppElement 'Index settings') }
    Write-Host 'UI smoke: live search, empty/clear, first/next pages, boundary controls and settings tab navigation passed.'
    if (-not $process.CloseMainWindow() -or -not $process.WaitForExit(15000) -or $process.ExitCode -ne 0) {
        throw 'App did not close cleanly after UI scenario.'
    }
} finally {
    if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }
}
