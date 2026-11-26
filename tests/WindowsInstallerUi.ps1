param([Parameter(Mandatory=$true)][string]$Msi)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or -not $env:RUNNER_TEMP) {
    throw 'This test requires a disposable GitHub Actions Windows runner.'
}
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class DeepFindInstallerPointer {
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT point);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] public static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int maxCount);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll", SetLastError=true)] private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam, uint flags, uint timeout, out UIntPtr result);
    public static int NativeCheckState(IntPtr hwnd, IntPtr dialog) {
        if (hwnd == IntPtr.Zero || GetAncestor(hwnd,2) != dialog)
            throw new InvalidOperationException("Checkbox handle is outside its verified installer dialog.");
        var cls = new StringBuilder(128);
        GetClassName(hwnd, cls, cls.Capacity);
        int style = GetWindowLong(hwnd, -16) & 15;
        if (!string.Equals(cls.ToString(),"Button",StringComparison.OrdinalIgnoreCase) ||
            (style != 2 && style != 3 && style != 4 && style != 5 && style != 6 && style != 9))
            throw new InvalidOperationException("Native checkbox is not a standard checkable Button: "+cls+" style="+style);
        UIntPtr result;
        if (SendMessageTimeout(hwnd, 0x00F0, UIntPtr.Zero, IntPtr.Zero, 2, 1000, out result) == IntPtr.Zero)
            throw new InvalidOperationException("Native checkbox state query timed out.");
        return checked((int)result.ToUInt64());
    }
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
}
'@
$ae = [System.Windows.Automation.AutomationElement]
$scope = [System.Windows.Automation.TreeScope]::Descendants
$root = $ae::RootElement
$desktopPath = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'DeepFind.lnk'
$exePath = Join-Path $env:LOCALAPPDATA 'Programs\DeepFind\DeepFind.App.exe'
function Find-Dialog {
    $windows = $root.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($window in $windows) {
        if ($window.Current.ClassName -eq 'MsiDialogCloseClass' -and
            $window.Current.Name -eq 'DeepFind Setup' -and -not $window.Current.IsOffscreen) {
            return $window
        }
    }
}
function Capture-Installer([string]$Tag) {
    Add-Type -AssemblyName System.Drawing, System.Windows.Forms
    $rect = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bitmap = [System.Drawing.Bitmap]::new($rect.Width,$rect.Height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen($rect.X,$rect.Y,0,0,$bitmap.Size)
        $bitmap.Save((Join-Path $env:RUNNER_TEMP "deepfind-ui-installer-$Tag.png"),[System.Drawing.Imaging.ImageFormat]::Png)
    } finally { $graphics.Dispose();$bitmap.Dispose() }
}
function Find-Control($Dialog, [string]$Name) {
    # MSI's Win32 provider may expose a named button or checkbox with a different
    # UIA ControlType than WPF. Use the real accessible label, scoped to the
    # observed installer dialog; never match arbitrary desktop controls.
    $expected = ($Name -replace '[&<>]','').Trim()
    $matches = @($Dialog.FindAll($scope, [System.Windows.Automation.Condition]::TrueCondition) |
        Where-Object { ($_.Current.Name -replace '[&<>]','').Trim() -eq $expected -and -not $_.Current.IsOffscreen })
    if ($matches.Count -gt 1) { throw "Ambiguous installer control '$Name'." }
    if ($matches.Count -eq 1) { return $matches[0] }
}
function Test-ControlEnabled($Control) {
    if (-not $Control) { return $false }
    $handle = [IntPtr]$Control.Current.NativeWindowHandle
    if ($handle -ne [IntPtr]::Zero) {
        return [DeepFindInstallerPointer]::IsWindowEnabled($handle)
    }
    return $Control.Current.IsEnabled
}
function Click-InstallerControl($Dialog, $Control) {
    if (-not (Test-ControlEnabled $Control)) { throw 'Installer control is not enabled.' }
    if ($Control.Current.ProcessId -ne $Dialog.Current.ProcessId -or $Control.Current.IsOffscreen) {
        throw 'Refusing to interact outside the visible DeepFind installer.'
    }
    $handle = [IntPtr]$Dialog.Current.NativeWindowHandle
    if ($handle -eq [IntPtr]::Zero) { throw 'Installer dialog has no native window handle.' }
    $null = [DeepFindInstallerPointer]::SetForegroundWindow($handle)
    $bounds = $Control.Current.BoundingRectangle
    if ($bounds.Width -le 0 -or $bounds.Height -le 0) { throw 'Installer control has empty click bounds.' }
    $x = [int]($bounds.X + $bounds.Width / 2)
    $y = [int]($bounds.Y + $bounds.Height / 2)
    $point = [DeepFindInstallerPointer+POINT]::new($x, $y)
    $hit = [DeepFindInstallerPointer]::WindowFromPoint($point)
    if ($hit -eq [IntPtr]::Zero -or [DeepFindInstallerPointer]::GetAncestor($hit, 2) -ne $handle) {
        throw "Installer control '$($Control.Current.Name)' is not hittable in its dialog."
    }
    Write-Host "Click installer control '$($Control.Current.Name)' at ($x,$y); type=$($Control.Current.ControlType.ProgrammaticName)."
    if (-not [DeepFindInstallerPointer]::SetCursorPos($x,$y)) { throw 'Could not move test pointer.' }
    [DeepFindInstallerPointer]::mouse_event(2,0,0,0,[UIntPtr]::Zero)
    [DeepFindInstallerPointer]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
}
function Get-CheckState($Dialog, $Control) {
    $toggle = $null
    if ($Control.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$toggle)) {
        return [int]$toggle.Current.ToggleState
    }
    # Observed Windows Installer provider exposes its license checkbox as Pane,
    # without TogglePattern. BM_GETCHECK is read-only and validates both HWND
    # ancestry and standard checkable Button style before reading the state.
    return [DeepFindInstallerPointer]::NativeCheckState(
        [IntPtr]$Control.Current.NativeWindowHandle, [IntPtr]$Dialog.Current.NativeWindowHandle)
}
function Set-Checked($Dialog, $Control, [bool]$Checked) {
    $expected = if ($Checked) { 1 } else { 0 }
    $state = Get-CheckState $Dialog $Control
    Write-Host "Installer checkbox '$($Control.Current.Name)' state=$state; expected=$expected."
    if ($state -ne $expected) { Click-InstallerControl $Dialog $Control }
    $deadline = (Get-Date).AddSeconds(3)
    do {
        if ((Get-CheckState $Dialog $Control) -eq $expected) {
            Write-Host "Installer checkbox verified state=$expected."
            return
        }
        Start-Sleep -Milliseconds 100
    } while ((Get-Date) -lt $deadline)
    throw "Installer checkbox '$($Control.Current.Name)' failed to reach checked=$Checked."
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
            $finish = Find-Control $dialog 'Finish'
            if (Test-ControlEnabled $finish) {
                $checkbox = Find-Control $dialog 'Create a desktop shortcut'
                if (-not $checkbox) { throw 'Finish screen missing Desktop shortcut checkbox.' }
                Set-Checked $dialog $checkbox $checked
                Capture-Installer "finish-$checked"
                Click-InstallerControl $dialog $finish
                $finished = $true
                break
            }
            # The state fingerprint prevents double-clicking stale Next buttons.
            $controls = $dialog.FindAll($scope, [System.Windows.Automation.Condition]::TrueCondition)
            $state = ($controls | ForEach-Object { $_.Current.Name }) -join '|'
            if ($state -eq $seenState) { Start-Sleep -Milliseconds 300; continue }
            $seenState = $state
            Write-Host "Installer dialog: $($dialog.Current.Name); controls: $state"
            foreach ($control in $controls) {
                if ($control.Current.Name -match '^I accept.*(terms|license)' -and -not $control.Current.IsOffscreen) {
                    Set-Checked $dialog $control $true
                }
            }
            $installButton = Find-Control $dialog 'Install'
            $nextButton = Find-Control $dialog 'Next'
            if (-not $nextButton) { $nextButton = Find-Control $dialog 'Next >' }
            if (Test-ControlEnabled $installButton) { Click-InstallerControl $dialog $installButton }
            elseif (Test-ControlEnabled $nextButton) { Click-InstallerControl $dialog $nextButton }
            else { Write-Host 'No enabled Next or Install control; waiting for dialog state change.'; $seenState = '' }
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
    } catch {
        Write-Host "Installer UI failure: $($_.ScriptStackTrace)"
        try { Capture-Installer "failure-$checked" } catch { Write-Host "Screenshot failed: $_" }
        foreach ($window in $root.FindAll([System.Windows.Automation.TreeScope]::Children,[System.Windows.Automation.Condition]::TrueCondition)) {
            Write-Host "Window: '$($window.Current.Name)' class=$($window.Current.ClassName) pid=$($window.Current.ProcessId)"
        }
        throw
    } finally {
        if (-not $install.HasExited) {
            $dialog = Find-Dialog
            if ($dialog) {
                $cancel = Find-Control $dialog 'Cancel'
                if (Test-ControlEnabled $cancel) { Click-InstallerControl $dialog $cancel }
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
