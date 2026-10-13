[CmdletBinding()]
param(
    [switch]$CleanupWorker,
    [string]$RunDirectory,
    [string]$WpfBuildDirectory,
    [string]$ClientSid
)

# Explicit interactive validation only. NetTrace does not execute this script.
# The real page button requests the install UAC prompt. A later separate cleanup
# worker requests elevation solely to uninstall the registration created here.
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$verificationRoot = Join-Path $repository 'bin\AppUsage0052Verification'
$serviceName = 'NetTraceAppUsageCollector'
$developmentServiceName = 'NetTraceAppUsageCollectorDev'
$sessionName = 'NetTrace-AppUsage003-Collector-938F90CEB2A34EF7A67BA5A43521EE77'
$pipeName = 'NetTrace.AppUsage003.Service.938F90CEB2A34EF7A67BA5A43521EE77'
$databasePath = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'NetTrace\app-usage.db'
$installationRoot = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'NetTrace\AppUsageCollector'
$isAdministrator = ([Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

function Assert-VerificationPath {
    param([string]$Path)
    if (-not [IO.Path]::IsPathRooted($Path)) { throw 'Verification paths must be absolute.' }
    $value = [IO.Path]::GetFullPath($Path)
    if (-not $value.StartsWith($verificationRoot+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Artifact path escaped the 005.2 verification directory.' }
    return $value
}

function Read-Registration {
    param([string]$Name=$serviceName)
    $text = & sc.exe query $Name 2>&1
    if ($LASTEXITCODE -eq 1060) { return $null }
    if ($LASTEXITCODE -ne 0) { throw "SCM inspection failed for $Name." }
    $service = Get-CimInstance Win32_Service -Filter "Name='$Name'"
    if (-not $service) { throw 'SCM and service configuration disagree.' }
    return $service
}

function Read-InstallerStatus {
    $text = & $installer status 2>&1
    if ($LASTEXITCODE -ne 0) { throw ('Read-only installer status failed: '+($text -join ' ')) }
    return ($text -join [Environment]::NewLine) | ConvertFrom-Json
}

function Write-Json {
    param([string]$Name,[object]$Value)
    $path = Join-Path $RunDirectory $Name
    $Value | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath ($path+'.writing')
    Move-Item -LiteralPath ($path+'.writing') -Destination $path -Force
}

if (-not $CleanupWorker) {
    if ($isAdministrator) { throw 'Start this workflow from a normal terminal; the page installer alone requests installation elevation.' }
    if ((Read-Registration) -or (Read-Registration $developmentServiceName)) { throw 'Live setup validation refuses an existing production or development service.' }
    New-Item -ItemType Directory -Path $verificationRoot -Force | Out-Null
    $RunDirectory = Join-Path $verificationRoot ('run-'+[Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $RunDirectory | Out-Null
    if (-not $WpfBuildDirectory) { $WpfBuildDirectory = Join-Path $verificationRoot 'Wpf' }
    $WpfBuildDirectory = Assert-VerificationPath $WpfBuildDirectory
    $installer = Join-Path $WpfBuildDirectory 'AppTracking\Installer\NetTrace.AppUsageInstaller.exe'
    $harness = Join-Path $WpfBuildDirectory 'NetTrace.WpfTests.exe'
    foreach ($path in @($harness,$installer,(Join-Path $WpfBuildDirectory 'AppTracking\Service\NetTrace.AppUsageService.exe'))) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "The complete prebuilt 005.2 output is missing: $path" }
    }
    $ClientSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $clientReportPath = Join-Path $RunDirectory 'Setup-client.json'
    Write-Json 'normal-baseline.json' ([pscustomobject]@{ ProductionAbsent=$true; DevelopmentAbsent=$true; ClientSid=$ClientSid; WpfDirectory=$WpfBuildDirectory })
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $harness
    $start.Arguments = '--app-usage-setup-live-validation --output "'+$clientReportPath+'"'
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $client = [Diagnostics.Process]::Start($start)
    $standardOutput = $client.StandardOutput.ReadToEndAsync()
    $standardError = $client.StandardError.ReadToEndAsync()
    $report = $null
    $cleanup = $null
    $workflowError = $null
    try {
        Write-Output 'Unelevated real setup page started. Its explicit Install App Tracking click requests the installation UAC prompt.'
        $watch = [Diagnostics.Stopwatch]::StartNew()
        while (-not $client.HasExited -and $watch.Elapsed.TotalSeconds -lt 155) {
            Start-Sleep -Milliseconds 250
            $client.Refresh()
        }
        if (-not $client.HasExited) { throw 'The bounded setup page observation did not finish.' }
        [IO.File]::WriteAllText((Join-Path $RunDirectory 'Setup-client.log'),$standardOutput.GetAwaiter().GetResult())
        [IO.File]::WriteAllText((Join-Path $RunDirectory 'Setup-client-error.log'),$standardError.GetAwaiter().GetResult())
        if (-not (Test-Path -LiteralPath $clientReportPath)) { throw 'The normal-token setup report is missing.' }
        $report = Get-Content -LiteralPath $clientReportPath -Raw | ConvertFrom-Json
        if (-not $report.Completed) { throw 'The setup report did not reach a final installer outcome.' }
        if (-not $report.Success -or $client.ExitCode -ne 0) { throw ('Setup page validation failed: '+$report.Error) }
    }
    catch { $workflowError=$_.Exception.Message }
    finally {
        if (-not $client.HasExited) { Stop-Process -Id $client.Id -Force }
        $client.Dispose()
        if (-not $report -and (Test-Path -LiteralPath $clientReportPath)) {
            $report = Get-Content -LiteralPath $clientReportPath -Raw | ConvertFrom-Json
        }
        try {
            $current = Read-Registration
            if ($current) {
                if (-not $report -or -not $report.ExplicitInstallClicked -or $report.BaselineState -ne 'Missing' -or
                    -not $report.InstallOutcome) {
                    throw 'A registration is present without a completed known install; refusing to race or adopt it.'
                }
                $expected = Read-InstallerStatus
                if (-not $expected.Installed) { throw 'Production registration disappeared before ownership-checked cleanup.' }
                Write-Json 'cleanup-context.json' ([pscustomobject]@{ ExpectedRegistration=$expected; ClientSid=$ClientSid; ExplicitInstallClicked=$true; BaselineMissing=$true })
                $arguments = '-NoProfile -ExecutionPolicy RemoteSigned -File "'+$PSCommandPath+'" -CleanupWorker -RunDirectory "'+$RunDirectory+'" -WpfBuildDirectory "'+$WpfBuildDirectory+'" -ClientSid '+$ClientSid
                Write-Output 'The completed page installation will now be removed by a separate explicit cleanup UAC prompt; history is retained.'
                $worker = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -Verb RunAs -WindowStyle Hidden -PassThru -ArgumentList $arguments
                try {
                    $cleanupWatch = [Diagnostics.Stopwatch]::StartNew()
                    while (-not $worker.HasExited -and $cleanupWatch.Elapsed.TotalSeconds -lt 110) {
                        $null = $worker.WaitForExit(250)
                        $worker.Refresh()
                    }
                    if (-not $worker.HasExited) { throw 'The cleanup worker exceeded its bounded uninstall wait; it was left alive to finish safely.' }
                    $summaryPath = Join-Path $RunDirectory 'cleanup-summary.json'
                    if (-not (Test-Path -LiteralPath $summaryPath)) { throw 'The elevated cleanup summary is missing.' }
                    $cleanup = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
                    if (-not $cleanup.Success) { throw ('Cleanup verification failed: '+$cleanup.Error) }
                }
                finally { $worker.Dispose() }
            }
            else {
                $cleanup = [pscustomobject]@{ Success=$true; ServiceAbsent=$true; NoCleanupNeeded=$true; DatabasePreserved='Not changed by this workflow' }
            }
        }
        catch { $workflowError=($workflowError+' Cleanup: '+$_.Exception.Message).Trim() }
    }
    $summary = [pscustomobject]@{ Success=($null -eq $workflowError); Error=$workflowError; Client=$report; Cleanup=$cleanup; EvidenceDirectory=$RunDirectory }
    Write-Json 'workflow-summary.json' $summary
    $summary | ConvertTo-Json -Depth 20
    if ($workflowError) { throw ($workflowError+'. Evidence: '+$RunDirectory) }
    return
}

if (-not $isAdministrator) { throw 'The explicitly invoked cleanup worker requires elevation.' }
$RunDirectory = Assert-VerificationPath $RunDirectory
$WpfBuildDirectory = Assert-VerificationPath $WpfBuildDirectory
if ([Security.Principal.WindowsIdentity]::GetCurrent().User.Value -ne $ClientSid) { throw 'Elevate the same account that runs the normal page.' }
$installer = Join-Path $WpfBuildDirectory 'AppTracking\Installer\NetTrace.AppUsageInstaller.exe'
$context = Get-Content -LiteralPath (Join-Path $RunDirectory 'cleanup-context.json') -Raw | ConvertFrom-Json
$expected = $context.ExpectedRegistration
if (-not $context.ExplicitInstallClicked -or -not $context.BaselineMissing -or $context.ClientSid -ne $ClientSid) { throw 'The explicit page install ownership context is invalid.' }

function Read-KitVersion {
    $source = [IO.Path]::GetFullPath((Join-Path $WpfBuildDirectory 'AppTracking\Service')).TrimEnd('\')
    if ((Get-Item -LiteralPath $source).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'The source kit is a reparse point.' }
    $files = [Collections.Generic.SortedDictionary[string,string]]::new([StringComparer]::Ordinal)
    foreach ($entry in Get-ChildItem -LiteralPath $source -Recurse -Force) {
        if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'The source kit contains a reparse point.' }
        if ($entry.PSIsContainer) { continue }
        $absolute = [IO.Path]::GetFullPath($entry.FullName)
        if (-not $absolute.StartsWith($source+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'A source kit entry escaped its directory.' }
        $relative = $absolute.Substring($source.Length+1)
        $files.Add($relative,(Get-FileHash -LiteralPath $absolute -Algorithm SHA256).Hash.ToLowerInvariant())
    }
    if ($files.Count -eq 0 -or $files.Count -gt 10000) { throw 'The source kit inventory is invalid.' }
    $inventory = ($files.GetEnumerator() | ForEach-Object { $_.Key+'|'+$_.Value }) -join "`n"
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($inventory))).Replace('-','').ToLowerInvariant() }
    finally { $sha.Dispose() }
}
$sourceKitVersion = Read-KitVersion

function Assert-OwnedRegistration {
    $current = Read-InstallerStatus
    if (-not $current.Installed -or $current.ServiceName -ne $serviceName -or $current.Identity -ne 'LocalSystem' -or
        $current.Description -ne 'NetTrace App Usage production collector; installation format 005.1.' -or
        $current.BinaryCommandLine -cne $expected.BinaryCommandLine) {
        throw 'Registration ownership changed; the workflow will not stop or uninstall another registration.'
    }
    if ($current.BinaryCommandLine -notmatch '^"([^"]+)" --client-sid (S-1-5-21-\d+-\d+-\d+-\d+) --service-name NetTraceAppUsageCollector --log "([^"]+)"$' -or
        $Matches[2] -ne $ClientSid) { throw 'The production command does not match the exact intended client identity.' }
    $image = [IO.Path]::GetFullPath($Matches[1])
    $log = [IO.Path]::GetFullPath($Matches[3])
    $directory = Split-Path -Parent $image
    if (-not $directory.StartsWith((Join-Path $installationRoot 'Versions')+'\',[StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path -Leaf $directory) -notmatch '^[0-9a-f]{64}$' -or
        (Split-Path -Leaf $image) -ne 'NetTrace.AppUsageService.exe' -or
        $log -ne (Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'NetTrace\AppUsageService\service.log')) {
        throw 'The registered production image or log escaped the protected 005.1 installation layout.'
    }
    if ((Split-Path -Leaf $directory) -cne $sourceKitVersion -or
        (Get-FileHash -LiteralPath $image -Algorithm SHA256).Hash -cne (Get-FileHash -LiteralPath (Join-Path $WpfBuildDirectory 'AppTracking\Service\NetTrace.AppUsageService.exe') -Algorithm SHA256).Hash) {
        throw 'The recorded installation does not match this workflow bundled service kit.'
    }
    # The existing tool revalidates protected manifests and binary hashes during
    # uninstall. These exact snapshot checks additionally guard the preceding
    # clean stop used only to obtain a settled database-preservation hash.
    $service = Read-Registration
    if ($service.PathName -cne $expected.BinaryCommandLine -or $service.StartName -ne 'LocalSystem') { throw 'The service changed before its clean stop.' }
    return $service
}

$success = $false
$errorText = $null
$before = $null
$after = $null
$preserved = $false
$serviceAbsent = $false
$ownedSessionAbsent = $false
$noServiceProcess = $false
$noPipeServer = $false
Start-Transcript -LiteralPath (Join-Path $RunDirectory 'cleanup-workflow.log') -Force | Out-Null
try {
    if (Read-Registration $developmentServiceName) { throw 'A competing development registration is present; refusing cleanup.' }
    $service = Assert-OwnedRegistration
    $ownedPid = [int]$service.ProcessId
    # Prevent an already queued SCM recovery action from restarting the exact
    # owned collector between its clean stop, settled hash and tool uninstall.
    $result = & sc.exe config $serviceName start= disabled 2>&1
    if ($LASTEXITCODE -ne 0) { throw ('Disabling owned startup before cleanup failed: '+($result -join ' ')) }
    Assert-OwnedRegistration | Out-Null
    if ($service.State -ne 'Stopped') {
        $result = & sc.exe stop $serviceName 2>&1
        if ($LASTEXITCODE -ne 0) { throw ('Owned service stop failed: '+($result -join ' ')) }
    }
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ((Read-Registration).State -ne 'Stopped' -and $watch.Elapsed.TotalSeconds -lt 30) { Start-Sleep -Milliseconds 200 }
    if ((Read-Registration).State -ne 'Stopped') { throw 'The owned service did not finish its clean stop.' }
    if ($ownedPid -gt 0) {
        try {
            $process = [Diagnostics.Process]::GetProcessById($ownedPid)
            try { if (-not $process.WaitForExit(10000)) { throw 'The cleanly stopped collector process did not exit.' } }
            finally { $process.Dispose() }
        }
        catch [ArgumentException] { }
    }
    Assert-OwnedRegistration | Out-Null
    if (-not (Test-Path -LiteralPath $databasePath -PathType Leaf)) { throw 'The installed production database is absent.' }
    $before = [pscustomobject]@{ Path=$databasePath; Length=(Get-Item -LiteralPath $databasePath).Length; Sha256=(Get-FileHash -LiteralPath $databasePath -Algorithm SHA256).Hash }
    $result = & $installer uninstall 2>&1
    $toolExit = $LASTEXITCODE
    $result | Out-File -LiteralPath (Join-Path $RunDirectory 'uninstall.log')
    if ($toolExit -ne 0) { throw ('The existing ownership-checked uninstall tool failed: '+($result -join ' ')) }
    & sc.exe query $serviceName 2>&1 | Out-File -LiteralPath (Join-Path $RunDirectory 'final-scm-query.log')
    $serviceAbsent = $LASTEXITCODE -eq 1060
    & logman.exe query $sessionName -ets 2>&1 | Out-File -LiteralPath (Join-Path $RunDirectory 'final-session-query.log')
    $ownedSessionAbsent = $LASTEXITCODE -ne 0
    $noServiceProcess = @(Get-CimInstance Win32_Process -Filter "Name='NetTrace.AppUsageService.exe'" | Where-Object { $_.ProcessId -eq $ownedPid -or ($_.ExecutablePath -and $_.ExecutablePath.StartsWith($installationRoot+'\',[StringComparison]::OrdinalIgnoreCase)) }).Count -eq 0
    $noPipeServer = @(Get-ChildItem -LiteralPath '\\.\pipe\' | Where-Object Name -eq $pipeName).Count -eq 0
    if (Test-Path -LiteralPath $databasePath -PathType Leaf) {
        $after = [pscustomobject]@{ Path=$databasePath; Length=(Get-Item -LiteralPath $databasePath).Length; Sha256=(Get-FileHash -LiteralPath $databasePath -Algorithm SHA256).Hash }
    }
    $preserved = $null -ne $after -and $before.Length -eq $after.Length -and $before.Sha256 -eq $after.Sha256
    if (-not $serviceAbsent -or -not $ownedSessionAbsent -or -not $noServiceProcess -or -not $noPipeServer -or -not $preserved) { throw 'Owned cleanup or byte-for-byte database preservation verification failed.' }
    $success = $true
}
catch { $errorText=$_.Exception.Message }
finally {
    Write-Json 'cleanup-summary.json' ([pscustomobject]@{ Success=$success; Error=$errorText; ServiceAbsent=$serviceAbsent; OwnedSessionAbsent=$ownedSessionAbsent; NoServiceProcess=$noServiceProcess; NoPipeServer=$noPipeServer; DatabaseBefore=$before; DatabaseAfter=$after; DatabasePreserved=$preserved })
    Stop-Transcript | Out-Null
}
if (-not $success) { exit 1 }
