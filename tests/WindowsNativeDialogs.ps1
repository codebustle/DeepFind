# Native message boxes may expose their controls as Pane rather than the usual Text/Button UIA types.
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class DeepFindNativeDialogPointer {
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; public POINT(int x,int y){X=x;Y=y;} }
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT point);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hwnd,uint flags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags,uint dx,uint dy,uint data,UIntPtr extra);
}
'@
function Find-NativeMessageDialog([string]$Message) {
    $root=[System.Windows.Automation.AutomationElement]::RootElement
    $pidCondition=[System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty,$script:process.Id)
    $classCondition=[System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ClassNameProperty,'#32770')
    $condition=[System.Windows.Automation.AndCondition]::new([System.Windows.Automation.Condition[]]@($pidCondition,$classCondition))
    $windows=$root.FindAll([System.Windows.Automation.TreeScope]::Descendants,$condition)
    $expected=($Message -replace '\s+',' ').Trim()
    foreach ($dialog in $windows) {
        if ($dialog.Current.Name -ne 'DeepFind' -or $dialog.Current.IsOffscreen) { continue }
        $controls=$dialog.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)
        foreach ($control in $controls) {
            if (($control.Current.Name -replace '\s+',' ').Trim() -eq $expected) { return $dialog }
        }
    }
}
function Find-NativeDialogTitle([string]$Title) {
    $root=[System.Windows.Automation.AutomationElement]::RootElement
    $conditions=@(
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$script:process.Id),
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$Title),
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ClassNameProperty,'#32770'))
    $dialog=$root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.AndCondition]::new([System.Windows.Automation.Condition[]]$conditions))
    if ($dialog -and -not $dialog.Current.IsOffscreen) { $dialog }
}
function Click-NativeMessageButton($Dialog,[string]$Name) {
    if (-not $Dialog -or $Dialog.Current.ProcessId -ne $script:process.Id -or $Dialog.Current.ClassName -ne '#32770') {
        throw 'Refusing to click outside the tested DeepFind native message.'
    }
    $expected=($Name -replace '&','').Trim()
    $controls=$Dialog.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)
    $buttons=@($controls | Where-Object { ($_.Current.Name -replace '&','').Trim() -eq $expected -and -not $_.Current.IsOffscreen })
    if ($buttons.Count -ne 1) { throw "Expected one native $Name control; found $($buttons.Count)." }
    $h=[IntPtr]$Dialog.Current.NativeWindowHandle
    $null=[DeepFindNativeDialogPointer]::SetForegroundWindow($h)
    $bounds=$buttons[0].Current.BoundingRectangle
    if ($bounds.Width -le 0 -or $bounds.Height -le 0) { throw 'Invalid native button bounds.' }
    $x=[int]($bounds.X+$bounds.Width/2); $y=[int]($bounds.Y+$bounds.Height/2)
    $hit=[DeepFindNativeDialogPointer]::WindowFromPoint([DeepFindNativeDialogPointer+POINT]::new($x,$y))
    if ($hit -eq [IntPtr]::Zero -or [DeepFindNativeDialogPointer]::GetAncestor($hit,2) -ne $h) {
        throw 'Native message button is not hittable in its own dialog.'
    }
    if (-not [DeepFindNativeDialogPointer]::SetCursorPos($x,$y)) { throw 'Could not move test pointer.' }
    [DeepFindNativeDialogPointer]::mouse_event(2,0,0,0,[UIntPtr]::Zero)
    [DeepFindNativeDialogPointer]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
}
