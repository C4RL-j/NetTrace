[CmdletBinding()]
param(
    [switch]$AdminWorker,
    [string]$RunDirectory,
    [string]$ServiceBuildDirectory,
    [string]$ClientSid
)

$ErrorActionPreference = 'Stop'
$serviceName = 'NetTraceAppUsageCollectorDev'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$verificationRoot = Join-Path $repository 'bin\AppUsage003Verification'
$isAdministrator = ([Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

function Read-TestService {
    param([switch]$IncludeDescription, [switch]$IncludeConfig)
    $details = & sc.exe queryex $serviceName 2>&1
    if ($LASTEXITCODE -eq 1060) { return $null }
    if ($LASTEXITCODE -ne 0) { throw "SCM queryex failed: $($details -join ' ')" }
    $detailsText = $details -join [Environment]::NewLine
    if ($detailsText -notmatch '(?m)^\s*STATE\s*:\s*\d+\s+([A-Z_]+)' ) { throw 'SCM queryex omitted service state.' }
    $state = switch ($Matches[1]) {
        'RUNNING' { 'Running' }
        'STOPPED' { 'Stopped' }
        default { $Matches[1] }
    }
    if ($detailsText -notmatch '(?m)^\s*PID\s*:\s*(\d+)') { throw 'SCM queryex omitted process ID.' }
    $processId = [int]$Matches[1]
    $description = ''
    $pathName = ''
    if ($IncludeDescription) {
        $descriptionOutput = & sc.exe qdescription $serviceName 2>&1
        if ($LASTEXITCODE -eq 1060) { return $null }
        if ($LASTEXITCODE -ne 0) { throw "SCM description query failed: $($descriptionOutput -join ' ')" }
        $descriptionText = $descriptionOutput -join [Environment]::NewLine
        if ($descriptionText -match '(?m)^DESCRIPTION:\s*(.*)$') { $description = $Matches[1].Trim() }
    }
    if ($IncludeConfig) {
        $configOutput = & sc.exe qc $serviceName 2>&1
        if ($LASTEXITCODE -eq 1060) { return $null }
        if ($LASTEXITCODE -ne 0) { throw "SCM configuration query failed: $($configOutput -join ' ')" }
        $configText = $configOutput -join [Environment]::NewLine
        if ($configText -notmatch '(?m)^\s*BINARY_PATH_NAME\s*:\s*(.*)$') { throw 'SCM configuration omitted the executable path.' }
        $pathName = $Matches[1].Trim()
    }
    [pscustomobject]@{ State=$state; ProcessId=$processId; Description=$description; PathName=$pathName }
}

function Invoke-ServiceCommand {
    param([string[]]$Arguments, [switch]$AllowFailure)
    $result = & sc.exe @Arguments 2>&1
    $commandExit = $LASTEXITCODE
    if ($commandExit -ne 0 -and -not $AllowFailure) { throw "SCM command failed ($commandExit): $($Arguments -join ' '): $($result -join ' ')" }
    return $result
}

function Wait-ServiceState {
    param([string]$State, [int]$Seconds = 45)
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    while ($deadline.Elapsed.TotalSeconds -lt $Seconds) {
        $service = Read-TestService
        if (($State -eq 'Absent' -and $null -eq $service) -or ($null -ne $service -and $service.State -eq $State)) { return $service }
        Start-Sleep -Milliseconds 200
    }
    throw "The test service did not reach $State within $Seconds seconds."
}

function Wait-ReplacementService {
    param([int]$PreviousPid, [int]$Seconds = 60)
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    while ($deadline.Elapsed.TotalSeconds -lt $Seconds) {
        $service = Read-TestService
        if ($service -and $service.State -eq 'Running' -and [int]$service.ProcessId -gt 0 -and
            [int]$service.ProcessId -ne $PreviousPid) { return $service }
        Start-Sleep -Milliseconds 200
    }
    throw "SCM recovery did not start a replacement service process within $Seconds seconds."
}

function Assert-VerificationPath {
    param([string]$Path)
    $resolved = [IO.Path]::GetFullPath($Path)
    if (-not $resolved.StartsWith($verificationRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Validation artifacts must stay inside the repository verification directory.' }
    return $resolved
}

if (-not $AdminWorker) {
    if ($isAdministrator) { throw 'Run this wrapper from a normal terminal; only its explicit SCM worker requests elevation.' }
    if (Read-TestService) { throw 'The development service already exists. No pre-existing service will be modified.' }
    New-Item -ItemType Directory -Path $verificationRoot -Force | Out-Null
    $RunDirectory = Join-Path $verificationRoot ('run-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $RunDirectory | Out-Null
    $ServiceBuildDirectory = Join-Path $verificationRoot 'Service'
    $clientBuildDirectory = Join-Path $verificationRoot 'Client'
    & dotnet build (Join-Path $PSScriptRoot 'NetTrace.AppUsageService.csproj') -c Release -o $ServiceBuildDirectory -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'Service Release build failed.' }
    & dotnet build (Join-Path $repository 'tools\AppUsageDiagnosticClient\AppUsageDiagnosticClient.csproj') -c Release -o $clientBuildDirectory -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'Client Release build failed.' }
    $ClientSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $powershellExecutable = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $workerArguments = '-NoProfile -ExecutionPolicy RemoteSigned -File "' + $PSCommandPath + '" -AdminWorker -RunDirectory "' + $RunDirectory + '" -ServiceBuildDirectory "' + $ServiceBuildDirectory + '" -ClientSid ' + $ClientSid
    $worker = Start-Process -FilePath $powershellExecutable -Verb RunAs -WindowStyle Hidden -PassThru -ArgumentList $workerArguments
    $clients = @{}
    $watch = $null
    $scriptDeadline = [Diagnostics.Stopwatch]::StartNew()
    try {
        while (-not $worker.HasExited -and $scriptDeadline.Elapsed.TotalSeconds -lt 540) {
            $service = Read-TestService -IncludeDescription
            if ($service -and $service.State -eq 'Running') {
                $phase = [string]$service.Description
                $clientOptions = $null
                switch ($phase) {
                    'AppUsage003:Capability' { $clientOptions = '--seconds 20 --controlled' }
                    'AppUsage003:Controlled' { $clientOptions = '--seconds 20 --controlled' }
                    'AppUsage003:Normal' { $clientOptions = '--seconds 20' }
                    'AppUsage003:High' { $clientOptions = '--seconds 20 --stress' }
                    'AppUsage003:Lifecycle' {
                        if ($null -eq $watch) {
                            $stopSignal = Join-Path $RunDirectory 'lifecycle-stop.signal'
                            $clientOptions = '--seconds 240 --recover --stop-when-file "' + $stopSignal + '"'
                            $watch = $true
                        }
                    }
                }
                if ($clientOptions -and -not $clients.ContainsKey($phase)) {
                    $label = $phase.Split(':')[1].ToLowerInvariant()
                    $arguments = $clientOptions + ' --service --cadence-ms 2000 --quiet --output "' + (Join-Path $RunDirectory ($label + '-client.json')) + '"'
                    $startInfo = [Diagnostics.ProcessStartInfo]::new()
                    $startInfo.FileName = Join-Path $clientBuildDirectory 'AppUsageDiagnosticClient.exe'
                    $startInfo.Arguments = $arguments
                    $startInfo.UseShellExecute = $false
                    $startInfo.CreateNoWindow = $true
                    $startInfo.RedirectStandardOutput = $true
                    $startInfo.RedirectStandardError = $true
                    $clientProcess = [Diagnostics.Process]::Start($startInfo)
                    $clients[$phase] = [pscustomobject]@{
                        Process = $clientProcess
                        Output = $clientProcess.StandardOutput.ReadToEndAsync()
                        Error = $clientProcess.StandardError.ReadToEndAsync()
                        Label = $label
                        StartedUtc = [DateTime]::UtcNow
                    }
                    Write-Output "Normal client started for $phase, PID $($clientProcess.Id)."
                }
            }
            Start-Sleep -Milliseconds 500
            $worker.Refresh()
        }
        if (-not $worker.HasExited) { throw 'The bounded administrative validation workflow did not finish.' }
        $summaryPath = Join-Path $RunDirectory 'admin-summary.json'
        if (-not (Test-Path -LiteralPath $summaryPath)) { throw 'Administrative validation did not produce a cleanup report.' }
        $summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
        if (-not $summary.Success -or -not $summary.ServiceAbsent -or -not $summary.OwnedSessionAbsent -or -not $summary.NoServiceProcess -or -not $summary.NoPipeServer -or -not $summary.ScmQuery1060) { throw ('Administrative validation failed: ' + $summary.Error) }
        foreach ($entry in $clients.Values) {
            $client = $entry.Process
            while (-not $client.WaitForExit(1000)) {
                if (([DateTime]::UtcNow - $entry.StartedUtc).TotalSeconds -gt 250) { throw 'A bounded diagnostic client did not finish.' }
            }
            [IO.File]::WriteAllText((Join-Path $RunDirectory ($entry.Label + '-client.log')), $entry.Output.GetAwaiter().GetResult())
            [IO.File]::WriteAllText((Join-Path $RunDirectory ($entry.Label + '-client-error.log')), $entry.Error.GetAwaiter().GetResult())
            $client.Refresh()
            if ($client.ExitCode -ne 0) { throw "Diagnostic client $($client.Id) returned $($client.ExitCode)." }
        }
        if (Read-TestService) { throw 'The temporary service still exists after validation.' }
        $controlled = Get-Content -LiteralPath (Join-Path $RunDirectory 'controlled-client.json') -Raw | ConvertFrom-Json
        if ($controlled.ClientElevated -or @($controlled.ControlledChecks).Count -ne 2 -or @($controlled.ControlledChecks | Where-Object { -not $_.ChildBytesExact -or -not $_.ServerBytesExact -or -not $_.LoopbackPreserved -or -not $_.FamilyPreserved }).Count -ne 0) { throw 'Service controlled-transfer proof failed.' }
        $controlledClientIdentity = @($controlled.LastSnapshot.Applications | ForEach-Object { $_.Processes } |
            Where-Object { $_.ProcessId -eq $controlled.ClientPid -and $_.ExecutablePath -and $_.StartTimeUtc })
        if ($controlledClientIdentity.Count -eq 0) { throw 'Selected service identity did not resolve a live normal-client executable and process lifetime.' }
        $lifecycle = Get-Content -LiteralPath (Join-Path $RunDirectory 'lifecycle-client.json') -Raw | ConvertFrom-Json
        if ($lifecycle.ClientElevated -or @($lifecycle.CollectorInstances).Count -lt 3 -or 'Disconnected' -notin @($lifecycle.Connections.State)) { throw 'Normal-client service reconnect was not established.' }
        # Convert both JSON date formats to offsets before comparison. Windows
        # PowerShell parses ISO Z strings as local DateTime but /Date(...)/ as UTC.
        $killUtc = [DateTimeOffset]$summary.ForcedKillUtc
        $recoveryUtc = [DateTimeOffset]$summary.RecoveryRunningUtc
        $beforeCrashInstances = @($lifecycle.Samples | Where-Object {
            [DateTimeOffset]::Parse([string]$_.TimestampUtc, [Globalization.CultureInfo]::InvariantCulture) -lt $killUtc
        } | ForEach-Object CollectorInstanceId)
        $afterRecovery = @($lifecycle.Samples | Where-Object {
            [DateTimeOffset]::Parse([string]$_.TimestampUtc, [Globalization.CultureInfo]::InvariantCulture) -gt $recoveryUtc -and
            $_.CollectorInstanceId -notin $beforeCrashInstances
        })
        if ($beforeCrashInstances.Count -eq 0 -or $afterRecovery.Count -eq 0) { throw 'Normal client did not reconnect to a fresh collector lifetime after forced-kill recovery.' }
        Write-Output "Service validation passed. Evidence: $RunDirectory"
        $summary | Format-List
    }
    finally {
        foreach ($entry in $clients.Values) {
            $client = $entry.Process
            if (-not $client.HasExited) { Stop-Process -Id $client.Id -Force }
            $client.Dispose()
        }
        $worker.Dispose()
    }
    return
}

if (-not $isAdministrator) { throw 'The SCM worker requires an explicitly elevated token.' }
$RunDirectory = Assert-VerificationPath $RunDirectory
$ServiceBuildDirectory = Assert-VerificationPath $ServiceBuildDirectory
$null = [Security.Principal.SecurityIdentifier]::new($ClientSid)
if (Read-TestService) { throw 'The development service already exists; refusing to alter it.' }
$stageParent = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'NetTrace'
$createdStageParent = -not (Test-Path -LiteralPath $stageParent)
$stageRoot = Join-Path $stageParent 'AppUsage003Dev'
foreach ($ancestor in @($stageParent, $stageRoot)) {
    if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Protected service staging cannot traverse a reparse point.' }
}
$stage = [IO.Path]::GetFullPath((Join-Path $stageRoot ([IO.Path]::GetFileName($RunDirectory))))
if (-not $stage.StartsWith([IO.Path]::GetFullPath($stageRoot) + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Protected staging path escaped its development directory.' }
New-Item -ItemType Directory -Path $stage | Out-Null
$stageBinary = Join-Path $stage 'Binary'
$stageLogs = Join-Path $stage 'Logs'
New-Item -ItemType Directory -Path $stageBinary, $stageLogs | Out-Null
$operatorLog = Join-Path $RunDirectory 'admin-workflow.log'
Start-Transcript -LiteralPath $operatorLog -Force | Out-Null
$createdHere = $false
$selectedIdentity = $null
$testedLocalService = $false
$localServiceResult = 'Not tested'
$measurements = @()
$serviceProcessIds = @()
$errorText = $null
$success = $false
$serviceAbsent = $false
$ownedSessionAbsent = $false
$noServiceProcess = $false
$noPipeServer = $false
$scmQuery1060 = $false
$staleAfterKill = $false
$recoveryWorked = $false
$forcedKillUtc = $null
$recoveryRunningUtc = $null
$sessionName = 'NetTrace-AppUsage003-Collector-938F90CEB2A34EF7A67BA5A43521EE77'

# Configure only this development service. Native structures avoid empty-action
# quoting differences between Windows PowerShell and PowerShell 7 / sc.exe.
Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
public static class AppUsage003RecoveryPolicy {
    [StructLayout(LayoutKind.Sequential)] private struct Actions {
        public uint Reset; public IntPtr Reboot; public IntPtr Command; public uint Count; public IntPtr Items;
    }
    [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern IntPtr OpenSCManager(string machine, string database, uint access);
    [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern IntPtr OpenService(IntPtr manager, string name, uint access);
    [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern bool ChangeServiceConfig2(IntPtr service, uint level, ref Actions actions);
    [DllImport("advapi32.dll")] private static extern bool CloseServiceHandle(IntPtr handle);
    public static void Set(bool enabled) {
        IntPtr manager=OpenSCManager(null,null,1), service=IntPtr.Zero, items=IntPtr.Zero;
        if(manager==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try {
            service=OpenService(manager,"NetTraceAppUsageCollectorDev",0x12);
            if(service==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            items=Marshal.AllocHGlobal(24);
            Marshal.Copy(new int[]{1,10000,1,10000,0,0},0,items,6);
            Actions actions=new Actions{Reset=3600,Count=enabled?3U:0U,Items=items};
            if(!ChangeServiceConfig2(service,2,ref actions)) throw new Win32Exception(Marshal.GetLastWin32Error());
        } finally {
            if(items!=IntPtr.Zero) Marshal.FreeHGlobal(items);
            if(service!=IntPtr.Zero) CloseServiceHandle(service);
            CloseServiceHandle(manager);
        }
    }
}
'@

function Set-ProtectedDirectory {
    param([string]$Directory, [switch]$ServiceWritable)
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($sid in @('S-1-5-18', 'S-1-5-32-544')) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid), 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    }
    $serviceAccess = 'ReadAndExecute'
    if ($ServiceWritable) { $serviceAccess = 'Modify' }
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-5-19'), $serviceAccess, 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($ClientSid), 'ReadAndExecute', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    Set-Acl -LiteralPath $Directory -AclObject $acl
}

function Set-Phase {
    param([string]$Phase)
    Invoke-ServiceCommand @('description', $serviceName, ('AppUsage003:' + $Phase)) | Out-Null
    Write-Output "Phase: $Phase"
}

function New-TestService {
    param([string]$Identity, [string]$Label)
    $script:reportPath = Join-Path $stageLogs ($Label + '-service.json')
    $image = '"' + (Join-Path $stageBinary 'NetTrace.AppUsageService.exe') + '" --client-sid ' + $ClientSid + ' --log "' + (Join-Path $stageLogs 'service.log') + '" --report "' + $script:reportPath + '"'
    Invoke-ServiceCommand @('create', $serviceName, 'binPath=', $image, 'type=', 'own', 'start=', 'demand', 'obj=', $Identity) | Out-Null
    $script:createdHere = $true
    Invoke-ServiceCommand @('sidtype', $serviceName, 'unrestricted') | Out-Null
}

function Start-TestService {
    Invoke-ServiceCommand @('start', $serviceName) | Out-Null
    $running = Wait-ServiceState 'Running'
    $script:serviceProcessIds += [int]$running.ProcessId
    return $running
}

function Stop-TestService {
    $service = Read-TestService
    if ($service -and $service.State -ne 'Stopped') {
        Invoke-ServiceCommand @('stop', $serviceName) -AllowFailure | Out-Null
        Wait-ServiceState 'Stopped' 15 | Out-Null
    }
}

function Delete-TestService {
    Stop-TestService
    Invoke-ServiceCommand @('delete', $serviceName) | Out-Null
    Wait-ServiceState 'Absent' | Out-Null
    $script:createdHere = $false
}

function Measure-ServiceWindow {
    param([string]$Scenario, [int]$Seconds = 24)
    $service = Read-TestService
    $process = [Diagnostics.Process]::GetProcessById([int]$service.ProcessId)
    try {
        $initialCpu = $process.TotalProcessorTime
        $clock = [Diagnostics.Stopwatch]::StartNew()
        Start-Sleep -Seconds $Seconds
        $process.Refresh()
        $cpuSeconds = ($process.TotalProcessorTime - $initialCpu).TotalSeconds
        $workingSet = $process.WorkingSet64
        $duration = $clock.Elapsed.TotalSeconds
        Stop-TestService
        $report = Get-Content -LiteralPath $script:reportPath -Raw | ConvertFrom-Json
        $snapshot = $report.Snapshot
        $measurement = [pscustomobject]@{ Scenario=$Scenario; Seconds=$duration; CpuPercentOfOneCore=100*$cpuSeconds/$duration; WorkingSetMiB=$workingSet/1MB; EventCount=$snapshot.EventCount; EventsPerSecond=$snapshot.EventCount/$duration; LostEvents=$snapshot.LostEventCount; SnapshotRequests=$snapshot.Metrics.SnapshotsServed; SnapshotsPerSecond=$snapshot.Metrics.SnapshotsServed/$duration }
        $script:measurements += $measurement
        Write-Output ($measurement | ConvertTo-Json -Compress)
    }
    finally { $process.Dispose() }
}

function Set-ReportLabel {
    param([string]$Label)
    # Changing the image arguments is an administrator-only SCM operation.
    $script:reportPath = Join-Path $stageLogs ($Label + '-service.json')
    $image = '"' + (Join-Path $stageBinary 'NetTrace.AppUsageService.exe') + '" --client-sid ' + $ClientSid + ' --log "' + (Join-Path $stageLogs 'service.log') + '" --report "' + $script:reportPath + '"'
    Invoke-ServiceCommand @('config', $serviceName, 'binPath=', $image) | Out-Null
}

try {
    # Protect newly created ancestors too, so the normal client cannot replace
    # a service image by renaming a writable parent directory.
    if ($createdStageParent) { Set-ProtectedDirectory $stageParent }
    Set-ProtectedDirectory $stageRoot
    Set-ProtectedDirectory $stage
    Set-ProtectedDirectory $stageBinary
    Set-ProtectedDirectory $stageLogs -ServiceWritable
    Copy-Item -Path (Join-Path $ServiceBuildDirectory '*') -Destination $stageBinary -Recurse
    New-TestService 'NT AUTHORITY\LocalService' 'localservice-capability'
    $testedLocalService = $true
    Set-Phase 'Capability'
    $attributionInsufficient = $false
    try {
        Start-TestService | Out-Null
        Start-Sleep -Seconds 25
        if ((Read-TestService).State -ne 'Running') { throw 'LocalService worker exited during ETW initialization.' }
        $session = & logman.exe query $sessionName -ets 2>&1
        if ($LASTEXITCODE -ne 0) { throw 'LocalService did not own the required ETW session.' }
        $capabilityClientPath = Join-Path $RunDirectory 'capability-client.json'
        if (-not (Test-Path -LiteralPath $capabilityClientPath)) { throw 'LocalService normal-client capability report was not produced.' }
        $capabilityClient = Get-Content -LiteralPath $capabilityClientPath -Raw | ConvertFrom-Json
        if ($capabilityClient.ClientElevated -or @($capabilityClient.ControlledChecks).Count -ne 2 -or
            @($capabilityClient.ControlledChecks | Where-Object { -not $_.ChildBytesExact -or -not $_.ServerBytesExact }).Count -ne 0) {
            throw 'LocalService ETW transfer/client capability proof failed.'
        }
        $clientIdentities = @($capabilityClient.LastSnapshot.Applications | ForEach-Object { $_.Processes } |
            Where-Object { $_.ProcessId -eq $capabilityClient.ClientPid -and $_.ExecutablePath -and $_.StartTimeUtc })
        if ($clientIdentities.Count -eq 0) {
            $attributionInsufficient = $true
            throw 'LocalService captured bytes but could not resolve the live normal-client executable path and start time.'
        }
        $localServiceResult = 'Kernel-Network ETW capture and live user-process identity both succeeded'
        $selectedIdentity = 'LocalService'
        Stop-TestService
    }
    catch {
        $localServiceResult = $_.Exception.Message
        Write-Output "LocalService capability: $localServiceResult"
        $capabilityPath = Join-Path $stageLogs 'localservice-capability-service.json'
        $capability = if (Test-Path -LiteralPath $capabilityPath) { Get-Content -LiteralPath $capabilityPath -Raw | ConvertFrom-Json } else { $null }
        $etwAccessDenied = $null -ne $capability -and $null -ne $capability.Capture -and
            $capability.Capture.AccessDenied -eq $true -and $capability.Capture.NativeErrorCode -eq 5 -and
            $capability.Capture.FailureStage -in @('SessionStart', 'CaptureProcessing')
        if (-not $etwAccessDenied -and -not $attributionInsufficient) {
            throw "LocalService failed without proven ETW or required process-identity insufficiency; refusing to escalate the service identity. $localServiceResult"
        }
        if ($etwAccessDenied) { $localServiceResult = [string]$capability.Capture.Error }
        Delete-TestService
        New-TestService 'LocalSystem' 'selected-identity'
        $selectedIdentity = 'LocalSystem'
    }
    Set-ReportLabel 'controlled'
    Set-Phase 'Controlled'
    Start-TestService | Out-Null
    Start-Sleep -Seconds 25
    Stop-TestService
    Set-ReportLabel 'idle'
    Set-Phase 'Idle'
    Start-TestService | Out-Null
    Measure-ServiceWindow 'Idle (no intentional traffic or client)' 20
    Set-ReportLabel 'normal'
    Set-Phase 'Normal'
    Start-TestService | Out-Null
    Measure-ServiceWindow 'Normal (passive existing activity)' 24
    Set-ReportLabel 'high'
    Set-Phase 'High'
    Start-TestService | Out-Null
    Measure-ServiceWindow 'High (bounded local workload)' 24

    Set-ReportLabel 'lifecycle'
    Set-Phase 'Lifecycle'
    Start-TestService | Out-Null
    Start-Sleep -Seconds 5
    for ($cycle = 0; $cycle -lt 3; $cycle++) {
        Stop-TestService
        Start-Sleep -Seconds 3
        Start-TestService | Out-Null
        Start-Sleep -Seconds 4
        $sessions = @(& logman.exe query -ets 2>&1 | Select-String -SimpleMatch $sessionName)
        if ($sessions.Count -ne 1) { throw 'Repeated restart did not retain exactly one owned ETW session.' }
    }
    # A ten-second restart delay leaves time to inspect the stale session after
    # forced termination. The final no-action entry prevents a rapid retry loop.
    [AppUsage003RecoveryPolicy]::Set($true)
    Invoke-ServiceCommand @('qfailure', $serviceName) | Out-File -LiteralPath (Join-Path $RunDirectory 'temporary-recovery-policy.log')
    $service = Read-TestService
    $killedPid = [int]$service.ProcessId
    $query = & logman.exe query $sessionName -ets 2>&1
    if ($LASTEXITCODE -ne 0 -or $service.State -ne 'Running' -or $killedPid -le 0) { throw 'Forced-kill preconditions failed.' }
    $forcedKillUtc = [DateTime]::UtcNow
    Stop-Process -Id $killedPid -Force
    Start-Sleep -Milliseconds 300
    $query = & logman.exe query $sessionName -ets 2>&1
    $staleAfterKill = $LASTEXITCODE -eq 0
    Write-Output "Forced process termination: PID $killedPid; stale session present=$staleAfterKill."
    $restarted = Wait-ReplacementService $killedPid 60
    $recoveryRunningUtc = [DateTime]::UtcNow
    $serviceProcessIds += [int]$restarted.ProcessId
    Start-Sleep -Seconds 20
    $sessions = @(& logman.exe query -ets 2>&1 | Select-String -SimpleMatch $sessionName)
    if ($sessions.Count -ne 1) { throw 'Forced-kill recovery produced missing or duplicate owned sessions.' }
    $recoveryWorked = $true
    Set-Content -LiteralPath (Join-Path $RunDirectory 'lifecycle-stop.signal') -Value 'Recovery observation complete.'
    Stop-TestService
    $success = $true
}
catch { $errorText = $_.Exception.Message; Write-Output "Validation error: $errorText" }
finally {
    if ($createdHere) {
        try {
            $current = Read-TestService -IncludeConfig
            if ($current -and -not $current.PathName.Contains($stageBinary)) { throw 'Service ownership changed; refusing to remove another registration.' }
            # SCM may already have queued a restart; disabling the temporary
            # registration prevents that queued action while it is removed.
            Invoke-ServiceCommand @('config', $serviceName, 'start=', 'disabled') | Out-Null
            [AppUsage003RecoveryPolicy]::Set($false)
            try { Stop-TestService }
            catch {
                $owned = Read-TestService -IncludeConfig
                if ($owned -and $owned.PathName.Contains($stageBinary) -and [int]$owned.ProcessId -gt 0) {
                    Stop-Process -Id ([int]$owned.ProcessId) -Force
                    Wait-ServiceState 'Stopped' 10 | Out-Null
                }
                else { throw }
            }
            Delete-TestService
        }
        catch { $success = $false; $errorText = 'Service cleanup failed: ' + $_.Exception.Message }
    }
    try {
        # Keep evidence even when the exact-session safety check refuses cleanup.
        Copy-Item -Path (Join-Path $stageLogs '*') -Destination $RunDirectory -ErrorAction SilentlyContinue
        & (Join-Path $stageBinary 'NetTrace.AppUsageService.exe') --cleanup-owned-session
        if ($LASTEXITCODE -ne 0) { throw 'Safe exact-session cleanup did not succeed.' }
        $query = & logman.exe query $sessionName -ets 2>&1
        $ownedSessionAbsent = $LASTEXITCODE -ne 0
        $serviceAbsent = $null -eq (Read-TestService)
        $remaining = @(Get-Process -Name 'NetTrace.AppUsageService' -ErrorAction SilentlyContinue | Where-Object {
            $serviceProcessIds -contains $_.Id -or ($_.Path -and $_.Path.StartsWith($stageBinary, [StringComparison]::OrdinalIgnoreCase))
        })
        $noServiceProcess = $remaining.Count -eq 0
        $pipeName = 'NetTrace.AppUsage003.Service.938F90CEB2A34EF7A67BA5A43521EE77'
        $noPipeServer = @(Get-ChildItem -LiteralPath '\\.\pipe\' -ErrorAction Stop | Where-Object Name -eq $pipeName).Count -eq 0
        & sc.exe query $serviceName 2>&1 | Out-File -LiteralPath (Join-Path $RunDirectory 'final-scm-query.log')
        $scmQuery1060 = $LASTEXITCODE -eq 1060
        if (-not $ownedSessionAbsent -or -not $serviceAbsent -or -not $noServiceProcess -or -not $noPipeServer -or -not $scmQuery1060) { throw 'Final SCM/process/pipe/session cleanup is incomplete.' }
    }
    catch { $success = $false; $errorText = 'Final cleanup verification failed: ' + $_.Exception.Message }
    [pscustomobject]@{ Success=$success; Error=$errorText; TestedLocalService=$testedLocalService; LocalServiceResult=$localServiceResult; SelectedIdentity=$selectedIdentity; Measurements=$measurements; ForcedKillStaleSession=$staleAfterKill; ForcedKillUtc=$forcedKillUtc; ScmRecoveryWorked=$recoveryWorked; RecoveryRunningUtc=$recoveryRunningUtc; ServiceAbsent=$serviceAbsent; OwnedSessionAbsent=$ownedSessionAbsent; NoServiceProcess=$noServiceProcess; NoPipeServer=$noPipeServer; ScmQuery1060=$scmQuery1060; ProtectedStage=$stage; ServiceProcessIds=$serviceProcessIds } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $RunDirectory 'admin-summary.json')
    Stop-Transcript | Out-Null
    # Never delete a computed staging path until its absolute boundary is checked.
    if ($serviceAbsent -and $ownedSessionAbsent -and $noServiceProcess -and $stage.StartsWith([IO.Path]::GetFullPath($stageRoot) + '\', [StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $stage -Recurse -Force }
}
if (-not $success) { exit 1 }
