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

$workspace=Join-Path $env:RUNNER_TEMP ('deepfind-network-' + [Guid]::NewGuid().ToString('N'))
$source=Join-Path $workspace 'shared-source'
$child=Join-Path $source 'child'
$parent=Join-Path $workspace 'index'
New-Item -ItemType Directory -Path $child,$parent | Out-Null
$first=Join-Path $source 'network-first.txt'
$second=Join-Path $child 'network-second.txt'
[IO.File]::WriteAllText($first,'networkneedle originalnetworkword')
[IO.File]::WriteAllText($second,'networkneedle')
$shareName='DeepFindQA_' + [Guid]::NewGuid().ToString('N')
$unc='\\localhost\' + $shareName
$uncChild=$unc + '\child'
$account="$env:USERDOMAIN\$env:USERNAME"
$settingsPath=Join-Path $env:LOCALAPPDATA 'DeepFind\settings.json'
New-Item -ItemType Directory -Path (Split-Path $settingsPath) -Force | Out-Null
. "$PSScriptRoot\WindowsUiHelpers.ps1"
function New-TestShare {
    if (Get-SmbShare -Name $shareName -ErrorAction SilentlyContinue) { throw 'Unexpected test-share name collision.' }
    New-SmbShare -Name $shareName -Path $source -FullAccess $account | Out-Null
    $s=Get-SmbShare -Name $shareName
    if ($s.Path -ne $source -or -not (Test-Path -LiteralPath $unc)) { throw 'Test-local SMB share is not accessible.' }
}
function Remove-TestShare {
    $s=Get-SmbShare -Name $shareName -ErrorAction SilentlyContinue
    if ($s) {
        if ($s.Path -ne $source) { throw 'Unexpected path for the disposable SMB share.' }
        Remove-SmbShare -Name $shareName -Force -Confirm:$false
    }
}
function Start-Main {
    $script:process=Start-Process $Application -PassThru
    $script:main=Wait-For 'main window' {
        $script:process.Refresh()
        if ($script:process.HasExited) { throw "App exited $($script:process.ExitCode)." }
        if ($script:process.MainWindowHandle -ne 0) { $ae::FromHandle($script:process.MainWindowHandle) }
    }
}
function Close-Main {
    if (-not $script:process.CloseMainWindow() -or -not $script:process.WaitForExit(15000) -or $script:process.ExitCode -ne 0) { throw 'Application did not close cleanly.' }
}
function Open-Status {
    Invoke-Control (By-Id $script:main IndexSettingsButton)
    $dialog=Wait-For 'Index settings' { Find-AppElement 'Index settings' }
    Select-Tab (By-Name $dialog 'Status')
    $dialog
}
function Close-Status($Dialog) {
    $null=Wait-For 'settings ready to close' { (By-Name $Dialog 'Apply changes').Current.IsEnabled }
    Invoke-Control (By-Name $Dialog 'Close')
    $null=Wait-For 'settings closed' { -not (Find-AppElement 'Index settings') }
}
function Assert-Count([int]$Count) {
    $label=(By-Id $script:main ResultCount).Current.Name
    if ($Count -eq 0) { $label -eq '0 results' }
    else { $label -eq "1–$Count of $Count results" }
}
$process=$null
$main=$null
try {
    New-TestShare
    @{Folders=@($unc,$uncChild); IndexLocation=$parent; PageSize=250; Theme='Dark'} |
        ConvertTo-Json | Set-Content -LiteralPath $settingsPath -Encoding UTF8
    Start-Main
    $null=Wait-For 'overlapping UNC roots contain exactly two distinct results' { (Assert-Count 2) -and (By-Name $main 'Index up to date') } 90
    Set-Text (By-Id $main SearchBox) 'networkneedle'
    $null=Wait-For 'UNC content matches both distinct source files' { (Assert-Count 2) -and (By-Name $main 'network-first.txt') -and (By-Name $main 'network-second.txt') }
    Capture-Window $main 'network-online'
    Write-Host 'Actual loopback SMB share and overlapping roots search without duplicates.'

    Remove-TestShare
    $null=Wait-For 'actual UNC share is disconnected' { -not (Test-Path -LiteralPath $unc) }
    $settings=Open-Status
    Invoke-Control (By-Name $settings 'Retry selected file')
    $null=Wait-For 'retry without selected notice' { By-Name $settings 'Select a file notice to retry.' }
    Invoke-Control (By-Name $settings 'Update now')
    $null=Wait-For 'SMB unavailable notice' { By-Name $settings 'Source is unavailable.' } 90
    Capture-Window $settings 'network-offline-notices'
    Close-Status $settings
    $null=Wait-For 'offline cached results remain accessible' { (Assert-Count 2) -and (By-Name $main 'Source unavailable') }
    Capture-Window $main 'network-offline-results'
    Write-Host 'Disconnected actual SMB source preserves committed results and displays unavailable state.'

    [IO.File]::WriteAllText($first,'networkneedle reconnectnetworkword')
    New-TestShare
    $settings=Open-Status
    $notices=By-Name $settings 'Index notices'
    $listItem=[System.Windows.Automation.PropertyCondition]::new($ae::ControlTypeProperty,[System.Windows.Automation.ControlType]::ListItem)
    $item=$notices.FindFirst($scope,$listItem)
    if (-not $item) { throw 'No selected retry notice available.' }
    (Require-Pattern $item ([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
    Invoke-Control (By-Name $settings 'Retry selected file')
    Close-Status $settings
    $null=Wait-For 'reconnect incremental reconciliation' { By-Name $main 'Index up to date' } 90
    Set-Text (By-Id $main SearchBox) 'reconnectnetworkword'
    $null=Wait-For 'reconnected changed content searchable' { (Assert-Count 1) -and (By-Name $main 'network-first.txt') }
    Set-Text (By-Id $main SearchBox) 'originalnetworkword'
    $null=Wait-For 'old network content removed' { Assert-Count 0 }
    Write-Host 'Reconnect and Retry selected file reconcile changed source contents.'

    # With actual paused settings, add a file and restart without extracting it.
    $settings=Open-Status
    Invoke-Control (By-Name $settings 'Pause')
    $null=Wait-For 'pause persisted and ready' { (Read-Settings).Paused -eq $true -and (By-Name $settings 'Resume').Current.IsEnabled }
    Close-Status $settings
    $third=Join-Path $source 'network-paused.txt'
    [IO.File]::WriteAllText($third,'networkneedle pausednetworkword')
    Close-Main
    Start-Main
    $null=Wait-For 'restart stays paused' { (Read-Settings).Paused -eq $true -and (By-Name $main 'Index paused') }
    Set-Text (By-Id $main SearchBox) 'pausednetworkword'
    $null=Wait-For 'paused new file not indexed' { Assert-Count 0 }
    $settings=Open-Status
    Invoke-Control (By-Name $settings 'Resume')
    $null=Wait-For 'resume persisted' { (Read-Settings).Paused -eq $false -and (By-Name $settings 'Pause').Current.IsEnabled }
    Close-Status $settings
    $null=Wait-For 'resume finds previously unindexed file' { (Assert-Count 1) -and (By-Name $main 'network-paused.txt') } 90
    if (@(Get-ChildItem -LiteralPath $source -File -Recurse).Count -ne 3) { throw 'Source file count changed.' }
    Write-Host 'Paused restart does not extract new source files until Resume.'
    Close-Main

    # Mutate only the disposable sources while the application is not running.
    [IO.File]::WriteAllText($first,'networkneedle closedchangedword')
    $renamed=Join-Path $child 'network-renamed.txt'
    Move-Item -LiteralPath $second -Destination $renamed
    Remove-Item -LiteralPath $third
    $newFile=Join-Path $source 'network-added.txt'
    [IO.File]::WriteAllText($newFile,'networkneedle closedaddedword')
    Start-Main
    $null=Wait-For 'closed changes reconcile to three distinct files' {
        (Assert-Count 3) -and (By-Name $main 'Index up to date') -and
        (By-Name $main 'network-first.txt') -and (By-Name $main 'network-renamed.txt') -and
        (By-Name $main 'network-added.txt') -and -not (By-Name $main 'network-second.txt') -and
        -not (By-Name $main 'network-paused.txt')
    } 90
    Set-Text (By-Id $main SearchBox) 'closedchangedword'
    $null=Wait-For 'closed modification searchable' { (Assert-Count 1) -and (By-Name $main 'network-first.txt') }
    Set-Text (By-Id $main SearchBox) 'closedaddedword'
    $null=Wait-For 'closed addition searchable' { (Assert-Count 1) -and (By-Name $main 'network-added.txt') }
    Set-Text (By-Id $main SearchBox) 'pausednetworkword'
    $null=Wait-For 'closed deletion removed cached content' { Assert-Count 0 }
    Set-Text (By-Id $main SearchBox) 'networkneedle'
    $null=Wait-For 'all three post-restart matches' { Assert-Count 3 }
    Write-Host 'Actual closed-app modification, addition, rename and deletion reconcile without duplicates.'

    # Apply a temporary deny on the exact disposable child directory, not the
    # share parent or any station-wide ACL. Restore it even if a check fails.
    $originalAcl=Get-Acl -LiteralPath $child
    try {
        $deniedAcl=Get-Acl -LiteralPath $child
        $rule=[System.Security.AccessControl.FileSystemAccessRule]::new(
            $account,[System.Security.AccessControl.FileSystemRights]::ListDirectory,
            [System.Security.AccessControl.AccessControlType]::Deny)
        $deniedAcl.AddAccessRule($rule)
        Set-Acl -LiteralPath $child -AclObject $deniedAcl
        $denied=$false
        try { $null=Get-ChildItem -LiteralPath $uncChild -ErrorAction Stop }
        catch [System.UnauthorizedAccessException] { $denied=$true }
        if (-not $denied) { throw 'Test did not establish actual source enumeration access denial.' }
        $settings=Open-Status
        Invoke-Control (By-Name $settings 'Update now')
        $null=Wait-For 'access-denied source notice' { By-Name $settings 'Source is unavailable.' } 90
        Capture-Window $settings 'network-access-denied-notices'
        Close-Status $settings
        $null=Wait-For 'access denied preserves all committed results' {
            (Assert-Count 3) -and (By-Name $main 'network-renamed.txt') -and (By-Name $main 'Source unavailable')
        }
        Capture-Window $main 'network-access-denied-results'
    } finally {
        Set-Acl -LiteralPath $child -AclObject $originalAcl
    }
    $settings=Open-Status
    Invoke-Control (By-Name $settings 'Update now')
    Close-Status $settings
    $null=Wait-For 'permission restored and all results available' {
        (Assert-Count 3) -and (By-Name $main 'Index up to date') -and -not (By-Name $main 'Source unavailable')
    } 90
    if (@(Get-ChildItem -LiteralPath $source -File -Recurse).Count -ne 3) { throw 'Permission test changed source file count.' }
    Write-Host 'Actual disposable UNC directory permission denial retains committed results; permission restoration reconciles notices.'
    Close-Main
} catch {
    Write-Host "Network acceptance failure: $($_.ScriptStackTrace)"
    if ($main -and $process -and -not $process.HasExited) { try { Capture-Window $main 'network-failure' } catch { Write-Host "Screenshot unavailable: $_" } }
    throw
} finally {
    if ($process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force }
    Remove-TestShare
}
