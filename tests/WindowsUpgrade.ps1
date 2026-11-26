param(
    [Parameter(Mandatory=$true)][string]$PreviousMsi,
    [Parameter(Mandatory=$true)][string]$CandidateMsi
)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or -not $env:RUNNER_TEMP) {
    throw 'This test requires a disposable GitHub Actions Windows runner.'
}
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$ae = [System.Windows.Automation.AutomationElement]
$scope = [System.Windows.Automation.TreeScope]::Descendants
$data = Join-Path $env:LOCALAPPDATA 'DeepFind'
$application = Join-Path $env:LOCALAPPDATA 'Programs\DeepFind\DeepFind.App.exe'
$startLink = Join-Path ([Environment]::GetFolderPath('Programs')) 'DeepFind.lnk'
$source = Join-Path $env:RUNNER_TEMP ('deepfind-upgrade-source-' + [Guid]::NewGuid().ToString('N'))
$customParent = Join-Path $env:RUNNER_TEMP ('deepfind-upgrade-index-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $source,$customParent | Out-Null
$fixture = Join-Path $source 'upgrade-document.txt'
Set-Content $fixture 'upgradefixturekeyword'
$settingsFile = Join-Path $data 'settings.json'
function Invoke-Msi([string]$Operation,[string]$File,[string]$Name,[string]$Properties='') {
    $log = Join-Path $env:RUNNER_TEMP "deepfind-upgrade-$Name.log"
    $p = Start-Process msiexec.exe -ArgumentList ('{0} "{1}" /quiet /norestart /log "{2}" {3}' -f $Operation,$File,$log,$Properties) -Wait -PassThru
    if ($p.ExitCode -ne 0) { throw "MSI ${Name}: $($p.ExitCode)" }
}
function Inspect-App([string]$Expected) {
    $p = Start-Process $application -PassThru
    try {
        $deadline = (Get-Date).AddSeconds(90)
        $seen = $false
        do {
            $p.Refresh()
            if ($p.HasExited) { throw "App exited unexpectedly: $($p.ExitCode)" }
            if ($p.MainWindowHandle -ne 0) {
                $main = $ae::FromHandle($p.MainWindowHandle)
                $condition = [System.Windows.Automation.PropertyCondition]::new($ae::NameProperty, $Expected)
                if ($main.FindFirst($scope, $condition)) { $seen = $true; break }
            }
            Start-Sleep -Milliseconds 250
        } while ((Get-Date) -lt $deadline)
        if (-not $seen) { throw "Did not see expected indexed result: $Expected" }
        if (-not $p.CloseMainWindow() -or -not $p.WaitForExit(15000)) { throw 'App did not stop.' }
        # V2 is a historical baseline. V3 must close cleanly.
        if ($Expected -eq 'Page 1 / 1' -and $p.ExitCode -ne 0) { throw "V3 close failed: $($p.ExitCode)" }
    } finally {
        if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }
    }
}
function Assert-StartLink {
    if (-not (Test-Path $startLink)) { throw 'Start Menu shortcut missing after upgrade/repair.' }
    $shell = New-Object -ComObject WScript.Shell
    if ($shell.CreateShortcut($startLink).TargetPath -ne $application) { throw 'Wrong Start Menu target.' }
}
Invoke-Msi /i $PreviousMsi 'v2-install'
try {
    New-Item -ItemType Directory -Path $data -Force | Out-Null
    @{Folders=@($source); IndexLocation=$customParent; Theme='Dark'} | ConvertTo-Json | Set-Content $settingsFile -Encoding UTF8
    Inspect-App '1 result'
    $index = Join-Path $customParent 'DeepFind.Index'
    if (-not (Test-Path (Join-Path $index 'tracking.db'))) { throw 'V2 failed to create the custom index.' }
    $hash = (Get-FileHash $settingsFile).Hash
    Invoke-Msi /i $CandidateMsi 'v3-upgrade'
    if ((Get-FileHash $settingsFile).Hash -ne $hash) { throw 'Upgrade changed the existing settings.' }
    if (-not (Test-Path (Join-Path $index 'tracking.db'))) { throw 'Upgrade removed the existing custom index.' }
    $engine = New-Object -ComObject WindowsInstaller.Installer
    $installed = @()
    foreach ($code in $engine.Products) {
        try {
            $name = $engine.ProductInfo($code, 'ProductName')
            if ($name -eq 'DeepFind') { $installed += [pscustomobject]@{Code=$code; Version=$engine.ProductInfo($code, 'VersionString')} }
        } catch { continue }
    }
    Write-Host "Windows Installer product registrations: $($installed | ConvertTo-Json -Compress)"
    if ($installed.Count -ne 1 -or [version]$installed[0].Version -ne [version]'1.1.0') { throw 'Upgrade did not leave exactly one V3 MSI registration.' }
    Assert-StartLink
    Inspect-App 'Page 1 / 1'
    Invoke-Msi /fa $CandidateMsi 'repair'
    Assert-StartLink
    if ((Get-FileHash $settingsFile).Hash -ne $hash) { throw 'Repair changed existing settings.' }
    Invoke-Msi /x $CandidateMsi 'retain'
    if (-not (Test-Path $settingsFile) -or -not (Test-Path (Join-Path $index 'tracking.db'))) {
        throw 'Default uninstall removed settings or the custom index.'
    }
    Invoke-Msi /i $CandidateMsi 'reinstall'
    Invoke-Msi /x $CandidateMsi 'removeindex' 'REMOVEINDEX=1'
    if (Test-Path $data) { throw 'Explicit default-data removal did not remove the DeepFind data directory.' }
    if (-not (Test-Path $fixture) -or -not (Test-Path (Join-Path $index 'tracking.db'))) {
        throw 'Explicit default-data removal touched a source or custom index.'
    }
    Write-Host 'V2 to V3 upgrade, one MSI registration, search, repair, default data retention and explicit default-data removal passed.'
} finally {
    if (Test-Path $application) {
        # Either version may remain if a prior step failed; only these disposable
        # installer products are eligible for cleanup.
        try { Invoke-Msi /x $CandidateMsi 'cleanup-v3' } catch { Invoke-Msi /x $PreviousMsi 'cleanup-v2' }
    }
}
