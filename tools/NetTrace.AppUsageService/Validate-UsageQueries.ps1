[CmdletBinding()]
param(
    [switch]$AdminWorker,
    [string]$RunDirectory,
    [string]$ServiceBuildDirectory,
    [string]$DiagnosticBuildDirectory,
    [string]$ClientSid
)

$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$verificationRoot = Join-Path $repository 'bin\AppUsage0043Verification'
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
    $ServiceBuildDirectory = Join-Path $verificationRoot 'Service'
    $DiagnosticBuildDirectory = Join-Path $verificationRoot 'Client'
    & dotnet build (Join-Path $PSScriptRoot 'NetTrace.AppUsageService.csproj') -c Release -o $ServiceBuildDirectory -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'Service build failed.' }
    & dotnet build (Join-Path $repository 'tools\AppUsageDiagnosticClient\AppUsageDiagnosticClient.csproj') -c Release -o $DiagnosticBuildDirectory -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'Unelevated diagnostic build failed.' }
    $ClientSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $workerArguments = '-NoProfile -ExecutionPolicy RemoteSigned -File "' + $PSCommandPath + '" -AdminWorker -RunDirectory "' + $RunDirectory + '" -ServiceBuildDirectory "' + $ServiceBuildDirectory + '" -DiagnosticBuildDirectory "' + $DiagnosticBuildDirectory + '" -ClientSid ' + $ClientSid
    $worker = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -Verb RunAs -WindowStyle Hidden -PassThru -ArgumentList $workerArguments
    $clients = @{}
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $databaseAccessDenied = $false
    try {
        while (-not $worker.HasExited -and $watch.Elapsed.TotalSeconds -lt 180) {
            $service = Read-TestService
            if ($service -and $service.Description -in @('AppUsage0043:Warm','AppUsage0043:Reads','AppUsage0043:RestartGap')) {
                $label = $service.Description.Split(':')[1]
                if (-not $clients.ContainsKey($label)) {
                    if ($label -eq 'Warm') {
                        $database = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) ('NetTrace\AppUsage0043Dev\' + [IO.Path]::GetFileName($RunDirectory) + '\Data\NetTrace\app-usage.db')
                        try { $stream = [IO.File]::OpenRead($database); $stream.Dispose(); throw 'Normal client unexpectedly opened the protected validation database.' }
                        catch [UnauthorizedAccessException] { $databaseAccessDenied = $true }
                    }
                    $start = [Diagnostics.ProcessStartInfo]::new()
                    $start.FileName = Join-Path $DiagnosticBuildDirectory 'AppUsageDiagnosticClient.exe'
                    $queryOptions = if ($label -eq 'Warm') { '--app-usage-all --query-count 1 --seconds 20' } elseif ($label -eq 'Reads') { '--app-usage-all --query-count 5 --seconds 45 --quiet' } else { '--app-usage Today --query-count 3 --seconds 35 --recover --quiet' }
                    $start.Arguments = '--service ' + $queryOptions + ' --output "' + (Join-Path $RunDirectory ($label + '-client.json')) + '"'
                    $start.UseShellExecute = $false
                    $start.CreateNoWindow = $true
                    $start.RedirectStandardOutput = $true
                    $start.RedirectStandardError = $true
                    $process = [Diagnostics.Process]::Start($start)
                    $clients[$label] = [pscustomobject]@{ Process=$process; Output=$process.StandardOutput.ReadToEndAsync(); Error=$process.StandardError.ReadToEndAsync(); Label=$label }
                    Write-Output "Unelevated App Usage client started: $label."
                }
            }
            Start-Sleep -Milliseconds 250
            $worker.Refresh()
        }
        if (-not $worker.HasExited) { throw 'Administrative validation exceeded its bounded workflow.' }
        $summaryPath = Join-Path $RunDirectory 'admin-summary.json'
        if (-not (Test-Path -LiteralPath $summaryPath)) { throw 'Administrative cleanup report is missing.' }
        $summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
        foreach ($entry in $clients.Values) {
            if (-not $entry.Process.WaitForExit(10000)) { throw 'A bounded normal-token diagnostic did not exit.' }
            [IO.File]::WriteAllText((Join-Path $RunDirectory ($entry.Label + '-client.log')), $entry.Output.GetAwaiter().GetResult())
            [IO.File]::WriteAllText((Join-Path $RunDirectory ($entry.Label + '-client-error.log')), $entry.Error.GetAwaiter().GetResult())
            if ($entry.Process.ExitCode -ne 0) { throw "Query client $($entry.Label) failed. Artifacts: $RunDirectory" }
            $report = Get-Content -LiteralPath (Join-Path $RunDirectory ($entry.Label + '-client.json')) -Raw | ConvertFrom-Json
            if ($report.ClientElevated -or $report.CompletedQueries -ne $report.RequestedQueries) { throw 'Normal-token complete query evidence is missing.' }
        }
        if ($clients.Count -ne 3 -or -not $databaseAccessDenied -or -not $summary.Success -or -not $summary.ServiceAbsent -or -not $summary.ScmQuery1060 -or -not $summary.NoServiceProcess -or -not $summary.NoPipeServer -or -not $summary.OwnedSessionAbsent -or -not $summary.OwnedDatabaseRemoved) { throw ('Query validation failed: ' + $summary.Error + '. Artifacts: ' + $RunDirectory) }
        $absentOutput = Join-Path $RunDirectory 'Absent-client.json'
        & (Join-Path $DiagnosticBuildDirectory 'AppUsageDiagnosticClient.exe') --service --app-usage Today --seconds 5 --output $absentOutput
        if ($LASTEXITCODE -ne 1) { throw 'Absent-service diagnostic did not return its safe unavailable exit code.' }
        $absent = Get-Content -LiteralPath $absentOutput -Raw | ConvertFrom-Json
        if ($absent.ClientElevated -or $absent.CompletedQueries -ne 0) { throw 'Absent-service normal-client evidence was invalid.' }
        [pscustomobject]@{ ClientElevated=$false; DirectDatabaseAccessDenied=$databaseAccessDenied; AbsentServiceHandled=$true; QueryCounts=@(3,15,3); EvidenceDirectory=$RunDirectory } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $RunDirectory 'client-summary.json')
        Write-Output "Read-only IPC validation passed. Evidence: $RunDirectory"
        $summary | ConvertTo-Json -Depth 8
    }
    finally {
        foreach ($entry in $clients.Values) {
            if (-not $entry.Process.HasExited) { Stop-Process -Id $entry.Process.Id -Force }
            $entry.Process.Dispose()
        }
        $worker.Dispose()
    }
    return
}

if (-not $isAdministrator) { throw 'The SCM worker needs its explicitly elevated token.' }
$RunDirectory = Assert-VerificationPath $RunDirectory
$ServiceBuildDirectory = Assert-VerificationPath $ServiceBuildDirectory
$DiagnosticBuildDirectory = Assert-VerificationPath $DiagnosticBuildDirectory
$null = [Security.Principal.SecurityIdentifier]::new($ClientSid)
if (Read-TestService) { throw 'A pre-existing development service will not be altered.' }
$dataDirectory = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'NetTrace'
$stageRoot = Join-Path $dataDirectory 'AppUsage0043Dev'
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
    param([string]$Label,[int]$Seconds=50)
    $path = Join-Path $RunDirectory ($Label + '-client.json')
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while (-not (Test-Path -LiteralPath $path) -and $watch.Elapsed.TotalSeconds -lt $Seconds) { Start-Sleep -Milliseconds 250 }
    if (-not (Test-Path -LiteralPath $path)) { throw "Unelevated $Label query report did not arrive." }
    $report = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    if ($report.ClientElevated -or $report.CompletedQueries -ne $report.RequestedQueries) { throw 'Normal-token query completion failed.' }
    foreach ($query in @($report.Queries | Where-Object State -eq 'Connected')) {
        if ($query.Result.Availability -ne 'Available' -or $query.Result.TrackedApplicationCount -lt 1) { throw 'A persisted application result was unavailable or empty.' }
    }
    return $report
}

function Read-Resources {
    $service = Read-TestService
    $process = [Diagnostics.Process]::GetProcessById([int]$service.ProcessId)
    try { $process.Refresh(); return [pscustomobject]@{ HandleCount=$process.HandleCount; PrivateBytes=$process.PrivateMemorySize64; WorkingSetBytes=$process.WorkingSet64 } }
    finally { $process.Dispose() }
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
$serviceBaseline = $null
$serviceFinal = $null
$finalReport = $null
Start-Transcript -LiteralPath (Join-Path $RunDirectory 'admin-workflow.log') -Force | Out-Null
try {
    New-Item -ItemType Directory -Path $stageRoot -Force | Out-Null
    Assert-ProtectedParent $dataDirectory
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
    Invoke-Sc @('description',$serviceName,'AppUsage0043:Warm')
    $warm = Wait-ClientReport 'Warm' 25
    $serviceBaseline = Read-Resources
    Invoke-Sc @('description',$serviceName,'AppUsage0043:Reads')
    $reads = Wait-ClientReport 'Reads'
    $serviceFinal = Read-Resources
    if ($serviceFinal.HandleCount - $serviceBaseline.HandleCount -gt 64 -or $serviceFinal.PrivateBytes - $serviceBaseline.PrivateBytes -gt 32MB) { throw 'The modest live query window exceeded its bounded resource-growth guard.' }
    Invoke-Sc @('description',$serviceName,'AppUsage0043:RestartGap')
    Stop-Lifetime
    Copy-Item -LiteralPath (Join-Path $stageLogs 'service.json') -Destination (Join-Path $stageLogs 'before-restart-service.json')
    Start-Sleep -Seconds 4
    Invoke-Sc @('start',$serviceName)
    $serviceProcessIds += [int](Wait-Service 'RUNNING').ProcessId
    $recovery = Wait-ClientReport 'RestartGap' 40
    if (@($recovery.Connections | Where-Object { $_.State -ne 'Connected' }).Count -eq 0) { throw 'The bounded restart diagnostic did not observe a service interruption.' }
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
    [pscustomobject]@{ Success=$success; Error=$errorText; SelectedIdentity='LocalSystem'; IsolatedDataRoot=$isolatedDataRoot; ServiceWarmBaseline=$serviceBaseline; ServiceAfterRepeatedQueries=$serviceFinal; FinalPersistence=$finalReport.Persistence; ServiceAbsent=$serviceAbsent; ScmQuery1060=$scmQuery1060; OwnedSessionAbsent=$ownedSessionAbsent; NoServiceProcess=$noServiceProcess; NoPipeServer=$noPipeServer; OwnedDatabaseRemoved=$ownedDatabaseRemoved } | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $RunDirectory 'admin-summary.json')
    Stop-Transcript | Out-Null
}
if (-not $success) { exit 1 }
