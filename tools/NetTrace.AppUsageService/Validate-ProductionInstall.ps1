[CmdletBinding()]
param(
    [switch]$AdminWorker,
    [string]$RunDirectory,
    [string]$ServiceBuildDirectory,
    [string]$InstallerBuildDirectory,
    [string]$WpfBuildDirectory,
    [string]$ApprovedBaselineIcon,
    [string]$VerificationTargets,
    [switch]$SkipBuild,
    [string]$ClientSid
)

# An explicitly invoked validation workflow. NetTrace never invokes this file.
# Only its SCM/install worker requests UAC; the real WPF window stays unelevated.
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$verificationRoot = Join-Path $repository 'bin\AppUsage0051Verification'
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
    if (-not $value.StartsWith($verificationRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Artifact path escaped the verification directory.' }
    return $value
}

function Read-Service {
    param([string]$Name=$serviceName)
    $details = & sc.exe queryex $Name 2>&1
    if ($LASTEXITCODE -eq 1060) { return $null }
    if ($LASTEXITCODE -ne 0) { throw "SCM queryex failed for $Name." }
    $text = $details -join [Environment]::NewLine
    if ($text -notmatch '(?m)^\s*STATE\s*:\s*\d+\s+([A-Z_]+)') { throw 'SCM omitted service state.' }
    $state = $Matches[1]
    if ($text -notmatch '(?m)^\s*PID\s*:\s*(\d+)') { throw 'SCM omitted service process ID.' }
    [pscustomobject]@{ State=$state; ProcessId=[int]$Matches[1] }
}

function Invoke-Sc {
    param([string[]]$ScArguments)
    $result = & sc.exe @ScArguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "SCM $($ScArguments[0]) failed: $($result -join ' ')" }
}

function Wait-Service {
    param([string]$State, [int]$Seconds=30)
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt $Seconds) {
        $value = Read-Service
        if (($State -eq 'Absent' -and $null -eq $value) -or ($value -and $value.State -eq $State)) { return $value }
        Start-Sleep -Milliseconds 200
    }
    throw "Production service did not reach $State within the bounded wait."
}

function Write-Json {
    param([string]$Name, [object]$Value)
    $target = Join-Path $RunDirectory $Name
    $Value | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath ($target+'.writing')
    Move-Item -LiteralPath ($target+'.writing') -Destination $target -Force
}

function Wait-Marker {
    param([string]$Name, [int]$Seconds=60)
    $target = Join-Path $RunDirectory $Name
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while (-not (Test-Path -LiteralPath $target) -and $watch.Elapsed.TotalSeconds -lt $Seconds) {
        $failure = Join-Path $RunDirectory 'Production-client.json'
        if ($Name -ne 'Production-client.json' -and (Test-Path -LiteralPath $failure)) {
            $clientReport = Get-Content -LiteralPath $failure -Raw | ConvertFrom-Json
            if (-not $clientReport.Success) { throw ('The normal-token page failed: '+$clientReport.Error) }
        }
        Start-Sleep -Milliseconds 200
    }
    if (-not (Test-Path -LiteralPath $target)) { throw "Expected validation marker did not arrive: $Name." }
}

if (-not $AdminWorker) {
    if ($isAdministrator) { throw 'Run the explicit workflow from a normal terminal; only its install worker requests elevation.' }
    if ((Read-Service) -or (Read-Service $developmentServiceName)) { throw 'Validation will not adopt an existing production or development service.' }
    New-Item -ItemType Directory -Path $verificationRoot -Force | Out-Null
    $RunDirectory = Join-Path $verificationRoot ('run-'+[Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $RunDirectory | Out-Null
    if (-not $ServiceBuildDirectory) { $ServiceBuildDirectory = Join-Path $verificationRoot 'Service' }
    if (-not $InstallerBuildDirectory) { $InstallerBuildDirectory = Join-Path $verificationRoot 'Installer' }
    if (-not $WpfBuildDirectory) { $WpfBuildDirectory = Join-Path $verificationRoot 'Wpf' }
    $ServiceBuildDirectory = Assert-VerificationPath $ServiceBuildDirectory
    $InstallerBuildDirectory = Assert-VerificationPath $InstallerBuildDirectory
    $WpfBuildDirectory = Assert-VerificationPath $WpfBuildDirectory
    if (-not $SkipBuild) {
        & dotnet build (Join-Path $PSScriptRoot 'NetTrace.AppUsageService.csproj') -c Release -o $ServiceBuildDirectory -v minimal
        if ($LASTEXITCODE -ne 0) { throw 'Service build failed.' }
        & dotnet build (Join-Path $repository 'tools\NetTrace.AppUsageInstaller\NetTrace.AppUsageInstaller.csproj') -c Release -o $InstallerBuildDirectory -v minimal
        if ($LASTEXITCODE -ne 0) { throw 'Production installer build failed.' }
        $iconArguments = @()
        if ($ApprovedBaselineIcon -or $VerificationTargets) {
            if (-not $ApprovedBaselineIcon -or -not $VerificationTargets -or -not (Test-Path -LiteralPath $ApprovedBaselineIcon) -or -not (Test-Path -LiteralPath $VerificationTargets)) { throw 'Baseline icon verification requires both existing paths.' }
            $iconArguments = @("-p:ApplicationIcon=$ApprovedBaselineIcon","-p:ApprovedBaselineIcon=$ApprovedBaselineIcon","-p:CustomBeforeMicrosoftCommonTargets=$VerificationTargets")
        }
        & dotnet build (Join-Path $repository 'tests\NetTrace.WpfTests\NetTrace.WpfTests.csproj') -c Release -t:Rebuild -o $WpfBuildDirectory -v minimal @iconArguments
        if ($LASTEXITCODE -ne 0) { throw 'Normal-token WPF harness build failed.' }
    }
    foreach ($path in @((Join-Path $ServiceBuildDirectory 'NetTrace.AppUsageService.exe'),(Join-Path $InstallerBuildDirectory 'NetTrace.AppUsageInstaller.exe'),(Join-Path $WpfBuildDirectory 'NetTrace.WpfTests.exe'))) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Validation output is missing: $path" }
    }
    $ClientSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $workerArguments = '-NoProfile -ExecutionPolicy RemoteSigned -File "'+$PSCommandPath+'" -AdminWorker -RunDirectory "'+$RunDirectory+'" -ServiceBuildDirectory "'+$ServiceBuildDirectory+'" -InstallerBuildDirectory "'+$InstallerBuildDirectory+'" -WpfBuildDirectory "'+$WpfBuildDirectory+'" -ClientSid '+$ClientSid
    $worker = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -Verb RunAs -WindowStyle Hidden -PassThru -ArgumentList $workerArguments
    $client = $null
    try {
        $watch = [Diagnostics.Stopwatch]::StartNew()
        while (-not $worker.HasExited -and $watch.Elapsed.TotalSeconds -lt 240) {
            if (-not $client -and (Test-Path -LiteralPath (Join-Path $RunDirectory 'installation-ready.json'))) {
                $start = [Diagnostics.ProcessStartInfo]::new()
                $start.FileName = Join-Path $WpfBuildDirectory 'NetTrace.WpfTests.exe'
                $start.Arguments = '--app-usage-production-live-validation --output "'+(Join-Path $RunDirectory 'Production-client.json')+'"'
                $start.UseShellExecute = $false
                $start.CreateNoWindow = $true
                $start.RedirectStandardOutput = $true
                $start.RedirectStandardError = $true
                $process = [Diagnostics.Process]::Start($start)
                $client = [pscustomobject]@{ Process=$process; Output=$process.StandardOutput.ReadToEndAsync(); Error=$process.StandardError.ReadToEndAsync() }
                Write-Output 'Unelevated real production App Usage window started.'
            }
            Start-Sleep -Milliseconds 250
            $worker.Refresh()
        }
        if (-not $worker.HasExited) { throw 'Production validation exceeded its bounded workflow.' }
        $summaryPath = Join-Path $RunDirectory 'admin-summary.json'
        if (-not (Test-Path -LiteralPath $summaryPath)) { throw "Administrative cleanup report is missing. Artifacts: $RunDirectory" }
        $summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
        if ($client) {
            if (-not $client.Process.WaitForExit(10000)) { throw 'The bounded normal-token WPF window did not exit.' }
            [IO.File]::WriteAllText((Join-Path $RunDirectory 'Production-client.log'),$client.Output.GetAwaiter().GetResult())
            [IO.File]::WriteAllText((Join-Path $RunDirectory 'Production-client-error.log'),$client.Error.GetAwaiter().GetResult())
        }
        if (-not $summary.Success) { throw ('Production install validation failed: '+$summary.Error+'. Artifacts: '+$RunDirectory) }
        if (-not $client -or $client.Process.ExitCode -ne 0) { throw "Normal-token production page failed. Artifacts: $RunDirectory" }
        $report = Get-Content -LiteralPath (Join-Path $RunDirectory 'Production-client.json') -Raw | ConvertFrom-Json
        if ($report.ClientElevated -or -not $report.Success -or $report.BeforeRestart.Count -ne 3 -or $report.AfterRestart.Count -ne 3 -or -not $report.UnavailableDuringRestart -or -not $report.RecoveredWithoutNavigation -or $report.MaximumConcurrentQueries -ne 1) { throw 'Complete production range/restart evidence is missing.' }
        Write-Output "Production install/uninstall validation passed. Evidence: $RunDirectory"
        $summary | ConvertTo-Json -Depth 10
    }
    finally {
        if ($client) {
            if (-not $client.Process.HasExited) { Stop-Process -Id $client.Process.Id -Force }
            $client.Process.Dispose()
        }
        # Keep the administrative worker alive to reach its uninstall finally.
        $cleanupWatch = [Diagnostics.Stopwatch]::StartNew()
        while (-not $worker.HasExited -and $cleanupWatch.Elapsed.TotalSeconds -lt 105) { $null = $worker.WaitForExit(500) }
        $worker.Dispose()
    }
    return
}

if (-not $isAdministrator) { throw 'The explicitly invoked install worker requires elevation.' }
$RunDirectory = Assert-VerificationPath $RunDirectory
$ServiceBuildDirectory = Assert-VerificationPath $ServiceBuildDirectory
$InstallerBuildDirectory = Assert-VerificationPath $InstallerBuildDirectory
$WpfBuildDirectory = Assert-VerificationPath $WpfBuildDirectory
if ([Security.Principal.WindowsIdentity]::GetCurrent().User.Value -ne $ClientSid) { throw 'The elevated worker must use the intended normal user account.' }
if ((Read-Service) -or (Read-Service $developmentServiceName)) { throw 'The worker will not adopt any pre-existing registration.' }
$installer = Join-Path $InstallerBuildDirectory 'NetTrace.AppUsageInstaller.exe'

function Invoke-Installer {
    param([string]$LogName,[string[]]$ToolArguments)
    $text = & $installer @ToolArguments 2>&1
    $exitCode = $LASTEXITCODE
    $text | Out-File -LiteralPath (Join-Path $RunDirectory $LogName)
    if ($exitCode -ne 0) { throw "Production installer $($ToolArguments[0]) failed: $($text -join ' ')" }
}

function Read-Configuration {
    $service = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
    if (-not $service) { throw 'The production service configuration was absent.' }
    $values = Get-ItemProperty -LiteralPath ('HKLM:\SYSTEM\CurrentControlSet\Services\'+$serviceName)
    $recovery = & sc.exe qfailure $serviceName 2>&1
    if ($LASTEXITCODE -ne 0) { throw 'SCM recovery query failed.' }
    $failureFlag = & sc.exe qfailureflag $serviceName 2>&1
    if ($LASTEXITCODE -ne 0) { throw 'SCM non-crash recovery query failed.' }
    $recoveryText = $recovery -join [Environment]::NewLine
    $recovery | Out-File -LiteralPath (Join-Path $RunDirectory 'scm-recovery.log')
    $failureFlag | Out-File -LiteralPath (Join-Path $RunDirectory 'scm-recovery-flag.log')
    $configurationSnapshot = [pscustomobject]@{ ServiceName=$service.Name; Start=[int]$values.Start; DelayedAutoStart=[int]$values.DelayedAutoStart; Identity=$service.StartName; State=$service.State; ProcessId=[int]$service.ProcessId; BinaryCommandLine=$service.PathName; Recovery=$recoveryText; NonCrashRecovery=($failureFlag -join [Environment]::NewLine) }
    # Retain the installed image for ownership-checked cleanup even if a later
    # independent assertion rejects the configuration.
    $script:lastConfiguration = $configurationSnapshot
    if ($values.Start -ne 2 -or $values.DelayedAutoStart -ne 1 -or $service.StartName -ne 'LocalSystem' -or $service.State -ne 'Running') { throw 'Production delayed-auto/LocalSystem/running configuration did not match.' }
    if ($recoveryText -notmatch '86400' -or $recoveryText -notmatch '5000' -or $recoveryText -notmatch '15000' -or $recoveryText -notmatch '60000' -or $values.FailureActionsOnNonCrashFailures -ne 1) { throw 'Production SCM recovery policy did not match.' }
    $configurationSnapshot
}

function Assert-WorkflowRegistration {
    $expected = $secondConfiguration
    if (-not $expected) { $expected = $configuration }
    if (-not $expected) { $expected = $lastConfiguration }
    $service = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
    if (-not $expected -or -not $service -or $service.PathName -ne $expected.BinaryCommandLine -or $service.StartName -ne 'LocalSystem') {
        throw 'Production registration ownership changed; validation will not stop or start another service.'
    }
}

function Stop-ProductionLifetime {
    Assert-WorkflowRegistration
    $current = Read-Service
    if ($current.State -ne 'STOPPED') {
        Invoke-Sc @('stop',$serviceName)
        Wait-Service 'STOPPED' | Out-Null
    }
    if ($current.ProcessId -gt 0) {
        try { $process = [Diagnostics.Process]::GetProcessById($current.ProcessId) }
        catch [ArgumentException] { return }
        try { if (-not $process.WaitForExit(10000)) { throw 'The stopped collector process did not finish exiting.' } }
        finally { $process.Dispose() }
    }
}

$attemptedInstall = $false
$success = $false
$errorText = $null
$configuration = $null
$secondConfiguration = $null
$lastConfiguration = $null
$existingDatabaseBeforeInstall = Test-Path -LiteralPath $databasePath -PathType Leaf
$databaseBeforeUninstall = $null
$databaseAfterUninstall = $null
$databasePreserved = $false
$serviceAbsent = $false
$scmQuery1060 = $false
$noServiceProcess = $false
$noPipeServer = $false
$ownedSessionAbsent = $false
$serviceProcessIds = @()
$stoppedDurationSeconds = 0
Start-Transcript -LiteralPath (Join-Path $RunDirectory 'admin-workflow.log') -Force | Out-Null
try {
    $attemptedInstall = $true
    Invoke-Installer 'install-first.log' @('install','--source',$ServiceBuildDirectory,'--client-sid',$ClientSid)
    $configuration = Read-Configuration
    $serviceProcessIds += $configuration.ProcessId
    Invoke-Installer 'install-second.log' @('install','--source',$ServiceBuildDirectory,'--client-sid',$ClientSid)
    $secondConfiguration = Read-Configuration
    $serviceProcessIds += $secondConfiguration.ProcessId
    if ($configuration.BinaryCommandLine -ne $secondConfiguration.BinaryCommandLine) { throw 'An identical kit created a different installed service image.' }
    if (-not (Test-Path -LiteralPath $databasePath -PathType Leaf)) { throw 'Production persistence did not create or preserve the application database.' }
    Write-Json 'installation-ready.json' $secondConfiguration
    Wait-Marker 'client-ready-for-restart.json' 85
    Stop-ProductionLifetime
    $stoppedWatch = [Diagnostics.Stopwatch]::StartNew()
    Write-Json 'service-stopped.json' @{}
    Wait-Marker 'client-observed-unavailable.json' 30
    while ($stoppedWatch.Elapsed.TotalSeconds -lt 6) { Start-Sleep -Milliseconds 200 }
    $stoppedDurationSeconds = $stoppedWatch.Elapsed.TotalSeconds
    Assert-WorkflowRegistration
    Invoke-Sc @('start',$serviceName)
    $serviceProcessIds += [int](Wait-Service 'RUNNING').ProcessId
    Write-Json 'service-restarted.json' @{}
    Wait-Marker 'Production-client.json' 85
    $clientReport = Get-Content -LiteralPath (Join-Path $RunDirectory 'Production-client.json') -Raw | ConvertFrom-Json
    if (-not $clientReport.Success -or $clientReport.ClientElevated) { throw ('Normal-token production page validation failed: '+$clientReport.Error) }
    $success = $true
}
catch { $errorText=$_.Exception.Message; Write-Output ('Validation error: '+$errorText) }
finally {
    try {
        if ($attemptedInstall) {
            $current = Read-Service
            if ($current -and $current.State -ne 'STOPPED' -and ($secondConfiguration -or $configuration -or $lastConfiguration)) {
                Stop-ProductionLifetime
            }
            if (Test-Path -LiteralPath $databasePath -PathType Leaf) {
                $databaseBeforeUninstall = [pscustomobject]@{ Path=$databasePath; Length=(Get-Item -LiteralPath $databasePath).Length; Sha256=(Get-FileHash -LiteralPath $databasePath -Algorithm SHA256).Hash }
            }
            Invoke-Installer 'uninstall.log' @('uninstall')
            Wait-Service 'Absent' | Out-Null
            if (Test-Path -LiteralPath $databasePath -PathType Leaf) {
                $databaseAfterUninstall = [pscustomobject]@{ Path=$databasePath; Length=(Get-Item -LiteralPath $databasePath).Length; Sha256=(Get-FileHash -LiteralPath $databasePath -Algorithm SHA256).Hash }
            }
            $databasePreserved = $null -ne $databaseBeforeUninstall -and $null -ne $databaseAfterUninstall -and $databaseBeforeUninstall.Length -eq $databaseAfterUninstall.Length -and $databaseBeforeUninstall.Sha256 -eq $databaseAfterUninstall.Sha256
            Invoke-Installer 'uninstall-second.log' @('uninstall')
        }
        & sc.exe query $serviceName 2>&1 | Out-File -LiteralPath (Join-Path $RunDirectory 'final-scm-query.log')
        $scmQuery1060 = $LASTEXITCODE -eq 1060
        $serviceAbsent = $null -eq (Read-Service)
        & logman.exe query $sessionName -ets 2>&1 | Out-File -LiteralPath (Join-Path $RunDirectory 'final-session-query.log')
        $ownedSessionAbsent = $LASTEXITCODE -ne 0
        $remaining = @(Get-CimInstance Win32_Process -Filter "Name='NetTrace.AppUsageService.exe'" | Where-Object { $_.ProcessId -in $serviceProcessIds -or ($_.ExecutablePath -and $_.ExecutablePath.StartsWith($installationRoot+'\',[StringComparison]::OrdinalIgnoreCase)) })
        $noServiceProcess = $remaining.Count -eq 0
        $noPipeServer = @(Get-ChildItem -LiteralPath '\\.\pipe\' | Where-Object Name -eq $pipeName).Count -eq 0
        if (-not $scmQuery1060 -or -not $serviceAbsent -or -not $ownedSessionAbsent -or -not $noServiceProcess -or -not $noPipeServer) { throw 'Production service/session/process/pipe cleanup was incomplete.' }
        if (-not $databasePreserved) { throw 'Uninstall did not preserve the stopped database byte for byte.' }
    }
    catch { $success=$false; $errorText=($errorText+' Cleanup verification failed: '+$_.Exception.Message).Trim() }
    Write-Json 'admin-summary.json' ([pscustomobject]@{ Success=$success; Error=$errorText; FirstConfiguration=$configuration; SecondConfiguration=$secondConfiguration; StoppedDurationSeconds=$stoppedDurationSeconds; ExistingDatabaseBeforeInstall=$existingDatabaseBeforeInstall; DatabaseBeforeUninstall=$databaseBeforeUninstall; DatabaseAfterUninstall=$databaseAfterUninstall; DatabasePreserved=$databasePreserved; ServiceAbsent=$serviceAbsent; ScmQuery1060=$scmQuery1060; OwnedSessionAbsent=$ownedSessionAbsent; NoServiceProcess=$noServiceProcess; NoPipeServer=$noPipeServer })
    Stop-Transcript | Out-Null
}
if (-not $success) { exit 1 }
