param([Parameter(Mandatory=$true)][string]$Msi)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or -not $env:RUNNER_TEMP) {
    throw 'This test requires a disposable GitHub Actions Windows runner.'
}
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$ae = [System.Windows.Automation.AutomationElement]
$scope = [System.Windows.Automation.TreeScope]::Descendants
$root = $ae::RootElement
$desktopPath = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'DeepFind.lnk'
$exePath = Join-Path $env:LOCALAPPDATA 'Programs\DeepFind\DeepFind.App.exe'
function Find-Dialog {
    $windows = $root.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($window in $windows) {
        if ($window.Current.Name -notmatch 'DeepFind') { continue }
        $p = Get-Process -Id $window.Current.ProcessId -ErrorAction SilentlyContinue
        if ($p -and $p.ProcessName -eq 'msiexec') { return $window }
    }
}
function Find-Control($Dialog, [string]$Name, $Type) {
    $conditions = [System.Windows.Automation.Condition[]]@(
        [System.Windows.Automation.PropertyCondition]::new($ae::NameProperty, $Name),
        [System.Windows.Automation.PropertyCondition]::new($ae::ControlTypeProperty, $Type)
    )
    $Dialog.FindFirst($scope, [System.Windows.Automation.AndCondition]::new($conditions))
}
function Invoke-DialogButton($Control) {
    $Control.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}
function Set-Checked($Control, [bool]$Checked) {
    $toggle = $Control.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    $state = $toggle.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On
    if ($state -ne $Checked) { $toggle.Toggle() }
}
foreach ($checked in @($true, $false)) {
    if (Test-Path $desktopPath) { throw 'Expected no preexisting desktop shortcut.' }
    $log = Join-Path $env:RUNNER_TEMP "deepfind-interactive-$checked.log"
    $arguments = '/i "{0}" /qf /norestart /log "{1}"' -f $Msi, $log
    $install = Start-Process msiexec.exe -ArgumentList $arguments -PassThru
    try {
        $deadline = (Get-Date).AddSeconds(120)
        $finished = $false
        $seenState = ''
        while ((Get-Date) -lt $deadline) {
            $install.Refresh()
            if ($install.HasExited) { throw "Setup ended before Finish at exit code $($install.ExitCode)." }
            $dialog = Find-Dialog
            if (-not $dialog) { Start-Sleep -Milliseconds 300; continue }
            $buttonType = [System.Windows.Automation.ControlType]::Button
            $checkType = [System.Windows.Automation.ControlType]::CheckBox
            $finish = Find-Control $dialog 'Finish' $buttonType
            if ($finish -and $finish.Current.IsEnabled) {
                $checkbox = Find-Control $dialog 'Create a desktop shortcut' $checkType
                if (-not $checkbox) { throw 'Finish screen missing Desktop shortcut checkbox.' }
                Set-Checked $checkbox $checked
                Invoke-DialogButton $finish
                $finished = $true
                break
            }
            # The state fingerprint prevents double-clicking stale Next buttons.
            $controls = $dialog.FindAll($scope, [System.Windows.Automation.Condition]::TrueCondition)
            $state = ($controls | ForEach-Object { $_.Current.Name }) -join '|'
            if ($state -eq $seenState) { Start-Sleep -Milliseconds 300; continue }
            $seenState = $state
            foreach ($control in $controls) {
                if ($control.Current.ControlType -eq $checkType -and $control.Current.Name -match 'accept.*(terms|license)') {
                    Set-Checked $control $true
                }
            }
            $installButton = Find-Control $dialog 'Install' $buttonType
            $nextButton = Find-Control $dialog 'Next' $buttonType
            if (-not $nextButton) { $nextButton = Find-Control $dialog 'Next >' $buttonType }
            if ($installButton -and $installButton.Current.IsEnabled) { Invoke-DialogButton $installButton }
            elseif ($nextButton -and $nextButton.Current.IsEnabled) { Invoke-DialogButton $nextButton }
            else { $seenState = '' }
            Start-Sleep -Milliseconds 300
        }
        if (-not $finished) { throw "Setup never reached Finish. Last state: $seenState" }
        if (-not $install.WaitForExit(60000) -or $install.ExitCode -ne 0) {
            throw 'Interactive installation did not exit successfully.'
        }
        if ((Test-Path $desktopPath) -ne $checked) { throw "Desktop shortcut does not match Finish checkbox: $checked." }
        if ($checked) {
            $shell = New-Object -ComObject WScript.Shell
            if ($shell.CreateShortcut($desktopPath).TargetPath -ne $exePath) { throw 'Interactive Desktop link has wrong target.' }
        }
        Write-Host "Interactive setup Finish checkbox checked=$checked passed."
    } finally {
        if (-not $install.HasExited) {
            $dialog = Find-Dialog
            if ($dialog) {
                $cancel = Find-Control $dialog 'Cancel' ([System.Windows.Automation.ControlType]::Button)
                if ($cancel -and $cancel.Current.IsEnabled) { Invoke-DialogButton $cancel }
            }
            if (-not $install.WaitForExit(15000)) { Stop-Process -Id $install.Id -Force }
        }
        if (Test-Path $exePath) {
            $uninstallLog = Join-Path $env:RUNNER_TEMP "deepfind-interactive-uninstall-$checked.log"
            $uninstall = Start-Process msiexec.exe -ArgumentList ('/x "{0}" /quiet /norestart /log "{1}"' -f $Msi,$uninstallLog) -Wait -PassThru
            if ($uninstall.ExitCode -ne 0) { throw "Interactive test cleanup failed: $($uninstall.ExitCode)." }
        }
    }
    if (Test-Path $desktopPath) { throw 'Interactive Desktop link remained after uninstall.' }
}
