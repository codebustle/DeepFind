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

. "$PSScriptRoot\WindowsUiHelpers.ps1"
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
    Select-Combo $main SortBox 'Name — Z to A' 4
    $null = Wait-For 'name descending renders expected first file' {
        $selected = By-Id $main SortBox
        $result = By-Name (By-Id $main ResultsList) 'document-050.txt'
        if ($result) { $result }
        else { By-Name $main 'document-050.txt' }
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

    # Exercise result keyboard actions against an actual installed application and disposable file.
    Add-Type -AssemblyName System.Windows.Forms
    Set-Text (By-Id $main SearchBox) 'uniqueuiword'
    $null = Wait-For 'unique file visible' { (Assert-Page 'Page 1 / 1') -and (By-Name $main 'document-050.txt') }
    (By-Id $main SearchBox).SetFocus()
    [System.Windows.Forms.SendKeys]::SendWait('{DOWN}')
    $null = Wait-For 'first result selected by keyboard' {
        $row = By-Name (By-Id $main ResultsList) 'document-050.txt'
        if ($row) {
            $sel = $null
            if ($row.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$sel)) { $sel.Current.IsSelected }
        }
    }
    [System.Windows.Forms.SendKeys]::SendWait('^c')
    $expectedPath = Join-Path $fixture 'document-050.txt'
    $null = Wait-For 'clipboard contains exact selected file path' { (Get-Clipboard -Raw) -eq $expectedPath }
    [System.Windows.Forms.SendKeys]::SendWait('^p')
    $preview = Wait-For 'preview window for selected file' { Find-AppElement 'document-050.txt' }
    $null = Wait-For 'preview identifies selected source' { By-Name $preview $expectedPath }
    Capture-Window $preview 'light-preview'
    Invoke-Control (By-Name $preview 'Close')
    $null = Wait-For 'preview closes' { -not (Find-AppElement 'document-050.txt') }
    Write-Host 'Installed keyboard selection, clipboard path and file preview passed.'

    # Exercise real local watcher notifications, not manually requested updates.
    $watchPath = Join-Path $fixture 'watcher-added.txt'
    $renamedPath = Join-Path $fixture 'watcher-renamed.txt'
    Set-Text (By-Id $main SearchBox) 'watcherfirstword'
    $null = Wait-For 'watcher fixture initially absent' { Assert-Page 'Page 0 / 0' }
    Set-Content -LiteralPath $watchPath 'watcherfirstword'
    $null = Wait-For 'watcher detects creation' { (Assert-Page 'Page 1 / 1') -and (By-Name $main 'watcher-added.txt') } 60
    Set-Content -LiteralPath $watchPath 'watchersecondword'
    $null = Wait-For 'watcher removes old content' { Assert-Page 'Page 0 / 0' } 60
    Set-Text (By-Id $main SearchBox) 'watchersecondword'
    $null = Wait-For 'watcher indexes changed content' { (Assert-Page 'Page 1 / 1') -and (By-Name $main 'watcher-added.txt') }
    Rename-Item -LiteralPath $watchPath -NewName 'watcher-renamed.txt'
    $null = Wait-For 'watcher detects rename' { (By-Name $main 'watcher-renamed.txt') -and -not (By-Name $main 'watcher-added.txt') } 60
    Remove-Item -LiteralPath $renamedPath
    $null = Wait-For 'watcher removes deleted file' { Assert-Page 'Page 0 / 0' } 60
    Invoke-Control (By-Id $main ClearQueryButton)
    $null = Wait-For 'original results retained' { Assert-Page 'Page 1 / 2' }
    Write-Host 'Installed local watcher create, modify, rename and delete passed.'

    Invoke-Control (By-Id $main IndexSettingsButton)
    $settings = Wait-For 'Index settings dialog for confirmed Clear' { Find-AppElement 'Index settings' }
    Select-Tab (By-Name $settings 'Status')
    Invoke-Control (By-Name $settings 'Clear index')
    $yes = Wait-For 'clear confirmation Yes' { Find-AppControl 'Yes' ([System.Windows.Automation.ControlType]::Button) }
    Invoke-Control $yes
    $null = Wait-For 'confirmed Clear pauses and finishes' { (Read-Settings).Paused -eq $true -and (By-Name $settings 'Resume').Current.IsEnabled }
    Invoke-Control (By-Name $settings 'Close')
    $null = Wait-For 'confirmed clear removes indexed results' { Assert-Page 'Page 0 / 0' }
    if (@(Get-ChildItem -LiteralPath $fixture -Filter 'document-*.txt' -File).Count -ne 51) { throw 'Clear changed the original source files.' }
    Invoke-Control (By-Id $main IndexSettingsButton)
    $settings = Wait-For 'Index settings dialog for Resume' { Find-AppElement 'Index settings' }
    Select-Tab (By-Name $settings 'Status')
    Invoke-Control (By-Name $settings 'Resume')
    $null = Wait-For 'Resume persisted and finishes its settings action' { (Read-Settings).Paused -eq $false -and (By-Name $settings 'Pause').Current.IsEnabled }
    Invoke-Control (By-Name $settings 'Close')
    $null = Wait-For 'Resume restores original result set' { Assert-Page 'Page 1 / 2' } 90
    Write-Host 'Confirmed Clear removes only index, preserves 51 originals and Resume restores search results.'


    # Exercise every page-size option and both end controls, with actual persisted choices.
    foreach ($size in @(250,500,1000)) {
        Select-Combo $main PageSize ([string]$size) ([array]::IndexOf(@(50,100,250,500,1000),$size))
        $null = Wait-For "page size $size" { (Read-Settings).PageSize -eq $size -and (Assert-Page 'Page 1 / 1') }
    }
    Select-Combo $main PageSize '50' 0
    $null = Wait-For 'restore 50 per page' { (Read-Settings).PageSize -eq 50 -and (Assert-Page 'Page 1 / 2') }
    Invoke-Control (By-Id $main LastPage)
    $null = Wait-For 'Last page shortcut' { Assert-Page 'Page 2 / 2' }
    Invoke-Control (By-Id $main PreviousPage)
    $null = Wait-For 'Previous page' { Assert-Page 'Page 1 / 2' }
    Write-Host 'All five page sizes and all four navigation actions passed.'

    # A deliberately unordered set of distinct timestamps and file sizes.
    $sortFiles = @(
        @{Name='sortfixture-a.txt'; Text='sortfixture'; Modified='2023-06-03T12:00:00Z'},
        @{Name='sortfixture-b.txt'; Text=('sortfixture ' + ('b' * 100)); Modified='2023-06-01T12:00:00Z'},
        @{Name='sortfixture-c.txt'; Text=('sortfixture ' + ('c' * 200)); Modified='2023-06-02T12:00:00Z'}
    )
    foreach ($file in $sortFiles) {
        $filePath = Join-Path $fixture $file.Name
        Set-Content -LiteralPath $filePath -Value $file.Text
        [IO.File]::SetLastWriteTimeUtc($filePath, [DateTimeOffset]::Parse($file.Modified).UtcDateTime)
    }
    Set-Text (By-Id $main SearchBox) 'sortfixture'
    $null = Wait-For 'three sort fixtures' { (By-Id $main ResultCount).Current.Name -eq '1–3 of 3 results' } 60
    $cases = @(
        @{Index=1; Label='Modified — newest'; First='sortfixture-a.txt'},
        @{Index=2; Label='Modified — oldest'; First='sortfixture-b.txt'},
        @{Index=3; Label='Name — A to Z'; First='sortfixture-a.txt'},
        @{Index=4; Label='Name — Z to A'; First='sortfixture-c.txt'},
        @{Index=5; Label='Size — largest'; First='sortfixture-c.txt'},
        @{Index=6; Label='Size — smallest'; First='sortfixture-a.txt'},
        @{Index=7; Label='Path — A to Z'; First='sortfixture-a.txt'},
        @{Index=8; Label='Path — Z to A'; First='sortfixture-c.txt'}
    )
    $listItem = [System.Windows.Automation.PropertyCondition]::new($ae::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
    foreach ($case in $cases) {
        Select-Combo $main SortBox $case.Label $case.Index
        $null = Wait-For "first $($case.Label) result is $($case.First)" {
            $first = (By-Id $main ResultsList).FindFirst($scope, $listItem)
            $first -and $first.Current.Name -eq $case.First
        }
        Write-Host "Sort verified: $($case.Label) -> $($case.First)"
    }
    Select-Combo $main SortBox 'Relevance' 0
    $null = Wait-For 'relevance returns three matches' { (By-Id $main ResultCount).Current.Name -eq '1–3 of 3 results' }

    # Check narrow layout with the OS resize pattern and visible control bounds.
    function Resize-CheckedWindow($Window, [int]$Width, [int]$Height, [string]$Name) {
        $transform = Require-Pattern $Window ([System.Windows.Automation.TransformPattern]::Pattern)
        if (-not $transform.Current.CanResize) { throw "Cannot resize $Name." }
        $transform.Resize($Width,$Height)
        $null = Wait-For "$Name resize to $Width x $Height" {
            $r = $Window.Current.BoundingRectangle
            [Math]::Abs($r.Width - $Width) -le 25 -and [Math]::Abs($r.Height - $Height) -le 25
        }
        $r = $Window.Current.BoundingRectangle
        Write-Host "$Name actual bounds $($r.Width) x $($r.Height)."
    }
    function Assert-VisibleInside($Window, [scriptblock]$Lookup, [string]$Name) {
        # Resize returns before WPF finishes its asynchronous measure/arrange.
        # Re-resolve the actual element until the final arrangement is visible.
        $null = Wait-For "$Name visible inside resized window" {
            try {
                $control = & $Lookup
                if (-not $control -or $control.Current.IsOffscreen) { return $false }
                $outer = $Window.Current.BoundingRectangle
                $inner = $control.Current.BoundingRectangle
                $inner.Width -gt 0 -and $inner.Height -gt 0 -and
                    $inner.Left -ge ($outer.Left-1) -and $inner.Top -ge ($outer.Top-1) -and
                    $inner.Right -le ($outer.Right+1) -and $inner.Bottom -le ($outer.Bottom+1)
            } catch [System.Windows.Automation.ElementNotAvailableException] { $false }
        }
    }
    Resize-CheckedWindow $main 440 410 'Main minimum'
    foreach ($controlId in @('SearchBox','SortBox','ViewOptions','PageSize','FirstPage','PreviousPage','NextPage','LastPage','IndexSettingsButton','ResultsList')) {
        Assert-VisibleInside $main { By-Id $main $controlId } $controlId
    }
    Capture-Window $main 'light-minimum-main'
    Invoke-Control (By-Id $main IndexSettingsButton)
    $settings = Wait-For 'settings at minimum' { Find-AppElement 'Index settings' }
    Resize-CheckedWindow $settings 440 405 'Settings minimum'
    foreach ($label in @('Folders','Status','Preferences')) {
        Select-Tab (By-Name $settings $label)
        Assert-VisibleInside $settings { By-Name $settings $label } $label
        Assert-VisibleInside $settings { By-Name $settings 'Apply changes' } 'Apply changes'
        Assert-VisibleInside $settings { By-Name $settings 'Close' } 'Close'
        Capture-Window $settings ("light-minimum-" + $label.ToLowerInvariant())
    }
    Invoke-Control (By-Name $settings 'Close')
    $null = Wait-For 'minimum settings closed' { -not (Find-AppElement 'Index settings') }
    Resize-CheckedWindow $main 970 690 'Main default'
    Write-Host 'Main and settings minimum dimensions and required control bounds passed.'

    # Missing source should be reported instead of passing a stale path to the shell.
    Invoke-Control (By-Id $main IndexSettingsButton)
    $settings = Wait-For 'pause for unavailable file' { Find-AppElement 'Index settings' }
    Select-Tab (By-Name $settings 'Status')
    Invoke-Control (By-Name $settings 'Pause')
    $null = Wait-For 'pause before moving source' { (Read-Settings).Paused -eq $true -and (By-Name $settings 'Resume').Current.IsEnabled }
    Invoke-Control (By-Name $settings 'Close')
    $null = Wait-For 'settings closed before missing-file test' { -not (Find-AppElement 'Index settings') }
    Set-Text (By-Id $main SearchBox) 'uniqueuiword'
    $null = Wait-For 'indexed original is visible' { (Assert-Page 'Page 1 / 1') -and (By-Name $main 'document-050.txt') }
    (By-Id $main SearchBox).SetFocus()
    [System.Windows.Forms.SendKeys]::SendWait('{DOWN}')
    $null = Wait-For 'result selected before original unavailable' {
        $row = By-Name (By-Id $main ResultsList) 'document-050.txt'
        if ($row) { (Require-Pattern $row ([System.Windows.Automation.SelectionItemPattern]::Pattern)).Current.IsSelected }
    }
    $original = Join-Path $fixture 'document-050.txt'
    $temporary = Join-Path $fixture 'temporarily-unavailable-original.txt'
    Move-Item -LiteralPath $original -Destination $temporary
    try {
        [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
        $null = Wait-For 'unavailable file feedback' { Find-AppControl 'The file is currently unavailable.' ([System.Windows.Automation.ControlType]::Text) }
        $ok = Wait-For 'unavailable file confirmation' { Find-AppControl 'OK' ([System.Windows.Automation.ControlType]::Button) }
        Invoke-Control $ok
        $null = Wait-For 'missing-file dialog dismissed' { -not (Find-AppControl 'The file is currently unavailable.' ([System.Windows.Automation.ControlType]::Text)) }
        Write-Host 'Unavailable original produces understandable feedback without launching an absent file.'
    } finally {
        Move-Item -LiteralPath $temporary -Destination $original
    }

    Invoke-Control (By-Id $main IndexSettingsButton)
    $settings = Wait-For 'settings for rebuild' { Find-AppElement 'Index settings' }
    Select-Tab (By-Name $settings 'Status')
    Invoke-Control (By-Name $settings 'Rebuild index')
    $no = Wait-For 'cancel rebuild' { Find-AppControl 'No' ([System.Windows.Automation.ControlType]::Button) }
    Invoke-Control $no
    $null = Wait-For 'declined rebuild keeps paused state' { (Read-Settings).Paused -eq $true -and -not (Find-AppControl 'No' ([System.Windows.Automation.ControlType]::Button)) }
    Invoke-Control (By-Name $settings 'Rebuild index')
    $yes = Wait-For 'confirm rebuild' { Find-AppControl 'Yes' ([System.Windows.Automation.ControlType]::Button) }
    Invoke-Control $yes
    $null = Wait-For 'rebuild resumes indexing' { (Read-Settings).Paused -eq $false -and (By-Name $settings 'Pause').Current.IsEnabled }
    Invoke-Control (By-Name $settings 'Close')
    $null = Wait-For 'rebuild restores unique match' { (Assert-Page 'Page 1 / 1') -and (By-Name $main 'document-050.txt') } 90
    if (@(Get-ChildItem -LiteralPath $fixture -Filter '*.txt' -File).Count -ne 54) { throw 'Rebuild changed original files.' }
    Write-Host 'Declined and confirmed Rebuild preserve original files and restore indexed results.'

    # Set a non-default combination and verify actual restart behavior.
    Select-Combo $main PageSize '250' 2
    $null = Wait-For 'nondefault page size saved' { (Read-Settings).PageSize -eq 250 }
    Invoke-Control (By-Id $main ViewOptions)
    Toggle-ResultOption 'File path'
    $null = Wait-For 'path disabled persisted' { -not (Read-Settings).ShowPath }
    [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
    if (-not $process.CloseMainWindow() -or -not $process.WaitForExit(15000) -or $process.ExitCode -ne 0) { throw 'First close before restart failed.' }
    $process = Start-Process $Application -PassThru
    $main = Wait-For 'main window after restart' {
        $process.Refresh()
        if ($process.HasExited) { throw 'Application exited on restart.' }
        if ($process.MainWindowHandle -ne 0) { $ae::FromHandle($process.MainWindowHandle) }
    }
    $null = Wait-For 'restart restores 54 results and up-to-date status' {
        (By-Id $main ResultCount).Current.Name -eq '1–54 of 54 results' -and (By-Name $main 'Index up to date')
    } 90
    $null = Wait-For 'restart settings remain persisted' { $s = Read-Settings; $s.PageSize -eq 250 -and -not $s.ShowPath -and $s.Theme -eq 'Light' }
    Set-Text (By-Id $main SearchBox) 'uniqueuiword'
    $null = Wait-For 'restart hides file path in visible unique result' { (Assert-Page 'Page 1 / 1') -and (By-Name $main 'document-050.txt') -and -not (By-Name $main $original) }
    Capture-Window $main 'light-restart-hidden-path'
    Write-Host 'Restart retains 250 page size, Light theme and hidden path; all 54 indexed results and up-to-date status available.'

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
