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
$verificationRoot = Join-Path $repository 'bin\AppUsage0042Verification'
$serviceName = 'NetTraceAppUsageCollectorDev'
$sessionName = 'NetTrace-AppUsage003-Collector-938F90CEB2A34EF7A67BA5A43521EE77'
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
    param([string]$State, [int]$Seconds=20)
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
    # Windows PowerShell/sc.exe can remove unnecessary quotes around a path
    # with no spaces. Require the exact executable plus an argument boundary.
    return $PathName.Equals($expected,[StringComparison]::OrdinalIgnoreCase) -or
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
    $DiagnosticBuildDirectory = Join-Path $verificationRoot 'Diagnostic'
    & dotnet build (Join-Path $PSScriptRoot 'NetTrace.AppUsageService.csproj') -c Release -o $ServiceBuildDirectory -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'Service build failed.' }
    & dotnet build (Join-Path $repository 'tools\AppUsagePersistenceDiagnostic\AppUsagePersistenceDiagnostic.csproj') -c Release -o $DiagnosticBuildDirectory -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'Persistence diagnostic build failed.' }
    $ClientSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $workerArguments = '-NoProfile -ExecutionPolicy RemoteSigned -File "' + $PSCommandPath + '" -AdminWorker -RunDirectory "' + $RunDirectory + '" -ServiceBuildDirectory "' + $ServiceBuildDirectory + '" -DiagnosticBuildDirectory "' + $DiagnosticBuildDirectory + '" -ClientSid ' + $ClientSid
    $worker = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -Verb RunAs -WindowStyle Hidden -PassThru -ArgumentList $workerArguments
    $clients = @{}
    $watch = [Diagnostics.Stopwatch]::StartNew()
    try {
        while (-not $worker.HasExited -and $watch.Elapsed.TotalSeconds -lt 240) {
            $service = Read-TestService
            if ($service -and $service.State -eq 'RUNNING' -and $service.Description -in @('AppUsage0042:AControlled','AppUsage0042:BNormal')) {
                $label = $service.Description.Split(':')[1]
                if (-not $clients.ContainsKey($label)) {
                    $start = [Diagnostics.ProcessStartInfo]::new()
                    $start.FileName = Join-Path $DiagnosticBuildDirectory 'AppUsagePersistenceDiagnostic.exe'
                    $start.Arguments = '--traffic --output "' + (Join-Path $RunDirectory ($label + '-client.json')) + '"'
                    $start.UseShellExecute = $false
                    $start.CreateNoWindow = $true
                    $start.RedirectStandardOutput = $true
                    $start.RedirectStandardError = $true
                    $process = [Diagnostics.Process]::Start($start)
                    $clients[$label] = [pscustomobject]@{ Process=$process; Output=$process.StandardOutput.ReadToEndAsync(); Error=$process.StandardError.ReadToEndAsync(); Label=$label }
                    Write-Output "Normal-token controlled client started: $label."
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
            if (-not $entry.Process.WaitForExit(30000)) { throw 'A bounded normal-token diagnostic did not exit.' }
            [IO.File]::WriteAllText((Join-Path $RunDirectory ($entry.Label + '-client.log')), $entry.Output.GetAwaiter().GetResult())
            [IO.File]::WriteAllText((Join-Path $RunDirectory ($entry.Label + '-client-error.log')), $entry.Error.GetAwaiter().GetResult())
            if ($entry.Process.ExitCode -ne 0) { throw "Controlled client $($entry.Label) failed; inspect its small IPv4/IPv6 evidence. Artifacts: $RunDirectory" }
        }
        if (-not $summary.Success -or -not $summary.ServiceAbsent -or -not $summary.ScmQuery1060 -or -not $summary.NoServiceProcess -or -not $summary.NoPipeServer -or -not $summary.OwnedSessionAbsent -or -not $summary.OwnedDatabaseRemoved) { throw ('Validation failed: ' + $summary.Error + '. Artifacts: ' + $RunDirectory) }
        if ($clients.Count -ne 2) { throw 'Both normal-token service lifetimes were not observed.' }
        Write-Output "Persistence validation passed. Evidence: $RunDirectory"
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
$dataRoot = [Environment]::GetFolderPath('CommonApplicationData')
$dataDirectory = Join-Path $dataRoot 'NetTrace'
$databasePath = Join-Path $dataDirectory 'app-usage.db'
$databaseFiles = @($databasePath, ($databasePath + '-wal'), ($databasePath + '-shm'), ($databasePath + '-journal'))
foreach ($file in $databaseFiles) {
    if (Test-Path -LiteralPath $file) { throw 'The default database or companion already exists; refusing to adopt or delete it.' }
}
$stageRoot = Join-Path $dataDirectory 'AppUsage0042Dev'
foreach ($path in @($dataDirectory,$stageRoot)) {
    if ((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Protected staging may not traverse a reparse point.' }
}
$createdDataDirectory = -not (Test-Path -LiteralPath $dataDirectory)
New-Item -ItemType Directory -Path $dataDirectory -Force | Out-Null
$stage = [IO.Path]::GetFullPath((Join-Path $stageRoot ([IO.Path]::GetFileName($RunDirectory))))
if (-not $stage.StartsWith([IO.Path]::GetFullPath($stageRoot) + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Staging path escaped the development directory.' }
$stageBinary = Join-Path $stage 'Binary'
$stageDiagnostic = Join-Path $stage 'Diagnostic'
$stageLogs = Join-Path $stage 'Logs'
New-Item -ItemType Directory -Path $stageBinary,$stageDiagnostic,$stageLogs -Force | Out-Null

function Protect-Directory {
    param([string]$Directory)
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
    $acl.SetAccessRuleProtection($true,$false)
    foreach ($sid in @('S-1-5-18','S-1-5-32-544')) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),'FullControl','ContainerInherit,ObjectInherit','None','Allow'))
    }
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($ClientSid),'ReadAndExecute','ContainerInherit,ObjectInherit','None','Allow'))
    Set-Acl -LiteralPath $Directory -AclObject $acl
}

function Assert-NoEveryoneWrite {
    param([string]$Path)
    $writeMask = [Security.AccessControl.FileSystemRights]::Write -bor [Security.AccessControl.FileSystemRights]::Delete -bor [Security.AccessControl.FileSystemRights]::ChangePermissions -bor [Security.AccessControl.FileSystemRights]::TakeOwnership
    foreach ($rule in (Get-Acl -LiteralPath $Path).Access) {
        $sid = $rule.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value
        if ($sid -eq 'S-1-1-0' -and $rule.AccessControlType -eq 'Allow' -and ($rule.FileSystemRights -band $writeMask) -ne 0) { throw 'Everyone has write access; validation does not change installer ACL policy.' }
    }
}

$createdHere = $false
$ownedDatabase = $false
$serviceProcessIds = @()
$measurements = @()
$reports = @()
$success = $false
$errorText = $null
$serviceAbsent = $false
$noServiceProcess = $false
$ownedSessionAbsent = $false
$noPipeServer = $false
$scmQuery1060 = $false
$ownedDatabaseRemoved = $false
$databaseAcl = $null
$restart = $null
$ipv6Proof = @()
$lifetimeWatch = $null
$reportPath = $null

function Set-Lifetime {
    param([string]$Label)
    $script:reportPath = Join-Path $stageLogs ($Label + '-service.json')
    $image = '"' + (Join-Path $stageBinary 'NetTrace.AppUsageService.exe') + '" --client-sid ' + $ClientSid + ' --log "' + (Join-Path $stageLogs 'service.log') + '" --report "' + $script:reportPath + '"'
    if (-not $script:createdHere) {
        Invoke-Sc @('create',$serviceName,'binPath=',$image,'type=','own','start=','demand','obj=','LocalSystem')
        $script:createdHere = $true
        Invoke-Sc @('sidtype',$serviceName,'unrestricted')
    }
    else { Invoke-Sc @('config',$serviceName,'binPath=',$image) }
    Invoke-Sc @('description',$serviceName,('AppUsage0042:' + $Label))
}

function Start-Lifetime {
    Invoke-Sc @('start',$serviceName)
    $running = Wait-Service 'RUNNING'
    $script:serviceProcessIds += [int]$running.ProcessId
    $script:lifetimeWatch = [Diagnostics.Stopwatch]::StartNew()
    return $running
}

function Stop-Lifetime {
    $service = Read-TestService
    if ($service -and $service.State -ne 'STOPPED') {
        Invoke-Sc @('stop',$serviceName)
        Wait-Service 'STOPPED' | Out-Null
    }
}

function Measure-Window {
    param([string]$Label,[int]$Seconds)
    $service = Read-TestService
    $process = [Diagnostics.Process]::GetProcessById([int]$service.ProcessId)
    try {
        $initialCpu = $process.TotalProcessorTime
        $watch = [Diagnostics.Stopwatch]::StartNew()
        $signatures = @{}
        $changes = 0
        while ($watch.Elapsed.TotalSeconds -lt $Seconds) {
            foreach ($file in $databaseFiles) {
                $signature = 'absent'
                if (Test-Path -LiteralPath $file) {
                    $info = Get-Item -LiteralPath $file
                    $signature = [string]$info.LastWriteTimeUtc.Ticks + ':' + [string]$info.Length
                }
                if ($signatures.ContainsKey($file) -and $signatures[$file] -ne $signature) { $changes++ }
                $signatures[$file] = $signature
            }
            Start-Sleep -Milliseconds 250
        }
        $process.Refresh()
        $script:measurements += [pscustomobject]@{ Scenario=$Label; Seconds=$watch.Elapsed.TotalSeconds; CpuPercentOfOneCore=100*($process.TotalProcessorTime-$initialCpu).TotalSeconds/$watch.Elapsed.TotalSeconds; WorkingSetMiB=$process.WorkingSet64/1MB; ObservedDatabaseFileChanges=$changes; ApproximateFileChangesPerMinute=60*$changes/$watch.Elapsed.TotalSeconds }
    }
    finally { $process.Dispose() }
}

function Read-Lifetime {
    param([string]$Label)
    $report = Get-Content -LiteralPath $script:reportPath -Raw | ConvertFrom-Json
    if (-not $report.FinalCheckpointComplete -or -not $report.Persistence.PersistenceAvailable -or $report.Persistence.PersistenceDegraded -or $report.Persistence.SuccessfulCheckpoints -lt 1 -or $report.Persistence.PendingKeys -ne 0 -or $report.Persistence.RetentionRuns -ne 1 -or $report.Snapshot.Capabilities.ServiceIdentity -ne 'LocalSystem') { throw 'Final service persistence health was not successful.' }
    $script:reports += [pscustomobject]@{ Lifetime=$Label; Seconds=$script:lifetimeWatch.Elapsed.TotalSeconds; SuccessfulCheckpoints=$report.Persistence.SuccessfulCheckpoints; TransactionsPerMinute=60*$report.Persistence.SuccessfulCheckpoints/$script:lifetimeWatch.Elapsed.TotalSeconds; FinalHealth=$report.Persistence }
    & (Join-Path $stageDiagnostic 'AppUsagePersistenceDiagnostic.exe') --query --data-root $dataRoot --output (Join-Path $stageLogs ($Label + '-rows.json'))
    if ($LASTEXITCODE -ne 0) { throw '004.1 storage diagnostic query failed.' }
    return $report
}

Start-Transcript -LiteralPath (Join-Path $RunDirectory 'admin-workflow.log') -Force | Out-Null
try {
    if ($createdDataDirectory) { Protect-Directory $dataDirectory }
    Assert-NoEveryoneWrite $dataDirectory
    foreach ($directory in @($stageRoot,$stage,$stageBinary,$stageDiagnostic,$stageLogs)) { Protect-Directory $directory }
    Copy-Item -Path (Join-Path $ServiceBuildDirectory '*') -Destination $stageBinary -Recurse
    Copy-Item -Path (Join-Path $DiagnosticBuildDirectory '*') -Destination $stageDiagnostic -Recurse
    Set-Lifetime 'AIdle'
    $ownedDatabase = $true
    Start-Lifetime | Out-Null
    Measure-Window 'Idle: no client or intentional traffic, background applications remain active' 20
    Invoke-Sc @('description',$serviceName,'AppUsage0042:AControlled')
    Measure-Window 'Normal A: tiny IPv4/IPv6 questions plus passive existing traffic' 24
    Stop-Lifetime
    $reportA = Read-Lifetime 'A'
    Assert-NoEveryoneWrite $databasePath
    $databaseAcl = (Get-Acl -LiteralPath $databasePath).AccessToString
    $rowsA = (Get-Content -LiteralPath (Join-Path $stageLogs 'A-rows.json') -Raw | ConvertFrom-Json)
    Set-Lifetime 'BNormal'
    Start-Lifetime | Out-Null
    Measure-Window 'Normal B: restarted service, tiny IPv4/IPv6 questions, passive Brave/NetMod' 24
    Stop-Lifetime
    $reportB = Read-Lifetime 'B'
    $rowsB = Get-Content -LiteralPath (Join-Path $stageLogs 'B-rows.json') -Raw | ConvertFrom-Json
    $clientA = Get-Content -LiteralPath (Join-Path $RunDirectory 'AControlled-client.json') -Raw | ConvertFrom-Json
    $clientB = Get-Content -LiteralPath (Join-Path $RunDirectory 'BNormal-client.json') -Raw | ConvertFrom-Json
    if ($clientA.ClientElevated -or $clientB.ClientElevated -or $reportA.Snapshot.CollectorInstanceId -eq $reportB.Snapshot.CollectorInstanceId) { throw 'Normal-token and fresh collector lifetime requirements failed.' }
    $key = $clientA.ApplicationIdentity
    $rowA = @($rowsA.Rows | Where-Object ApplicationIdentity -eq $key)
    $rowB = @($rowsB.Rows | Where-Object ApplicationIdentity -eq $key)
    $appA = @($reportA.Snapshot.Applications | Where-Object ExecutablePath -eq $clientA.ExecutablePath)
    $appB = @($reportB.Snapshot.Applications | Where-Object ExecutablePath -eq $clientB.ExecutablePath)
    if ($rowA.Count -ne 1 -or $rowB.Count -ne 1 -or $appA.Count -ne 1 -or $appB.Count -ne 1) { throw 'Controlled executable did not resolve to one persisted daily identity.' }
    $downloadA = [long]$appA[0].Counters.NonLoopbackRxBytes
    $uploadA = [long]$appA[0].Counters.NonLoopbackTxBytes
    $downloadB = [long]$appB[0].Counters.NonLoopbackRxBytes
    $uploadB = [long]$appB[0].Counters.NonLoopbackTxBytes
    if ([long]$rowA[0].DownloadBytes -ne $downloadA -or [long]$rowA[0].UploadBytes -ne $uploadA -or [long]$rowB[0].DownloadBytes -ne ($downloadA+$downloadB) -or [long]$rowB[0].UploadBytes -ne ($uploadA+$uploadB)) { throw 'Persisted deltas did not exactly match the two independent collector lifetimes.' }
    $restart = [pscustomobject]@{ SameDailyIdentity=$true; ADownload=$downloadA; AUpload=$uploadA; BDownloadDelta=$downloadB; BUploadDelta=$uploadB; FinalDownload=[long]$rowB[0].DownloadBytes; FinalUpload=[long]$rowB[0].UploadBytes; ExactEventDeltas=$true; DatabaseBytes=$rowsB.DatabaseBytes }
    foreach ($client in @($clientA,$clientB)) {
        $ipv4 = @($client.Exchanges | Where-Object Family -eq 'IPv4')
        $ipv6 = @($client.Exchanges | Where-Object Family -eq 'IPv6')
        if ($ipv4.Count -ne 1 -or -not $ipv4[0].Success -or $ipv6.Count -ne 1 -or $ipv6[0].SentBytes -le 0 -or $client.Application.Counters.Ipv4RxBytes -le 0 -or $client.Application.Counters.Ipv6TxBytes -le 0) { throw 'Controlled IPv4 roundtrip and IPv6 send were not observed in both lifetimes.' }
    }
    # A public IPv6 DNS peer can be blocked while the already-active VPN still
    # supplies real IPv6 traffic. Establish RX/TX from passive capture, and prove
    # its persisted B delta exactly matches the separate B collector lifetime.
    foreach ($application in $reportB.Snapshot.Applications) {
        $counter = $application.Counters
        $excludedRx = [long]$counter.LoopbackRxBytes + [long]$counter.LocalToLocalRxBytes + [long]$counter.UnclassifiedRxBytes
        $excludedTx = [long]$counter.LoopbackTxBytes + [long]$counter.LocalToLocalTxBytes + [long]$counter.UnclassifiedTxBytes
        if (-not $application.ExecutablePath -or $counter.Ipv6RxBytes -le $excludedRx -or $counter.Ipv6TxBytes -le $excludedTx) { continue }
        $previousRow = @($rowsA.Rows | Where-Object ExecutablePath -eq $application.ExecutablePath)
        $currentRow = @($rowsB.Rows | Where-Object ExecutablePath -eq $application.ExecutablePath)
        if ($currentRow.Count -ne 1 -or $previousRow.Count -gt 1) { throw 'Passive IPv6 application row identity was ambiguous.' }
        $previousDownload = 0L; $previousUpload = 0L
        if ($previousRow.Count -eq 1) { $previousDownload=[long]$previousRow[0].DownloadBytes; $previousUpload=[long]$previousRow[0].UploadBytes }
        if ([long]$currentRow[0].DownloadBytes-$previousDownload -ne [long]$counter.NonLoopbackRxBytes -or [long]$currentRow[0].UploadBytes-$previousUpload -ne [long]$counter.NonLoopbackTxBytes) { throw 'Passive IPv6 application persistence did not match exact B event deltas.' }
        $ipv6Proof += [pscustomobject]@{ ExecutableName=$currentRow[0].ExecutableName; Ipv6RxBytes=$counter.Ipv6RxBytes; Ipv6TxBytes=$counter.Ipv6TxBytes; PersistedBDownloadDelta=$counter.NonLoopbackRxBytes; PersistedBUploadDelta=$counter.NonLoopbackTxBytes; ExactEventDeltas=$true }
    }
    if ($ipv6Proof.Count -eq 0 -and ($clientB.Application.Counters.Ipv6RxBytes -le 0 -or $clientB.Application.Counters.Ipv6TxBytes -le 0)) { throw 'Real non-loopback IPv6 RX/TX was not established by controlled or passive traffic.' }
    $success = $true
}
catch { $errorText = $_.Exception.Message; Write-Output ('Validation error: ' + $errorText) }
finally {
    try {
        if ($createdHere) {
            $current = Read-TestService -IncludeConfig
            if ($current -and -not (Test-OwnedImage $current.PathName $stageBinary)) { throw 'Registration ownership changed; refusing cleanup of another service.' }
            Invoke-Sc @('config',$serviceName,'start=','disabled')
            try { Stop-Lifetime }
            catch {
                $current = Read-TestService -IncludeConfig
                if (-not $current -or $current.ProcessId -notin $serviceProcessIds -or -not $current.PathName.Contains($stageBinary)) { throw }
                Stop-Process -Id $current.ProcessId -Force
                Wait-Service 'STOPPED' 10 | Out-Null
            }
            Invoke-Sc @('delete',$serviceName)
            Wait-Service 'Absent' | Out-Null
        }
        Copy-Item -Path (Join-Path $stageLogs '*') -Destination $RunDirectory -ErrorAction SilentlyContinue
        & (Join-Path $stageBinary 'NetTrace.AppUsageService.exe') --cleanup-owned-session
        if ($LASTEXITCODE -ne 0) { throw 'Exact-session cleanup failed.' }
        & logman.exe query $sessionName -ets 2>&1 | Out-File -LiteralPath (Join-Path $RunDirectory 'final-session-query.log')
        $ownedSessionAbsent = $LASTEXITCODE -ne 0
        $serviceAbsent = $null -eq (Read-TestService)
        $remaining = @(Get-Process -Name 'NetTrace.AppUsageService' -ErrorAction SilentlyContinue | Where-Object { $_.Id -in $serviceProcessIds -or ($_.Path -and $_.Path.StartsWith($stageBinary,[StringComparison]::OrdinalIgnoreCase)) })
        $noServiceProcess = $remaining.Count -eq 0
        $noPipeServer = @(Get-ChildItem -LiteralPath '\\.\pipe\' | Where-Object Name -eq 'NetTrace.AppUsage003.Service.938F90CEB2A34EF7A67BA5A43521EE77').Count -eq 0
        & sc.exe query $serviceName 2>&1 | Out-File -LiteralPath (Join-Path $RunDirectory 'final-scm-query.log')
        $scmQuery1060 = $LASTEXITCODE -eq 1060
        if (-not $serviceAbsent -or -not $ownedSessionAbsent -or -not $noServiceProcess -or -not $noPipeServer -or -not $scmQuery1060) { throw 'Final service/session/process/pipe cleanup was incomplete.' }
        if ($ownedDatabase -and (Test-Path -LiteralPath $databasePath)) {
            $owner = (Get-Acl -LiteralPath $databasePath).GetOwner([Security.Principal.SecurityIdentifier]).Value
            if ($owner -ne 'S-1-5-18') { throw 'Database was not created by SYSTEM; refusing deletion without exact ownership.' }
            foreach ($file in $databaseFiles) {
                $resolved = [IO.Path]::GetFullPath($file)
                if ($resolved -notin $databaseFiles -or -not $resolved.StartsWith([IO.Path]::GetFullPath($dataDirectory)+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Owned database cleanup path escaped its directory.' }
                if (Test-Path -LiteralPath $resolved) {
                    if ((Get-Item -LiteralPath $resolved).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Refusing deletion through a database reparse point.' }
                    Remove-Item -LiteralPath $resolved -Force
                }
            }
        }
        $ownedDatabaseRemoved = @($databaseFiles | Where-Object { Test-Path -LiteralPath $_ }).Count -eq 0
    }
    catch { $success=$false; $errorText='Cleanup verification failed: '+$_.Exception.Message }
    [pscustomobject]@{ Success=$success; Error=$errorText; SelectedIdentity='LocalSystem'; DefaultDatabaseCreatedBySystem=$ownedDatabase; DatabaseAcl=$databaseAcl; Measurements=$measurements; Lifetimes=$reports; Restart=$restart; PassiveIpv6Persistence=$ipv6Proof; ServiceAbsent=$serviceAbsent; ScmQuery1060=$scmQuery1060; OwnedSessionAbsent=$ownedSessionAbsent; NoServiceProcess=$noServiceProcess; NoPipeServer=$noPipeServer; OwnedDatabaseRemoved=$ownedDatabaseRemoved } | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $RunDirectory 'admin-summary.json')
    Stop-Transcript | Out-Null
    $resolvedStage = [IO.Path]::GetFullPath($stage)
    if ($serviceAbsent -and $ownedSessionAbsent -and $noServiceProcess -and $resolvedStage.StartsWith([IO.Path]::GetFullPath($stageRoot)+'\',[StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $resolvedStage -Recurse -Force }
}
if (-not $success) { exit 1 }
