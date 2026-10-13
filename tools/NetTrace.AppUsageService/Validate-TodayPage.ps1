[CmdletBinding()]
param(
    [switch]$AdminWorker,
    [string]$RunDirectory,
    [string]$ServiceBuildDirectory,
    [string]$WpfBuildDirectory,
    [string]$ApprovedBaselineIcon,
    [string]$VerificationTargets,
    [switch]$SkipBuild,
    [string]$ClientSid
)

$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$verificationRoot = Join-Path $repository 'bin\AppUsage00441Verification'
$serviceName = 'NetTraceAppUsageCollectorDev'
$sessionName = 'NetTrace-AppUsage003-Collector-938F90CEB2A34EF7A67BA5A43521EE77'
$pipeName = 'NetTrace.AppUsage003.Service.938F90CEB2A34EF7A67BA5A43521EE77'
$isAdministrator = ([Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

function Read-TestService {
    param([switch]$IncludeConfig)
    $details = & sc.exe queryex $serviceName 2>&1
    if ($LASTEXITCODE -eq 1060) { return $null }
    if ($LASTEXITCODE -ne 0) { throw 'SCM queryex failed.' }
    $text = $details -join [Environment]::NewLine
    if ($text -notmatch '(?m)^\s*STATE\s*:\s*\d+\s+([A-Z_]+)') { throw 'SCM omitted service state.' }
    $state = $Matches[1]
    if ($text -notmatch '(?m)^\s*PID\s*:\s*(\d+)') { throw 'SCM omitted process ID.' }
    $processId = [int]$Matches[1]
    $descriptionOutput = & sc.exe qdescription $serviceName 2>&1
    if ($LASTEXITCODE -ne 0) { throw 'SCM description query failed.' }
    $description = ''
    if (($descriptionOutput -join [Environment]::NewLine) -match '(?m)^DESCRIPTION:\s*(.*)$') { $description = $Matches[1].Trim() }
    $pathName = ''
    if ($IncludeConfig) {
        $config = & sc.exe qc $serviceName 2>&1
        if ($LASTEXITCODE -ne 0 -or ($config -join [Environment]::NewLine) -notmatch '(?m)^\s*BINARY_PATH_NAME\s*:\s*(.*)$') { throw 'SCM configuration query failed.' }
        $pathName = $Matches[1].Trim()
    }
    [pscustomobject]@{ State=$state; ProcessId=$processId; Description=$description; PathName=$pathName }
}

function Invoke-Sc {
    param([string[]]$Arguments)
    $result = & sc.exe @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "SCM command failed: $($Arguments[0]): $($result -join ' ')" }
}

function Wait-Service {
    param([string]$State, [int]$Seconds=25)
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt $Seconds) {
        $value = Read-TestService
        if (($State -eq 'Absent' -and $null -eq $value) -or ($value -and $value.State -eq $State)) { return $value }
        Start-Sleep -Milliseconds 200
    }
    throw "Service did not reach $State within the bounded wait."
}

function Assert-VerificationPath {
    param([string]$Path)
    $value = [IO.Path]::GetFullPath($Path)
    if (-not $value.StartsWith($verificationRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Artifact path escaped the verification directory.' }
    return $value
}

function Test-OwnedImage {
    param([string]$PathName,[string]$BinaryDirectory)
    $expected = Join-Path $BinaryDirectory 'NetTrace.AppUsageService.exe'
    # SCM may omit quotes for a path without spaces. Match the complete image
    # plus an argument boundary, never a substring or a path-prefix sibling.
    return $PathName.Equals($expected,[StringComparison]::OrdinalIgnoreCase) -or
        $PathName.Equals(('"'+$expected+'"'),[StringComparison]::OrdinalIgnoreCase) -or
        $PathName.StartsWith($expected+' ',[StringComparison]::OrdinalIgnoreCase) -or
        $PathName.StartsWith(('"'+$expected+'" '),[StringComparison]::OrdinalIgnoreCase)
}

if (-not $AdminWorker) {
    if ($isAdministrator) { throw 'Run from a normal terminal; only the explicit SCM worker requests elevation.' }
    if (Read-TestService) { throw 'A pre-existing development service will not be modified.' }
    New-Item -ItemType Directory -Path $verificationRoot -Force | Out-Null
    $RunDirectory = Join-Path $verificationRoot ('run-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $RunDirectory | Out-Null
    if (-not $ServiceBuildDirectory) { $ServiceBuildDirectory = Join-Path $verificationRoot 'Service' }
    if (-not $WpfBuildDirectory) { $WpfBuildDirectory = Join-Path $verificationRoot 'Wpf' }
    $ServiceBuildDirectory = Assert-VerificationPath $ServiceBuildDirectory
    $WpfBuildDirectory = Assert-VerificationPath $WpfBuildDirectory
    if (-not $SkipBuild) {
        & dotnet build (Join-Path $PSScriptRoot 'NetTrace.AppUsageService.csproj') -c Release -o $ServiceBuildDirectory -v minimal
        if ($LASTEXITCODE -ne 0) { throw 'Service build failed.' }
        $iconArguments = @()
        if ($ApprovedBaselineIcon -or $VerificationTargets) {
            if (-not $ApprovedBaselineIcon -or -not $VerificationTargets -or -not (Test-Path -LiteralPath $ApprovedBaselineIcon) -or -not (Test-Path -LiteralPath $VerificationTargets)) { throw 'Temporary baseline icon validation requires both existing paths.' }
            $iconArguments = @("-p:ApplicationIcon=$ApprovedBaselineIcon","-p:ApprovedBaselineIcon=$ApprovedBaselineIcon","-p:CustomBeforeMicrosoftCommonTargets=$VerificationTargets")
        }
        & dotnet build (Join-Path $repository 'tests\NetTrace.WpfTests\NetTrace.WpfTests.csproj') -c Release -o $WpfBuildDirectory -v minimal @iconArguments
        if ($LASTEXITCODE -ne 0) { throw 'Normal-token WPF harness build failed.' }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $ServiceBuildDirectory 'NetTrace.AppUsageService.exe')) -or -not (Test-Path -LiteralPath (Join-Path $WpfBuildDirectory 'NetTrace.WpfTests.exe'))) { throw 'Separate validation outputs are missing.' }
    $ClientSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $workerArguments = '-NoProfile -ExecutionPolicy RemoteSigned -File "' + $PSCommandPath + '" -AdminWorker -RunDirectory "' + $RunDirectory + '" -ServiceBuildDirectory "' + $ServiceBuildDirectory + '" -WpfBuildDirectory "' + $WpfBuildDirectory + '" -ClientSid ' + $ClientSid
    $worker = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -Verb RunAs -WindowStyle Hidden -PassThru -ArgumentList $workerArguments
    $client = $null
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $databaseAccessDenied = $false
    try {
        while (-not $worker.HasExited -and $watch.Elapsed.TotalSeconds -lt 180) {
            $service = Read-TestService
            if ($service -and $service.Description -eq 'AppUsage00441:Today' -and -not $client) {
                $database = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) ('NetTrace\AppUsage00441Dev\' + [IO.Path]::GetFileName($RunDirectory) + '\Data\NetTrace\app-usage.db')
                try { $stream = [IO.File]::OpenRead($database); $stream.Dispose(); throw 'Normal WPF client unexpectedly opened the protected validation database.' }
                catch [UnauthorizedAccessException] { $databaseAccessDenied = $true }
                $start = [Diagnostics.ProcessStartInfo]::new()
                $start.FileName = Join-Path $WpfBuildDirectory 'NetTrace.WpfTests.exe'
                $start.Arguments = '--app-usage-today-live-validation --output "' + (Join-Path $RunDirectory 'Today-client.json') + '" --screenshots "' + (Join-Path $RunDirectory 'Screenshots') + '"'
                $start.UseShellExecute = $false
                $start.CreateNoWindow = $true
                $start.RedirectStandardOutput = $true
                $start.RedirectStandardError = $true
                $process = [Diagnostics.Process]::Start($start)
                $client = [pscustomobject]@{ Process=$process; Output=$process.StandardOutput.ReadToEndAsync(); Error=$process.StandardError.ReadToEndAsync() }
                Write-Output 'Unelevated real Today page validation started.'
            }
            Start-Sleep -Milliseconds 250
            $worker.Refresh()
        }
        if (-not $worker.HasExited) { throw 'Administrative validation exceeded its bounded workflow.' }
        $summaryPath = Join-Path $RunDirectory 'admin-summary.json'
        if (-not (Test-Path -LiteralPath $summaryPath)) { throw 'Administrative cleanup report is missing.' }
        $summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
        if (-not $client -or -not $client.Process.WaitForExit(10000)) { throw 'The bounded normal-token WPF client did not exit.' }
        [IO.File]::WriteAllText((Join-Path $RunDirectory 'Today-client.log'), $client.Output.GetAwaiter().GetResult())
        [IO.File]::WriteAllText((Join-Path $RunDirectory 'Today-client-error.log'), $client.Error.GetAwaiter().GetResult())
        if ($client.Process.ExitCode -ne 0) { throw "Today page failed. Artifacts: $RunDirectory" }
        $report = Get-Content -LiteralPath (Join-Path $RunDirectory 'Today-client.json') -Raw | ConvertFrom-Json
        if ($report.ClientElevated -or -not $report.Success -or -not $report.SummaryBound -or -not $report.LogicalTotalIncreased -or -not $report.ReturnFresh -or $report.HiddenQueriesAfter -ne $report.HiddenQueriesBefore -or $report.HiddenDurationSeconds -lt 8 -or -not $report.NoOverlap -or -not $report.CompactNoHorizontalOverflow -or -not $report.VerticalScrollWorked) { throw 'Complete normal-token Today page evidence is missing.' }
        if (-not $databaseAccessDenied -or -not $summary.Success -or -not $summary.ServiceAbsent -or -not $summary.ScmQuery1060 -or -not $summary.NoServiceProcess -or -not $summary.NoPipeServer -or -not $summary.OwnedSessionAbsent -or -not $summary.OwnedDatabaseRemoved) { throw ('Today page validation failed: ' + $summary.Error + '. Artifacts: ' + $RunDirectory) }
        $absentOutput = Join-Path $RunDirectory 'Absent-client.json'
        & (Join-Path $WpfBuildDirectory 'NetTrace.WpfTests.exe') --app-usage-today-live-validation --absent --output $absentOutput
        if ($LASTEXITCODE -ne 0) { throw 'Absent-service real WPF page did not display its safe unavailable state.' }
        $absent = Get-Content -LiteralPath $absentOutput -Raw | ConvertFrom-Json
        if ($absent.ClientElevated -or -not $absent.UnavailableDisplayed) { throw 'Absent-service normal WPF evidence was invalid.' }
        [pscustomobject]@{ ClientElevated=$false; DirectDatabaseAccessDenied=$databaseAccessDenied; AbsentServiceHandled=$true; EvidenceDirectory=$RunDirectory } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $RunDirectory 'client-summary.json')
        Write-Output "Today UI validation passed. Evidence: $RunDirectory"
        $summary | ConvertTo-Json -Depth 8
    }
    finally {
        if ($client) {
            if (-not $client.Process.HasExited) { Stop-Process -Id $client.Process.Id -Force }
            $client.Process.Dispose()
        }
        # The SCM worker always performs ownership-checked cleanup in finally.
        # Wait for its bounded workflow even if the normal-token client failed.
        $cleanupWatch = [Diagnostics.Stopwatch]::StartNew()
        while (-not $worker.HasExited -and $cleanupWatch.Elapsed.TotalSeconds -lt 95) { $null = $worker.WaitForExit(500) }
        $worker.Dispose()
    }
    return
}

if (-not $isAdministrator) { throw 'The SCM worker needs its explicitly elevated token.' }
$RunDirectory = Assert-VerificationPath $RunDirectory
$ServiceBuildDirectory = Assert-VerificationPath $ServiceBuildDirectory
$WpfBuildDirectory = Assert-VerificationPath $WpfBuildDirectory
$null = [Security.Principal.SecurityIdentifier]::new($ClientSid)
if (Read-TestService) { throw 'A pre-existing development service will not be altered.' }
$dataDirectory = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'NetTrace'
$stageRoot = Join-Path $dataDirectory 'AppUsage00441Dev'
foreach ($path in @($dataDirectory,$stageRoot)) {
    if ((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Protected staging may not traverse a reparse point.' }
}
$stage = [IO.Path]::GetFullPath((Join-Path $stageRoot ([IO.Path]::GetFileName($RunDirectory))))
if (-not $stage.StartsWith([IO.Path]::GetFullPath($stageRoot) + '\', [StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $stage)) { throw 'Staging must be a new owned child directory.' }
$stageBinary = Join-Path $stage 'Binary'
$stageLogs = Join-Path $stage 'Logs'
$isolatedDataRoot = Join-Path $stage 'Data'
$databasePath = Join-Path $isolatedDataRoot 'NetTrace\app-usage.db'

function Protect-Directory {
    param([string]$Directory,[switch]$ServiceOnly)
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
    $acl.SetAccessRuleProtection($true,$false)
    foreach ($sid in @('S-1-5-18','S-1-5-32-544')) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),'FullControl','ContainerInherit,ObjectInherit','None','Allow'))
    }
    if (-not $ServiceOnly) { $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($ClientSid),'ReadAndExecute','ContainerInherit,ObjectInherit','None','Allow')) }
    Set-Acl -LiteralPath $Directory -AclObject $acl
}

function Assert-ProtectedParent {
    param([string]$Directory)
    $writeMask = [Security.AccessControl.FileSystemRights]::Write -bor [Security.AccessControl.FileSystemRights]::Delete -bor [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor [Security.AccessControl.FileSystemRights]::ChangePermissions -bor [Security.AccessControl.FileSystemRights]::TakeOwnership
    foreach ($rule in (Get-Acl -LiteralPath $Directory).Access) {
        $sid = $rule.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value
        if ($rule.AccessControlType -eq 'Allow' -and -not ($rule.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly) -and $sid -notin @('S-1-5-18','S-1-5-32-544') -and ($rule.FileSystemRights -band $writeMask) -ne 0) { throw 'Existing ProgramData parent permits non-administrative writes; validation will not change its ACL.' }
    }
}

function Stop-Lifetime {
    $service = Read-TestService
    if ($service -and $service.State -ne 'STOPPED') { Invoke-Sc @('stop',$serviceName); Wait-Service 'STOPPED' | Out-Null }
}

function Wait-ClientReport {
    param([int]$Seconds=85)
    $path = Join-Path $RunDirectory 'Today-client.json'
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while (-not (Test-Path -LiteralPath $path) -and $watch.Elapsed.TotalSeconds -lt $Seconds) { Start-Sleep -Milliseconds 250 }
    if (-not (Test-Path -LiteralPath $path)) { throw 'Unelevated Today page report did not arrive.' }
    $report = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    if ($report.ClientElevated -or -not $report.Success) { throw ('Normal-token Today page validation failed: '+$report.Error) }
    return $report
}

$createdHere = $false
$createdStage = $false
$serviceProcessIds = @()
$success = $false
$errorText = $null
$serviceAbsent = $false
$scmQuery1060 = $false
$noServiceProcess = $false
$noPipeServer = $false
$ownedSessionAbsent = $false
$ownedDatabaseRemoved = $false
$finalReport = $null
Start-Transcript -LiteralPath (Join-Path $RunDirectory 'admin-workflow.log') -Force | Out-Null
try {
    Assert-ProtectedParent $dataDirectory
    if (Test-Path -LiteralPath $stageRoot) { Assert-ProtectedParent $stageRoot }
    New-Item -ItemType Directory -Path $stageRoot -Force | Out-Null
    Protect-Directory $stageRoot
    New-Item -ItemType Directory -Path $stage | Out-Null
    $createdStage = $true
    Protect-Directory $stage
    New-Item -ItemType Directory -Path $stageBinary,$stageLogs,$isolatedDataRoot | Out-Null
    Protect-Directory $stageBinary
    Protect-Directory $stageLogs
    Protect-Directory $isolatedDataRoot -ServiceOnly
    Copy-Item -Path (Join-Path $ServiceBuildDirectory '*') -Destination $stageBinary -Recurse
    $image = '"' + (Join-Path $stageBinary 'NetTrace.AppUsageService.exe') + '" --client-sid ' + $ClientSid + ' --log "' + (Join-Path $stageLogs 'service.log') + '" --report "' + (Join-Path $stageLogs 'service.json') + '" --data-root "' + $isolatedDataRoot + '"'
    Invoke-Sc @('create',$serviceName,'binPath=',$image,'type=','own','start=','demand','obj=','LocalSystem')
    $createdHere = $true
    Invoke-Sc @('sidtype',$serviceName,'unrestricted')
    Invoke-Sc @('start',$serviceName)
    $serviceProcessIds += [int](Wait-Service 'RUNNING').ProcessId
    $checkpointWatch = [Diagnostics.Stopwatch]::StartNew()
    $serviceLog = Join-Path $stageLogs 'service.log'
    while ($checkpointWatch.Elapsed.TotalSeconds -lt 25) {
        if ((Test-Path -LiteralPath $serviceLog) -and (Get-Content -LiteralPath $serviceLog -Raw).Contains('Application usage checkpoint committed at ')) { break }
        Start-Sleep -Milliseconds 250
    }
    if (-not (Get-Content -LiteralPath $serviceLog -Raw).Contains('Application usage checkpoint committed at ')) { throw 'Passive traffic did not produce a successful checkpoint within 25 seconds.' }
    Invoke-Sc @('description',$serviceName,'AppUsage00441:Today')
    $today = Wait-ClientReport
    Stop-Lifetime
    $finalReport = Get-Content -LiteralPath (Join-Path $stageLogs 'service.json') -Raw | ConvertFrom-Json
    if (-not $finalReport.FinalCheckpointComplete -or -not $finalReport.Persistence.PersistenceAvailable -or $finalReport.Persistence.PersistenceDegraded -or $finalReport.Persistence.PendingKeys -ne 0 -or $finalReport.DatabasePath -ne $databasePath) { throw 'Final isolated persistence health was not successful.' }
    $success = $true
}
catch { $errorText = $_.Exception.Message; Write-Output ('Validation error: ' + $errorText) }
finally {
    try {
        if ($createdHere) {
            $current = Read-TestService -IncludeConfig
            if ($current -and -not (Test-OwnedImage $current.PathName $stageBinary)) { throw 'Registration ownership changed; refusing another service.' }
            Invoke-Sc @('config',$serviceName,'start=','disabled')
            try { Stop-Lifetime }
            catch {
                $current = Read-TestService -IncludeConfig
                if (-not $current -or $current.ProcessId -notin $serviceProcessIds -or -not (Test-OwnedImage $current.PathName $stageBinary)) { throw }
                Stop-Process -Id $current.ProcessId -Force
                Wait-Service 'STOPPED' 10 | Out-Null
            }
            Invoke-Sc @('delete',$serviceName)
            Wait-Service 'Absent' | Out-Null
        }
        if ($createdStage) {
            Copy-Item -Path (Join-Path $stageLogs '*') -Destination $RunDirectory -ErrorAction SilentlyContinue
            & (Join-Path $stageBinary 'NetTrace.AppUsageService.exe') --cleanup-owned-session
            if ($LASTEXITCODE -ne 0) { throw 'Exact-session cleanup failed.' }
        }
        & logman.exe query $sessionName -ets 2>&1 | Out-File -LiteralPath (Join-Path $RunDirectory 'final-session-query.log')
        $ownedSessionAbsent = $LASTEXITCODE -ne 0
        $serviceAbsent = $null -eq (Read-TestService)
        $remaining = @(Get-Process -Name 'NetTrace.AppUsageService' -ErrorAction SilentlyContinue | Where-Object { $_.Id -in $serviceProcessIds -or ($_.Path -and $_.Path.StartsWith($stageBinary+'\',[StringComparison]::OrdinalIgnoreCase)) })
        $noServiceProcess = $remaining.Count -eq 0
        $noPipeServer = @(Get-ChildItem -LiteralPath '\\.\pipe\' | Where-Object Name -eq $pipeName).Count -eq 0
        & sc.exe query $serviceName 2>&1 | Out-File -LiteralPath (Join-Path $RunDirectory 'final-scm-query.log')
        $scmQuery1060 = $LASTEXITCODE -eq 1060
        if (-not $serviceAbsent -or -not $ownedSessionAbsent -or -not $noServiceProcess -or -not $noPipeServer -or -not $scmQuery1060) { throw 'Final service/session/process/pipe cleanup was incomplete.' }
        $resolvedStage = [IO.Path]::GetFullPath($stage)
        if ($createdStage -and $resolvedStage.StartsWith([IO.Path]::GetFullPath($stageRoot)+'\',[StringComparison]::OrdinalIgnoreCase)) {
            if ((Get-Item -LiteralPath $resolvedStage).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Refusing cleanup through a staging reparse point.' }
            $reparseChildren = @(Get-ChildItem -LiteralPath $resolvedStage -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint })
            if ($reparseChildren.Count -ne 0) { throw 'Refusing recursive cleanup through a child reparse point.' }
            Remove-Item -LiteralPath $resolvedStage -Recurse -Force
        }
        $ownedDatabaseRemoved = -not (Test-Path -LiteralPath $isolatedDataRoot)
    }
    catch { $success=$false; $errorText='Cleanup verification failed: '+$_.Exception.Message }
    [pscustomobject]@{ Success=$success; Error=$errorText; SelectedIdentity='LocalSystem'; IsolatedDataRoot=$isolatedDataRoot; FinalPersistence=$finalReport.Persistence; ServiceAbsent=$serviceAbsent; ScmQuery1060=$scmQuery1060; OwnedSessionAbsent=$ownedSessionAbsent; NoServiceProcess=$noServiceProcess; NoPipeServer=$noPipeServer; OwnedDatabaseRemoved=$ownedDatabaseRemoved } | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $RunDirectory 'admin-summary.json')
    Stop-Transcript | Out-Null
}
if (-not $success) { exit 1 }
