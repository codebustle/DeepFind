param([Parameter(Mandatory=$true)][string]$Application)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or -not $env:RUNNER_TEMP) {
    throw 'This test requires a disposable GitHub Actions Windows runner.'
}
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
. "$PSScriptRoot\WindowsNativeDialogs.ps1"
$ae = [System.Windows.Automation.AutomationElement]
$scope = [System.Windows.Automation.TreeScope]::Descendants
$workspace = Join-Path $env:RUNNER_TEMP ('deepfind-recovery-' + [Guid]::NewGuid().ToString('N'))
$source = Join-Path $workspace 'sources'
$parent = Join-Path $workspace 'bulk-index'
$settingsPath = Join-Path $env:LOCALAPPDATA 'DeepFind\settings.json'
$tracking = Join-Path $parent 'DeepFind.Index\tracking.db'
New-Item -ItemType Directory -Path $source,$parent -Force | Out-Null
$encoding = [Text.UTF8Encoding]::new($false)
for ($i=0; $i -lt 10001; $i++) {
    $name = 'bulk-{0:D5}.txt' -f $i
    $content = 'bulkneedle'
    if ($i -lt 1000) { $content += ' thousandexact' }
    if ($i -lt 1001) { $content += ' thousandandoneexact' }
    if ($i -eq 10000) { $content += ' uniquebulkword' }
    [IO.File]::WriteAllText((Join-Path $source $name),$content,$encoding)
}
function Write-Settings($Root,$IndexParent,$Paused=$false) {
    New-Item -ItemType Directory -Path (Split-Path $settingsPath) -Force | Out-Null
    @{Folders=@($Root); IndexLocation=$IndexParent; PageSize=250; Theme='System'; Paused=$Paused} |
        ConvertTo-Json | Set-Content -LiteralPath $settingsPath -Encoding UTF8
}
function Wait-For([string]$Description,[scriptblock]$Condition,[int]$Seconds=90) {
    $deadline=(Get-Date).AddSeconds($Seconds)
    do {
        $value=& $Condition
        if ($value) { return $value }
        Start-Sleep -Milliseconds 200
    } while ((Get-Date) -lt $deadline)
    throw "Timed out: $Description"
}
function By-Id($Root,[string]$Id) {
    $Root.FindFirst($scope,([System.Windows.Automation.PropertyCondition]::new($ae::AutomationIdProperty,$Id)))
}
function By-Name($Root,[string]$Name) {
    $Root.FindFirst($scope,([System.Windows.Automation.PropertyCondition]::new($ae::NameProperty,$Name)))
}
function App-Control([string]$Name,$Type) {
    $conditions=@(
        ([System.Windows.Automation.PropertyCondition]::new($ae::ProcessIdProperty,$script:process.Id)),
        ([System.Windows.Automation.PropertyCondition]::new($ae::NameProperty,$Name)),
        ([System.Windows.Automation.PropertyCondition]::new($ae::ControlTypeProperty,$Type)))
    $ae::RootElement.FindFirst($scope,([System.Windows.Automation.AndCondition]::new([System.Windows.Automation.Condition[]]$conditions)))
}
function Invoke-Control($Element) {
    if (-not $Element) { throw 'Missing required control.' }
    $Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}
function Set-Query([string]$Query) {
    (By-Id $script:main SearchBox).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Query)
}
function Start-App {
    $script:process=Start-Process $Application -PassThru
}
function Wait-Main {
    $script:main=Wait-For 'main window' {
        $script:process.Refresh()
        if ($script:process.HasExited) { throw "Application exited: $($script:process.ExitCode)." }
        if ($script:process.MainWindowHandle -ne 0 -and $script:process.MainWindowTitle -eq 'DeepFind') {
            $candidate=$ae::FromHandle($script:process.MainWindowHandle)
            if ($candidate -and $candidate.Current.ClassName -ne '#32770' -and (By-Id $candidate SearchBox)) { $candidate }
        }
    }
}
function Close-App {
    if (-not $script:process.CloseMainWindow() -or -not $script:process.WaitForExit(20000) -or $script:process.ExitCode -ne 0) {
        throw 'DeepFind did not close cleanly.'
    }
}
function Capture($Element,[string]$Name) {
    Add-Type -AssemblyName System.Drawing
    $r=$Element.Current.BoundingRectangle
    $bmp=[System.Drawing.Bitmap]::new([int]$r.Width,[int]$r.Height)
    $g=[System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.CopyFromScreen([int]$r.X,[int]$r.Y,0,0,$bmp.Size)
        $bmp.Save((Join-Path $env:RUNNER_TEMP "deepfind-ui-$Name.png"),[System.Drawing.Imaging.ImageFormat]::Png)
    } finally { $g.Dispose(); $bmp.Dispose() }
}
function Read-Tracking {
    if (-not (Test-Path -LiteralPath $tracking -PathType Leaf)) { return }
    # Query only the disposable index in read-only mode. Catch startup/lock races.
    $raw = & python "$PSScriptRoot\ReadTracking.py" $tracking
    if ($LASTEXITCODE -ne 0) { throw 'Unable to query disposable tracking journal.' }
    if ($raw) { $raw | ConvertFrom-Json }
}
function Find-CompletionLines {
    $dir=Join-Path $env:LOCALAPPDATA 'DeepFind\Logs'
    @((Get-ChildItem -LiteralPath $dir -Filter 'app-*.log' -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTime |
        ForEach-Object { Get-Content -LiteralPath $_.FullName }) |
        Where-Object { $_ -match 'Index reconciliation completed:' })
}
function Assert-Range([string]$Page,[string]$Range) {
    (By-Id $script:main PageLabel).Current.Name -eq $Page -and (By-Id $script:main ResultCount).Current.Name -eq $Range
}
$process=$null
$main=$null
try {
    Write-Settings $source $parent
    Start-App
    Wait-Main
    $partial=Wait-For 'committed partial batch with pending journal' {
        $snapshot=Read-Tracking
        if ($snapshot -and $snapshot.count -ge 64 -and $snapshot.count -lt 10001 -and $snapshot.requested -gt $snapshot.completed) { $snapshot }
    } 120
    Write-Host "Before forced stop: files=$($partial.count), journal=$($partial.requested)/$($partial.completed)."
    # Only this test's own child app is stopped, after real committed work.
    Stop-Process -Id $process.Id -Force
    $null=$process.WaitForExit(15000)
    $after=Read-Tracking
    if (-not $after -or $after.count -lt 64 -or $after.count -ge 10001 -or $after.requested -le $after.completed) {
        throw 'Interruption did not preserve the required partial committed/pending state.'
    }
    Write-Host "After forced stop: files=$($after.count), journal=$($after.requested)/$($after.completed)."
    Start-App
    Wait-Main
    $complete=Wait-For 'restart reconciles all 10001 fixtures and closes journal' {
        $snapshot=Read-Tracking
        if ($snapshot -and $snapshot.count -eq 10001 -and $snapshot.requested -eq $snapshot.completed -and
            (By-Name $main 'Index up to date')) { $snapshot }
    } 240
    Set-Query 'bulkneedle'
    $null=Wait-For '10001 results and 41 pages' { Assert-Range 'Page 1 / 41' '1–250 of 10,001 results' }
    Invoke-Control (By-Id $main LastPage)
    $null=Wait-For 'last page past the former engine cap' { Assert-Range 'Page 41 / 41' '10,001–10,001 of 10,001 results' }
    if ((By-Id $main NextPage).Current.IsEnabled) { throw 'Next remains enabled on last page.' }
    Capture $main 'recovery-bulk-last-page'
    Invoke-Control (By-Id $main PreviousPage)
    $null=Wait-For 'previous page has correct range' { Assert-Range 'Page 40 / 41' '9,751–10,000 of 10,001 results' }
    Set-Query 'thousandexact'
    $null=Wait-For '1000 results exactly four pages' { Assert-Range 'Page 1 / 4' '1–250 of 1,000 results' }
    for ($page=2;$page -le 4;$page++) {
        Invoke-Control (By-Id $main NextPage)
        $expectedPage="Page $page / 4"
        $start=($page-1)*250+1
        $end=$page*250
        $expectedRange=('{0:N0}–{1:N0} of 1,000 results' -f $start,$end)
        $null=Wait-For "1000 fixture range $expectedRange" { Assert-Range $expectedPage $expectedRange }
    }
    if ((By-Id $main NextPage).Current.IsEnabled) { throw 'Last exact-full page did not disable Next.' }
    Set-Query 'thousandandoneexact'
    $null=Wait-For '1001 results exactly five pages' { Assert-Range 'Page 1 / 5' '1–250 of 1,001 results' }
    Invoke-Control (By-Id $main LastPage)
    $null=Wait-For '1001 final one-row page' { Assert-Range 'Page 5 / 5' '1,001–1,001 of 1,001 results' }
    if ((By-Id $main NextPage).Current.IsEnabled) { throw 'Partial final page did not disable Next.' }
    Capture $main 'recovery-1001-final-page'
    Write-Host 'Actual UI 1000 and 1001 match boundary ranges and final page assertions passed.'
    # Several rapid query updates should never leave a stale result after final request.
    Set-Query 'missingbulkword'
    Set-Query 'bulkneedle'
    Set-Query 'uniquebulkword'
    $null=Wait-For 'rapid final query wins and resets page' {
        (Assert-Range 'Page 1 / 1' '1–1 of 1 results') -and (By-Name $main 'bulk-10000.txt')
    }
    $restartBefore=@(Find-CompletionLines).Count
    Close-App
    Start-App
    Wait-Main
    $null=Wait-For 'clean restart completes metadata-only check' {
        $snapshot=Read-Tracking
        $lines=@(Find-CompletionLines)
        $snapshot -and $snapshot.count -eq 10001 -and $snapshot.requested -eq $snapshot.completed -and
            $snapshot.completed -gt $complete.completed -and $lines.Count -gt $restartBefore -and
            $lines[-1] -match 'Index reconciliation completed: 10001 checked, 0 new or changed, 0 notices' -and
            (By-Name $main 'Index up to date')
    } 180
    Write-Host 'Real interrupted scan recovery, 10001 matches, deep page, last boundary, rapid query and clean-restart zero new/changed accounting passed.'
    Capture $main 'recovery-clean-restart'
    Close-App
    if (@(Get-ChildItem -LiteralPath $source -Filter 'bulk-*.txt' -File).Count -ne 10001) { throw 'Source files changed.' }

    # Controlled corruption of a separate small test index, never the owner's data.
    $smallSource=Join-Path $workspace 'corruption-source'
    $smallParent=Join-Path $workspace 'corruption-index'
    New-Item -ItemType Directory -Path $smallSource,$smallParent | Out-Null
    $original=Join-Path $smallSource 'original.txt'
    [IO.File]::WriteAllText($original,'recoverableoriginal',$encoding)
    $originalHash=(Get-FileHash -LiteralPath $original).Hash
    Write-Settings $smallSource $smallParent
    Start-App
    Wait-Main
    $null=Wait-For 'small index ready' { (By-Name $main 'Index up to date') -and (By-Name $main 'original.txt') }
    Close-App
    $search=Join-Path $smallParent 'DeepFind.Index\Search'
    $segments=@(Get-ChildItem -LiteralPath $search -Filter 'segments_*' -File)
    if ($segments.Count -ne 1) { throw "Expected one active Lucene segment manifest; found $($segments.Count)." }
    $segment=$segments[0].FullName
    [IO.File]::WriteAllBytes($segment,[byte[]]@(0,1,2,3,4,5,6,7,8,9,10,11))
    $badHash=(Get-FileHash -LiteralPath $segment).Hash
    Start-App
    $message='The search index cannot be read. Remove the damaged search index and rebuild from your selected folders?'
    $prompt=Wait-For 'corruption recovery prompt' { Find-NativeMessageDialog $message }
    Capture $prompt 'recovery-corrupt-confirmation-no'
    Click-NativeMessageButton $prompt 'No'
    if (-not $process.WaitForExit(15000) -or $process.ExitCode -ne 1) { throw 'Declining recovery should exit with failure.' }
    if (-not (Test-Path -LiteralPath $segment) -or (Get-FileHash -LiteralPath $segment).Hash -ne $badHash) { throw 'Declining recovery modified the damaged index.' }
    Write-Host 'Declined corruption recovery exited and preserved exact damaged manifest hash.'
    Start-App
    $prompt=Wait-For 'accept corruption recovery' { Find-NativeMessageDialog $message }
    Capture $prompt 'recovery-corrupt-confirmation-yes'
    Click-NativeMessageButton $prompt 'Yes'
    Write-Host 'Confirmed explicit corruption recovery; waiting for actual WPF SearchBox.'
    Wait-Main
    Set-Query 'recoverableoriginal'
    $null=Wait-For 'acceptance rebuilds original content' {
        (By-Name $main 'Index up to date') -and (Assert-Range 'Page 1 / 1' '1–1 of 1 results') -and (By-Name $main 'original.txt')
    }
    if ((Get-FileHash -LiteralPath $original).Hash -ne $originalHash) { throw 'Corruption recovery modified the source.' }
    Capture $main 'recovery-corrupt-index-rebuilt'
    Write-Host 'Corruption recovery decline and accept both verified; original content preserved.'
    Close-App
} catch {
    Write-Host "Recovery test failed: $($_.ScriptStackTrace)"
    if ($main -and $process -and -not $process.HasExited) { try { Capture $main 'recovery-failure' } catch { Write-Host "Screenshot unavailable: $_" } }
    throw
} finally {
    if ($process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force }
}
