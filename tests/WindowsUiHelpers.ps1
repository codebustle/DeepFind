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
function Select-Combo($Root, [string]$Id, [string]$Label, [int]$KeyboardIndex=-1) {
    $combo = Wait-For "visible enabled combo $Id" {
        try {
            $item = By-Id $Root $Id
            if ($item -and -not $item.Current.IsOffscreen -and $item.Current.IsEnabled) { $item }
        } catch [System.Windows.Automation.ElementNotAvailableException] { }
    }
    Write-Host "Selecting $Id option '$Label'."
    $expand = Require-Pattern $combo ([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    if ($KeyboardIndex -ge 0) {
        Add-Type -AssemblyName System.Windows.Forms
        $combo.SetFocus()
        $expand.Expand()
        $keys = '{HOME}' + ('{DOWN}' * $KeyboardIndex) + '{ENTER}'
        [System.Windows.Forms.SendKeys]::SendWait($keys)
        $null = Wait-For "combo $Id closed after keyboard selection" {
            $expand.Current.ExpandCollapseState -eq [System.Windows.Automation.ExpandCollapseState]::Collapsed
        }
        return
    }
    $expand.Expand()
    $item = Wait-For "combo option $Label" {
        $exact = Find-AppControl $Label ([System.Windows.Automation.ControlType]::ListItem)
        if ($exact) { return $exact }
        # A content-host peer may expose the visible label instead of the
        # ListItem label. Restrict fallback to the tested app and visible text.
        $visible = Find-AppControl $Label ([System.Windows.Automation.ControlType]::Text)
        if ($visible -and -not $visible.Current.IsOffscreen) { $visible }
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
    # Allow the app's atomic replace while the test reads persisted settings.
    $share = [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete
    $stream = [IO.File]::Open($settingsPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, $share)
    $reader = [IO.StreamReader]::new($stream)
    try { $reader.ReadToEnd() | ConvertFrom-Json }
    finally { $reader.Dispose(); $stream.Dispose() }
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
