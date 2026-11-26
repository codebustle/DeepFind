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

$workspace = Join-Path $env:RUNNER_TEMP ('deepfind-formats-' + [Guid]::NewGuid().ToString('N'))
$fixture = Join-Path $workspace 'sources'
$parent = Join-Path $workspace 'initial-index'
$newParent = Join-Path $workspace 'moved-index'
$exported = Join-Path $env:RUNNER_TEMP 'deepfind-ui-formats'
New-Item -ItemType Directory -Path $fixture,$parent,$newParent | Out-Null
$fixtures = @(Get-ChildItem -LiteralPath $exported -File)
if ($fixtures.Count -ne 8) { throw "Expected eight validated format fixtures, found $($fixtures.Count)." }
foreach ($file in $fixtures) { Copy-Item -LiteralPath $file.FullName -Destination $fixture }
$settingsPath = Join-Path $env:LOCALAPPDATA 'DeepFind\settings.json'
New-Item -ItemType Directory -Path (Split-Path $settingsPath) -Force | Out-Null
@{Folders=@(); IndexLocation=$parent; PageSize=250; Theme='System'} | ConvertTo-Json | Set-Content -LiteralPath $settingsPath -Encoding UTF8
. "$PSScriptRoot\WindowsUiHelpers.ps1"
Add-Type -AssemblyName System.Windows.Forms

function Assert-Count([int]$Count) {
    $label = (By-Id $script:main ResultCount).Current.Name
    if ($Count -eq 0) { $label -eq '0 results' }
    else { $label -eq "1–$Count of $Count results" }
}
function Open-Settings {
    Invoke-Control (By-Id $script:main IndexSettingsButton)
    Wait-For 'Index settings' { Find-AppElement 'Index settings' }
}
function Close-Settings($Window) {
    $null=Wait-For 'settings ready to close' { (By-Name $Window 'Apply changes').Current.IsEnabled }
    Invoke-Control (By-Name $Window 'Close')
    $null=Wait-For 'settings closed' { -not (Find-AppElement 'Index settings') }
}
function Select-Folder($Window,[string]$Path) {
    # Native Windows picker; navigate to a test-only folder via its address bar.
    $Window.SetFocus()
    [System.Windows.Forms.Clipboard]::SetText($Path)
    [System.Windows.Forms.SendKeys]::SendWait('%d')
    [System.Windows.Forms.SendKeys]::SendWait('^v')
    [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
    $button=Wait-For 'native Select Folder control' {
        $b=By-Name $Window 'Select Folder'
        if (-not $b) { $b=By-Name $Window 'Select folder' }
        if ($b -and $b.Current.IsEnabled) { $b }
    }
    Invoke-Control $button
}
function Find-Result([string]$Name) {
    By-Name (By-Id $script:main ResultsList) $Name
}
function Select-Result([string]$Name) {
    $item=Wait-For "result $Name" { Find-Result $Name }
    (Require-Pattern $item ([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
    $item.SetFocus()
    $item
}
function Right-ClickResult($Element) {
    if ($Element.Current.ProcessId -ne $script:process.Id -or $Element.Current.IsOffscreen) { throw 'Invalid target result.' }
    $r=$Element.Current.BoundingRectangle
    if ($r.Width -le 0 -or $r.Height -le 0) { throw 'Result not visible.' }
    if (-not [DeepFindTestPointer]::SetCursorPos([int]($r.X+$r.Width/2),[int]($r.Y+$r.Height/2))) { throw 'Pointer movement failed.' }
    [DeepFindTestPointer]::mouse_event(8,0,0,0,[UIntPtr]::Zero)
    [DeepFindTestPointer]::mouse_event(16,0,0,0,[UIntPtr]::Zero)
}
function Result-Menu([string]$Name) {
    Wait-For "context menu $Name" { Find-AppControl $Name ([System.Windows.Automation.ControlType]::MenuItem) }
}
function Start-Main {
    $script:process=Start-Process $Application -PassThru
    $script:main=Wait-For 'main window' {
        $script:process.Refresh()
        if ($script:process.HasExited) { throw "Application exited $($script:process.ExitCode)." }
        if ($script:process.MainWindowHandle -ne 0) { $ae::FromHandle($script:process.MainWindowHandle) }
    }
}
function Close-Main {
    if (-not $script:process.CloseMainWindow() -or -not $script:process.WaitForExit(15000) -or $script:process.ExitCode -ne 0) {
        throw 'DeepFind did not close cleanly.'
    }
}
$process=$null
$main=$null
try {
    Start-Main
    $null=Wait-For 'first run no folders' { (Assert-Count 0) -and (By-Name $main 'Add a folder to start searching your files.') }
    Capture-Window $main 'system-first-run'
    Invoke-Control (By-Name $main 'Add folder')
    $settings=Wait-For 'first run folder settings' { Find-AppElement 'Index settings' }
    Select-Tab (By-Name $settings 'Folders')
    Invoke-Control (By-Name $settings 'Add folder')
    $picker=Wait-For 'native folder picker' { Find-AppElement 'Choose folders to index' }
    Invoke-Control (By-Name $picker 'Cancel')
    $null=Wait-For 'picker cancelled' { -not (Find-AppElement 'Choose folders to index') }
    if (@((Read-Settings).Folders).Count -ne 0) { throw 'Cancelling folder picker changed committed roots.' }
    Invoke-Control (By-Name $settings 'Add folder')
    $picker=Wait-For 'native picker reopened' { Find-AppElement 'Choose folders to index' }
    Select-Folder $picker $fixture
    $null=Wait-For 'folder staged' { (By-Name $settings $fixture) -and -not (Find-AppElement 'Choose folders to index') }
    if (@((Read-Settings).Folders).Count -ne 0) { throw 'Staged folder was committed before Apply.' }
    Invoke-Control (By-Name $settings 'Apply changes')
    $null=Wait-For 'folder applied' { @((Read-Settings).Folders) -contains $fixture }
    Close-Settings $settings
    $null=Wait-For 'eight documents indexed' { (Assert-Count 8) -and (By-Name $main 'Index up to date') } 90
    Write-Host 'First run, actual folder picker cancel/add, Apply and initial search passed.'

    $cases=@(
        @{Term='harbour'; Name='notes.txt'; Format='TXT'},
        @{Term='winch'; Name='maintenance.md'; Format='MD'},
        @{Term='draught'; Name='voyage.csv'; Format='CSV'},
        @{Term='anchors'; Name='procedure.html'; Format='HTML'},
        @{Term='towing'; Name='manual.pdf'; Format='PDF'; Location='Page 2'},
        @{Term='waterline'; Name='survey.docx'; Format='DOCX'},
        @{Term='ballast'; Name='metrics.xlsx'; Format='XLSX'; Location='Inspection!B2'},
        @{Term='docking'; Name='slides.pptx'; Format='PPTX'; Location='Slide 1'}
    )
    foreach ($case in $cases) {
        Set-Text (By-Id $main SearchBox) $case.Term
        $row=Wait-For "$($case.Format) result and badge" {
            $r=Find-Result $case.Name
            if ((Assert-Count 1) -and $r -and (By-Name $r $case.Format)) { $r }
        }
        if ($case.Location) { $null=Wait-For "$($case.Format) location" { By-Name $row $case.Location } }
        Capture-Window $main ("system-format-" + $case.Format.ToLowerInvariant())
        Write-Host "Installed format verified: $($case.Format) $($case.Name) location=$($case.Location)"
    }
    Invoke-Control (By-Id $main ClearQueryButton)
    $null=Wait-For 'all eight' { Assert-Count 8 }
    foreach ($filter in @(@{Label='Documents'; Count=6},@{Label='PDF'; Count=1},@{Label='Excel'; Count=2},@{Label='All files'; Count=8})) {
        Click-TestControl (Find-AppControl $filter.Label ([System.Windows.Automation.ControlType]::RadioButton))
        $null=Wait-For "filter $($filter.Label)" { Assert-Count $filter.Count }
    }
    foreach ($query in @(@{Text='ext:pdf "emergency towing"'; Count=1},@{Text='filename:notes'; Count=1},
        @{Text='waterline OR ballast AND valve'; Count=2},@{Text='harbour arrival'; Count=1},@{Text='noactualmatch'; Count=0})) {
        Set-Text (By-Id $main SearchBox) $query.Text
        $null=Wait-For "query $($query.Text)" { Assert-Count $query.Count }
    }
    Set-Text (By-Id $main SearchBox) 'ext:pdf OR'
    $null=Wait-For 'invalid grammar feedback' { By-Name $main 'OR needs a search term on both sides.' }
    $search=By-Id $main SearchBox
    $search.SetFocus()
    [System.Windows.Forms.SendKeys]::SendWait('^a')
    [System.Windows.Forms.Clipboard]::SetText(('z' * 600))
    [System.Windows.Forms.SendKeys]::SendWait('^v')
    $null=Wait-For 'keyboard paste capped at 512' {
        (Require-Pattern $search ([System.Windows.Automation.ValuePattern]::Pattern)).Current.Value.Length -eq 512
    }
    Invoke-Control (By-Id $main ClearQueryButton)
    $null=Wait-For 'clear restores all results after long input' { Assert-Count 8 }
    Write-Host 'All eight format badges/locations, four file filters, grammar, no matches and long-input boundary passed.'

    # Focus shortcuts and right-click a result other than the selected item.
    Set-Text (By-Id $main SearchBox) 'filename:notes OR filename:maintenance'
    $null=Wait-For 'two context-menu targets' { (Assert-Count 2) -and (Find-Result 'notes.txt') -and (Find-Result 'maintenance.md') }
    $selected=Select-Result 'notes.txt'
    [System.Windows.Forms.SendKeys]::SendWait('^f')
    $null=Wait-For 'Ctrl+F focuses search' { (By-Id $main SearchBox).Current.HasKeyboardFocus }
    $selected=Select-Result 'notes.txt'
    [System.Windows.Forms.SendKeys]::SendWait('^l')
    $null=Wait-For 'Ctrl+L focuses search' { (By-Id $main SearchBox).Current.HasKeyboardFocus }
    $selected=Select-Result 'notes.txt'
    Right-ClickResult (Find-Result 'maintenance.md')
    Invoke-Control (Result-Menu 'Copy path')
    $null=Wait-For 'right-click copies path of actual clicked row' { (Get-Clipboard -Raw) -eq (Join-Path $fixture 'maintenance.md') }
    Right-ClickResult (Find-Result 'notes.txt')
    Invoke-Control (Result-Menu 'Preview')
    $preview=Wait-For 'context menu preview' { Find-AppElement 'notes.txt' }
    $null=Wait-For 'correct preview source' { By-Name $preview (Join-Path $fixture 'notes.txt') }
    [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
    $null=Wait-For 'Esc closes preview and returns results' { -not (Find-AppElement 'notes.txt') }
    Write-Host 'Keyboard focus and nonselected-row context copy/preview passed.'

    # Verify Windows actually opens the selected synthetic .txt and reveals the exact path.
    $notesPath=Join-Path $fixture 'notes.txt'
    $beforeNotepad=@(Get-Process notepad -ErrorAction SilentlyContinue | ForEach-Object Id)
    Right-ClickResult (Find-Result 'notes.txt')
    Invoke-Control (Result-Menu 'Open file')
    $opened=Wait-For 'new Notepad window for test note' {
        Get-Process notepad -ErrorAction SilentlyContinue |
            Where-Object { $beforeNotepad -notcontains $_.Id -and $_.MainWindowTitle -match 'notes' } |
            Select-Object -First 1
    }
    if (-not $opened.CloseMainWindow()) { throw 'Could not close the newly opened test note.' }
    $null=$opened.WaitForExit(15000)
    (By-Id $main SearchBox).SetFocus()
    Right-ClickResult (Find-Result 'notes.txt')
    Invoke-Control (Result-Menu 'Show in Explorer')
    $shell=New-Object -ComObject Shell.Application
    $null=Wait-For 'Explorer selected precise synthetic file' {
        foreach ($window in @($shell.Windows())) {
            try {
                foreach ($item in @($window.Document.SelectedItems())) {
                    if ($item.Path -eq $notesPath) { return $true }
                }
            } catch { }
        }
    }
    (By-Id $main SearchBox).SetFocus()
    Write-Host 'Windows Open file and Show in Explorer selected the exact test file.'

    # Validate preference boundaries and real staged removal.
    $settings=Open-Settings
    Select-Tab (By-Name $settings 'Folders')
    $row=By-Name $settings $fixture
    Select-Tab $row
    Invoke-Control (By-Name $settings 'Remove folder')
    $null=Wait-For 'staged removal' { -not (By-Name $settings $fixture) }
    Close-Settings $settings
    if (@((Read-Settings).Folders) -notcontains $fixture) { throw 'Unsaved Close committed folder removal.' }

    $settings=Open-Settings
    Select-Tab (By-Name $settings 'Preferences')
    Set-Text (By-Id $settings LocalHours) '0'
    Invoke-Control (By-Name $settings 'Apply changes')
    $null=Wait-For 'invalid local interval' { By-Name $settings 'Enter a file size from 1–1024 MB and check intervals from 1–720 hours.' }
    Set-Text (By-Id $settings LocalHours) '3'
    Set-Text (By-Id $settings NetworkHours) '721'
    Invoke-Control (By-Name $settings 'Apply changes')
    $null=Wait-For 'invalid network interval' { By-Name $settings 'Enter a file size from 1–1024 MB and check intervals from 1–720 hours.' }
    Set-Text (By-Id $settings NetworkHours) '12'
    Set-Text (By-Id $settings IndexLocation) (Join-Path $workspace 'missing')
    Invoke-Control (By-Name $settings 'Apply changes')
    $null=Wait-For 'invalid custom index parent' { By-Name $settings 'Choose an existing index storage parent folder or leave the field empty to use the default.' }
    Set-Text (By-Id $settings IndexLocation) $parent
    Set-Text (By-Id $settings Exclusions) 'notes.txt'
    Set-Text (By-Id $settings MaxSize) '1'
    $include=By-Id $settings IncludeOther
    (Require-Pattern $include ([System.Windows.Automation.TogglePattern]::Pattern)).Toggle()
    Invoke-Control (By-Name $settings 'Apply changes')
    $null=Wait-For 'valid preferences persisted' {
        $s=Read-Settings
        $s.LocalHours -eq 3 -and $s.NetworkHours -eq 12 -and $s.MaxMegabytes -eq 1 -and $s.IncludeOtherFiles -and @($s.Exclusions) -contains 'notes.txt'
    }
    Close-Settings $settings
    Set-Text (By-Id $main SearchBox) 'harbour'
    $null=Wait-For 'excluded original no longer indexed' { Assert-Count 0 }
    if (-not (Test-Path -LiteralPath (Join-Path $fixture 'notes.txt'))) { throw 'Exclusion deleted original.' }
    [IO.File]::WriteAllText((Join-Path $fixture 'unsupportedfixture.bin'),'notindexablecontent')
    Set-Text (By-Id $main SearchBox) 'unsupportedfixture'
    $null=Wait-For 'unsupported type name indexed with generic label' {
        $r=Find-Result 'unsupportedfixture.bin'
        (Assert-Count 1) -and $r -and (By-Name $r 'BIN')
    } 90
    Capture-Window $main 'system-format-unsupported'
    Set-Text (By-Id $main SearchBox) 'notindexablecontent'
    $null=Wait-For 'unsupported type content is not extracted' { Assert-Count 0 }
    Write-Host 'Unsaved folder removal, invalid intervals/path, valid preferences, exclusion and optional generic file fallback passed.'

    $settings=Open-Settings
    Select-Tab (By-Name $settings 'Preferences')
    Set-Text (By-Id $settings IndexLocation) $newParent
    Invoke-Control (By-Name $settings 'Apply changes')
    $null=Wait-For 'custom index restart required' { By-Name $settings 'Settings saved. Restart DeepFind to use the new index location.' }
    Close-Settings $settings
    Close-Main
    if (-not (Test-Path -LiteralPath (Join-Path $parent 'DeepFind.Index\Search'))) { throw 'Previous index was removed silently.' }
    Start-Main
    $null=Wait-For 'new custom index created and populated after restart' {
        (Test-Path -LiteralPath (Join-Path $newParent 'DeepFind.Index\tracking.db')) -and (By-Name $main 'Index up to date')
    } 90
    Set-Text (By-Id $main SearchBox) 'ballast'
    $null=Wait-For 'new index searches real document' { (Assert-Count 1) -and (Find-Result 'metrics.xlsx') }
    $settings=Open-Settings
    Select-Tab (By-Name $settings 'Folders')
    Select-Tab (By-Name $settings $fixture)
    Invoke-Control (By-Name $settings 'Remove folder')
    Invoke-Control (By-Name $settings 'Apply changes')
    $null=Wait-For 'folder removal committed' { @((Read-Settings).Folders).Count -eq 0 }
    Close-Settings $settings
    $null=Wait-For 'removal prunes results' { Assert-Count 0 }
    if (@(Get-ChildItem -LiteralPath $fixture -File).Count -ne 9) { throw 'Settings changed original files.' }
    Write-Host 'Custom index migration after restart, old data retained, confirmed folder removal and source preservation passed.'
    Close-Main
} catch {
    Write-Host "Additional UI failure: $($_.ScriptStackTrace)"
    if ($main -and $process -and -not $process.HasExited) { try { Capture-Window $main 'additional-ui-failure' } catch { Write-Host "Screenshot unavailable: $_" } }
    throw
} finally {
    if ($process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force }
}
