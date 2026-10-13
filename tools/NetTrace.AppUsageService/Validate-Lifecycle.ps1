[CmdletBinding()]
param(
    [switch]$AdminWorker,
    [switch]$TrafficOnly,
    [switch]$SnapshotOnly,
    [string]$RunDirectory,
    [string]$WpfBuildDirectory,
    [string]$ProbeBuildDirectory,
    [string]$ClientSid,
    [string]$Events = 'Update,Restart,ForcedRecovery,Sleep'
)

# Explicit production validation only. NetTrace never launches this script.
# Service actions target the verified existing production registration only.
# SQLite opens read-only; there is no clear, uninstall, reboot or directory deletion.
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$verificationRoot = Join-Path $repository 'bin\AppUsage0056Verification'
$serviceName = 'NetTraceAppUsageCollector'
$databasePath = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'NetTrace\app-usage.db'
$installationRoot = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'NetTrace\AppUsageCollector'
$isAdministrator = ([Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$eventNames = @($Events.Split(','))
if ($eventNames.Count -eq 0 -or @($eventNames | Select-Object -Unique).Count -ne $eventNames.Count -or
    @($eventNames | Where-Object { $_ -notin @('Update','Restart','ForcedRecovery','Sleep') }).Count -gt 0) {
    throw 'Choose distinct explicit Update, Restart, ForcedRecovery or Sleep events.'
}

function Assert-VerificationPath {
    param([string]$Path)
    if (-not [IO.Path]::IsPathRooted($Path)) { throw 'Verification paths must be absolute.' }
    $absolute = [IO.Path]::GetFullPath($Path)
    if (-not $absolute.StartsWith($verificationRoot+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Artifact path escaped the 005.5 verification directory.' }
    return $absolute
}

function Assert-NoReparse {
    param([string]$Path)
    $absolute = [IO.Path]::GetFullPath($Path)
    while ($absolute) {
        if ((Test-Path -LiteralPath $absolute) -and ((Get-Item -LiteralPath $absolute -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'A verification or protected path contains a reparse point.'
        }
        $absolute = [IO.Path]::GetDirectoryName($absolute)
    }
}

function Write-Json {
    param([string]$Name,[object]$Value)
    $path = Join-Path $RunDirectory $Name
    $Value | ConvertTo-Json -Depth 25 | Set-Content -LiteralPath ($path+'.writing') -Encoding UTF8
    Move-Item -LiteralPath ($path+'.writing') -Destination $path -Force
}

function Read-InstallerStatus {
    $value = & $installer status 2>&1
    if ($LASTEXITCODE -ne 0) { throw ('Read-only installer status failed: '+($value -join ' ')) }
    return ($value -join [Environment]::NewLine) | ConvertFrom-Json
}

function Get-TextHash {
    param([string]$Value)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Value))).Replace('-','').ToLowerInvariant() }
    finally { $sha.Dispose() }
}

function Get-HistoryDateKey {
    param([object]$Value)
    # PowerShell 7 may deserialize ISO strings as DateTime. PowerShell 5 keeps
    # them as strings; preserve the stored calendar key in either host.
    if ($Value -is [DateTime] -or $Value -is [DateTimeOffset]) {
        return $Value.ToString('yyyy-MM-dd',[Globalization.CultureInfo]::InvariantCulture)
    }
    return [string]$Value
}

function Get-HistoryTimestamp {
    param([object]$Value)
    if ($Value -is [DateTimeOffset]) { return $Value }
    if ($Value -is [DateTime]) { return [DateTimeOffset]$Value }
    return [DateTimeOffset]::Parse([string]$Value,[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::RoundtripKind)
}

function Read-KitVersion {
    param([string]$Directory)
    $source = [IO.Path]::GetFullPath($Directory).TrimEnd('\')
    Assert-NoReparse $source
    $files = [Collections.Generic.SortedDictionary[string,string]]::new([StringComparer]::Ordinal)
    foreach ($entry in Get-ChildItem -LiteralPath $source -Recurse -Force) {
        Assert-NoReparse $entry.FullName
        if ($entry.PSIsContainer) { continue }
        $absolute = [IO.Path]::GetFullPath($entry.FullName)
        if (-not $absolute.StartsWith($source+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'A kit entry escaped its directory.' }
        $relative = $absolute.Substring($source.Length+1)
        if ($relative.StartsWith('installation-0051',[StringComparison]::Ordinal)) { continue }
        $files.Add($relative,(Get-FileHash -LiteralPath $absolute -Algorithm SHA256).Hash.ToLowerInvariant())
    }
    if ($files.Count -eq 0 -or $files.Count -gt 10000) { throw 'The collector inventory is invalid.' }
    return Get-TextHash (($files.GetEnumerator() | ForEach-Object { $_.Key+'|'+$_.Value }) -join "`n")
}

function Assert-OwnedRegistration {
    param([object]$Status,[string]$ExpectedVersion)
    if (-not $Status.Installed -or -not $Status.Running -or $Status.ServiceName -ne $serviceName -or
        $Status.Identity -ne 'LocalSystem' -or $Status.Description -ne 'NetTrace App Usage production collector; installation format 005.1.') {
        throw 'The production running registration is not the expected owned LocalSystem collector.'
    }
    if ($Status.BinaryCommandLine -notmatch '^"([^"]+)" --client-sid (S-1-5-21-\d+-\d+-\d+-\d+) --service-name NetTraceAppUsageCollector --log "([^"]+)"$' -or $Matches[2] -ne $ClientSid) {
        throw 'The production command does not match the normal desktop identity.'
    }
    $image = [IO.Path]::GetFullPath($Matches[1])
    $log = [IO.Path]::GetFullPath($Matches[3])
    $directory = [IO.Path]::GetDirectoryName($image)
    $version = [IO.Path]::GetFileName($directory)
    if (-not $directory.StartsWith((Join-Path $installationRoot 'Versions')+'\',[StringComparison]::OrdinalIgnoreCase) -or
        $version -cne $ExpectedVersion -or $version -notmatch '^[0-9a-f]{64}$' -or
        [IO.Path]::GetFileName($image) -ne 'NetTrace.AppUsageService.exe' -or
        $log -ne (Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'NetTrace\AppUsageService\service.log')) {
        throw 'The registered service image, version or log escaped its expected protected layout.'
    }
    Assert-NoReparse $directory
    $manifestPath = Join-Path $directory ('installation-0051-'+(Get-TextHash $ClientSid)+'.json')
    Assert-NoReparse $manifestPath
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.Format -ne 1 -or $manifest.Configuration.ClientSid -ne $ClientSid -or
        $manifest.Configuration.DatabasePath -ne $databasePath -or $manifest.Configuration.ExecutablePath -ne $image -or
        $manifest.Configuration.LogPath -ne $log -or $manifest.Configuration.ServiceName -ne $serviceName) {
        throw 'The protected production manifest does not match the registered service and database.'
    }
    $files = [Collections.Generic.SortedDictionary[string,string]]::new([StringComparer]::Ordinal)
    foreach ($entry in $manifest.Files.PSObject.Properties) {
        $absolute = [IO.Path]::GetFullPath((Join-Path $directory $entry.Name))
        if (-not $absolute.StartsWith($directory+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'A protected manifest entry escaped its version directory.' }
        Assert-NoReparse $absolute
        if ((Get-FileHash -LiteralPath $absolute -Algorithm SHA256).Hash.ToLowerInvariant() -cne $entry.Value) { throw 'An installed collector file differs from its protected manifest.' }
        $files.Add($entry.Name,$entry.Value)
    }
    $actual = Get-TextHash (($files.GetEnumerator() | ForEach-Object { $_.Key+'|'+$_.Value }) -join "`n")
    if ($actual -cne $version) { throw 'The protected installed manifest has an invalid package version.' }
    return [pscustomobject]@{ Version=$version; Image=$image; ImageSha256=(Get-FileHash -LiteralPath $image -Algorithm SHA256).Hash; Files=$files.Count }
}



function Assert-BuildPath {
    param([string]$Path)
    $absolute = [IO.Path]::GetFullPath($Path)
    if (-not $absolute.StartsWith((Join-Path $repository 'bin')+'\',[StringComparison]::OrdinalIgnoreCase)) {
        throw 'Validation builds must stay in this workspace bin directory.'
    }
    Assert-NoReparse $absolute
    return $absolute
}

function Load-ObservationAssemblies {
    $null = [Reflection.Assembly]::LoadFrom((Join-Path $WpfBuildDirectory 'AppUsageIpc.dll'))
    $null = [Reflection.Assembly]::LoadFrom((Join-Path $ProbeBuildDirectory 'AppUsageEtwProbe.dll'))
}

function Read-RangeOnce {
    $client = [AppUsageIpc.IpcClient]::new([AppUsageIpc.ServicePipeSecurity]::DefaultPipeName,$null,$serviceName,$null)
    try {
        $connection = $client.ConnectAsync().GetAwaiter().GetResult()
        if ($connection.State.ToString() -ne 'Connected') { throw ('Authenticated ordinary IPC unavailable: '+$connection.State) }
        $query = $client.GetAppUsageRangeAsync([AppUsageIpc.AppUsageRange]::Today).GetAwaiter().GetResult()
        if ($query.State.ToString() -ne 'Connected' -or $query.Value.Availability.ToString() -ne 'Available') {
            throw 'The ordinary Today query was not available.'
        }
        return $query.Value
    }
    finally { $null = $client.DisposeAsync().AsTask().GetAwaiter().GetResult() }
}

function Read-Range {
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    do {
        try { return Read-RangeOnce }
        catch {
            if ($_.Exception.Message -notmatch '^Authenticated ordinary IPC unavailable: (CollectorUnavailable|Disconnected|TimedOut)$' -or
                $deadline.Elapsed.TotalSeconds -gt 25) { throw }
            Start-Sleep -Milliseconds 250
        }
    } while ($true)
}

function Get-ApplicationBytes {
    param([object]$Range,[string]$Path)
    $key = 'path:'+([IO.Path]::GetFullPath($Path)).ToUpperInvariant()
    $row = $Range.Applications | Where-Object ApplicationIdentity -CEQ $key | Select-Object -First 1
    return [pscustomobject]@{ Download=if ($row) { [long]$row.DownloadBytes } else { 0L }; Upload=if ($row) { [long]$row.UploadBytes } else { 0L } }
}

function Get-PersistedApplicationBytes {
    param([object]$Database,[string]$Path)
    $identity = 'path:'+([IO.Path]::GetFullPath($Path)).ToUpperInvariant()
    $today = [DateTime]::Now.ToString('yyyy-MM-dd',[Globalization.CultureInfo]::InvariantCulture)
    $row = $Database.Rows | Where-Object { (Get-HistoryDateKey $_[0]) -ceq $today -and $_[1] -ceq $identity } | Select-Object -First 1
    return [pscustomobject]@{ Download=if ($row) { [long]$row[5] } else { 0L }; Upload=if ($row) { [long]$row[6] } else { 0L } }
}

function Read-CaptureBytes {
    param([string]$Path)
    $captureClient = [AppUsageIpc.IpcClient]::new([AppUsageIpc.ServicePipeSecurity]::DefaultPipeName,$null,$serviceName,$null)
    try {
        if ($captureClient.ConnectAsync().GetAwaiter().GetResult().State.ToString() -ne 'Connected') { throw 'Capture evidence IPC unavailable.' }
        $captureResult = $captureClient.GetSnapshotAsync().GetAwaiter().GetResult()
        if ($captureResult.State.ToString() -ne 'Connected') { throw 'Authenticated capture evidence was not available.' }
        $application = $captureResult.Value.Applications | Where-Object { $_.ExecutablePath -ieq $Path } | Select-Object -First 1
        return [pscustomobject]@{ Instance=$captureResult.Value.CollectorInstanceId;
            Download=if ($application) { [long]$application.Counters.NonLoopbackRxBytes } else { 0L };
            Upload=if ($application) { [long]$application.Counters.NonLoopbackTxBytes } else { 0L } }
    } finally { $null = $captureClient.DisposeAsync().AsTask().GetAwaiter().GetResult() }
}

function Invoke-SmallHttpsTrafficOnce {
    # The production mapper intentionally excludes loopback. Use two small
    # public HTTPS responses, with no cookies, credentials, proxy or redirects.
    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.UseProxy=$false; $handler.UseCookies=$false; $handler.AllowAutoRedirect=$false
    $http = [Net.Http.HttpClient]::new($handler)
    $http.Timeout=[TimeSpan]::FromSeconds(15)
    $received=0L
    try {
        foreach ($iteration in 1..2) {
            $script:lifecycleTrafficRequestAttempts++
            $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get,'https://example.com/')
            $request.Version=[Version]::new(1,1)
            $request.Headers.ConnectionClose=$true
            $response=$null; $stream=$null
            $limit=[Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(15))
            try {
                $response=$http.SendAsync($request,[Net.Http.HttpCompletionOption]::ResponseHeadersRead,$limit.Token).GetAwaiter().GetResult()
                if (-not $response.IsSuccessStatusCode) { throw 'The bounded public HTTPS response was not successful.' }
                $stream=$response.Content.ReadAsStreamAsync($limit.Token).GetAwaiter().GetResult()
                $buffer=[byte[]]::new(4096); $responseBytes=0L
                do {
                    $read=$stream.ReadAsync($buffer,0,$buffer.Length,$limit.Token).GetAwaiter().GetResult()
                    $responseBytes+=$read
                    if ($responseBytes -gt 16384) { throw 'The public response exceeded the 16 KiB content bound.' }
                } while ($read -gt 0)
                if ($responseBytes -eq 0) { throw 'The public response was empty.' }
                $received+=$responseBytes
            } finally {
                if ($stream) { $stream.Dispose() }; if ($response) { $response.Dispose() }
                $request.Dispose(); $limit.Dispose()
            }
        }
    } finally { $http.Dispose() }
    return $received
}

function Invoke-SmallHttpsTraffic {
    $networkDeadline=[Diagnostics.Stopwatch]::StartNew()
    do {
        try { return Invoke-SmallHttpsTrafficOnce }
        catch {
            if ($networkDeadline.Elapsed.TotalSeconds -ge 50) { throw }
            $script:lifecycleTrafficNetworkRetries++
            Start-Sleep -Milliseconds 1000
        }
    } while ($true)
}

function Invoke-ControlledTraffic {
    param([string]$Name,[switch]$ProtectedEvidence)
    $script:lifecycleTrafficRequestAttempts=0
    $script:lifecycleTrafficNetworkRetries=0
    Start-Sleep -Milliseconds 5500
    $serverPath = [Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
    if ($ProtectedEvidence) {
        $beforeProtected = Get-Content -LiteralPath (Join-Path $RunDirectory ('snapshot-TrafficStart-'+$Name+'.json')) -Raw | ConvertFrom-Json
        $serverBefore = Get-PersistedApplicationBytes $beforeProtected.Database $serverPath
    } else {
        $serverBefore = Get-ApplicationBytes (Read-Range) $serverPath
        $captureBefore = Read-CaptureBytes $serverPath
    }
    $bodyBytes = Invoke-SmallHttpsTraffic
    if ($ProtectedEvidence) {
        Write-Json ('traffic-'+$Name+'.json') ([pscustomobject]@{ Success=$true; Name=$Name; ClientElevated=$false;
            Requests=$script:lifecycleTrafficRequestAttempts; NetworkRetries=$script:lifecycleTrafficNetworkRetries;
            ResponseContentBytes=$bodyBytes; MaximumDownloadDelta=524288; MaximumUploadDelta=131072;
            CounterVerification='Separate protected read-only SQLite comparison'; ServerPath=$serverPath; ServerBefore=$serverBefore })
        return
    }
    Start-Sleep -Milliseconds 2000
    $watch=[Diagnostics.Stopwatch]::StartNew()
    do {
        $afterBytes=Get-ApplicationBytes (Read-Range) $serverPath
        $captureAfter=Read-CaptureBytes $serverPath
        if ($captureAfter.Instance -ne $captureBefore.Instance) { throw 'The collector changed during a bounded traffic measurement.' }
        $download=$afterBytes.Download-$serverBefore.Download
        $upload=$afterBytes.Upload-$serverBefore.Upload
        $acceptedDownload=$captureAfter.Download-$captureBefore.Download
        $acceptedUpload=$captureAfter.Upload-$captureBefore.Upload
        if ($download -lt 0 -or $upload -lt 0 -or $download -gt 524288 -or $upload -gt 131072) {
            throw 'Persisted controlled traffic decreased or exceeded its finite network bound.'
        }
        $exact=$download -ge $bodyBytes -and $upload -gt 0 -and $download -eq $acceptedDownload -and $upload -eq $acceptedUpload
        if (-not $exact) { Start-Sleep -Milliseconds 500 }
    } while (-not $exact -and $watch.Elapsed.TotalSeconds -lt 25)
    $report=[pscustomobject]@{ Success=$exact; Name=$Name; ClientElevated=$false; Requests=$script:lifecycleTrafficRequestAttempts;
        NetworkRetries=$script:lifecycleTrafficNetworkRetries; ResponseContentBytes=$bodyBytes;
        DownloadDelta=$download; UploadDelta=$upload; AcceptedNonLoopbackDownload=$acceptedDownload; AcceptedNonLoopbackUpload=$acceptedUpload;
        ExactCapturedNonLoopbackMatch=$exact; CountersBounded=($download -le 524288 -and $upload -le 131072); ObservedAtUtc=[DateTimeOffset]::UtcNow.ToString('O') }
    Write-Json ('traffic-'+$Name+'.json') $report
    if (-not $exact) { throw 'Persisted traffic did not equal the exact accepted non-loopback event delta.' }
}
function Wait-Marker {
    param([string]$Name,[int]$Seconds=180)
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $path = Join-Path $RunDirectory $Name
    while (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        if (Test-Path -LiteralPath (Join-Path $RunDirectory 'lifecycle-client.json')) {
            $ui = Get-Content -LiteralPath (Join-Path $RunDirectory 'lifecycle-client.json') -Raw | ConvertFrom-Json
            if ($ui.Completed -and -not $ui.Success) { throw ('The normal UI observation failed: '+$ui.Error) }
        }
        if ($watch.Elapsed.TotalSeconds -gt $Seconds) { throw ('The bounded marker did not arrive: '+$Name) }
        Start-Sleep -Milliseconds 200
    }
    return Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
}

if (-not $AdminWorker) {
    if ($isAdministrator) { throw 'Run validation with the normal desktop token; only its explicit service worker elevates.' }
    New-Item -ItemType Directory -Path $verificationRoot -Force | Out-Null
    if (-not $RunDirectory) { $RunDirectory = Join-Path $verificationRoot ('run-'+[Guid]::NewGuid().ToString('N')) }
    $RunDirectory = Assert-VerificationPath $RunDirectory
    Assert-NoReparse $RunDirectory
    New-Item -ItemType Directory -Path $RunDirectory -Force | Out-Null
    if (Test-Path -LiteralPath (Join-Path $RunDirectory 'normal-context.json')) { throw 'Use a fresh lifecycle evidence directory.' }
    if (-not $WpfBuildDirectory) { $WpfBuildDirectory = Join-Path $verificationRoot 'Focused' }
    if (-not $ProbeBuildDirectory) { $ProbeBuildDirectory = Join-Path $repository 'bin\AppUsage0055Verification\AllBuilds\AppUsageEtwProbe' }
    $WpfBuildDirectory = Assert-BuildPath $WpfBuildDirectory
    $ProbeBuildDirectory = Assert-BuildPath $ProbeBuildDirectory
    $installer = Join-Path $WpfBuildDirectory 'AppTracking\Installer\NetTrace.AppUsageInstaller.exe'
    $harness = Join-Path $WpfBuildDirectory 'NetTrace.WpfTests.exe'
    foreach ($path in @($installer,$harness,(Join-Path $ProbeBuildDirectory 'AppUsageEtwProbe.runtimeconfig.json'))) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw 'A complete prebuilt validation kit is missing.' }
    }
    $ClientSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $status = Read-InstallerStatus
    if (-not $status.Running -or $status.Identity -ne 'LocalSystem' -or -not $status.DelayedAutomaticStart) { throw 'The existing production service is not Running LocalSystem with delayed automatic start.' }
    $bundle = Read-KitVersion (Join-Path $WpfBuildDirectory 'AppTracking\Service')
    Write-Json 'normal-context.json' ([pscustomobject]@{ ClientSid=$ClientSid; WpfDirectory=$WpfBuildDirectory; ProbeDirectory=$ProbeBuildDirectory; BundledVersion=$bundle; Events=$Events; StartedAtUtc=[DateTimeOffset]::UtcNow.ToString('O') })
    Load-ObservationAssemblies
    if ($TrafficOnly) { Invoke-ControlledTraffic 'Preflight'; return }
    if ($SnapshotOnly) {
        $snapshotArgs = '-NoProfile -ExecutionPolicy RemoteSigned -File "'+$PSCommandPath+'" -AdminWorker -SnapshotOnly -RunDirectory "'+$RunDirectory+'" -WpfBuildDirectory "'+$WpfBuildDirectory+'" -ProbeBuildDirectory "'+$ProbeBuildDirectory+'" -ClientSid '+$ClientSid+' -Events '+$Events
        $snapshotWorker = Start-Process -FilePath (Get-Command pwsh.exe).Source -Verb RunAs -WindowStyle Hidden -PassThru -ArgumentList $snapshotArgs
        try {
            if (-not $snapshotWorker.WaitForExit(55000)) { throw 'The protected read-only observation did not finish.' }
            $snapshotReport = Get-Content -LiteralPath (Join-Path $RunDirectory 'worker-report.json') -Raw | ConvertFrom-Json
            if (-not $snapshotReport.Success) { throw $snapshotReport.Error }
            Write-Output ('Protected read-only snapshot passed: '+$RunDirectory)
        } finally { $snapshotWorker.Dispose() }
        return
    }
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $harness
    $start.Arguments = '--app-usage-lifecycle-live-validation --directory "'+$RunDirectory+'" --events '+$Events+' --range 30D'
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $client = [Diagnostics.Process]::Start($start)
    $stdout = $client.StandardOutput.ReadToEndAsync(); $stderr = $client.StandardError.ReadToEndAsync()
    $worker = $null
    $failure = $null
    try {
        $null = Wait-Marker ('ready-'+$eventNames[0]+'.json') 40
        $workerArgs = '-NoProfile -ExecutionPolicy RemoteSigned -File "'+$PSCommandPath+'" -AdminWorker -RunDirectory "'+$RunDirectory+'" -WpfBuildDirectory "'+$WpfBuildDirectory+'" -ProbeBuildDirectory "'+$ProbeBuildDirectory+'" -ClientSid '+$ClientSid+' -Events '+$Events
        Write-Host 'Explicit UAC: verified production update/restart/recovery and read-only history checks; sleep uses a temporary 30-second wake timer.'
        $worker = Start-Process -FilePath (Get-Command pwsh.exe).Source -Verb RunAs -WindowStyle Hidden -PassThru -ArgumentList $workerArgs
        $overall = [Diagnostics.Stopwatch]::StartNew()
        $trafficDone = [Collections.Generic.HashSet[string]]::new()
        while (-not $worker.HasExited -and $overall.Elapsed.TotalSeconds -lt 900) {
            foreach ($eventName in $eventNames) {
                foreach ($phase in @('Before','After')) {
                    $name = $phase+'-'+$eventName
                    if (-not $trafficDone.Contains($name) -and (Test-Path -LiteralPath (Join-Path $RunDirectory ('traffic-ready-'+$name+'.json')))) {
                        $trafficReady = Get-Content -LiteralPath (Join-Path $RunDirectory ('traffic-ready-'+$name+'.json')) -Raw | ConvertFrom-Json
                        Invoke-ControlledTraffic $name -ProtectedEvidence:([bool]$trafficReady.ProtectedEvidence)
                        Write-Json ('traffic-done-'+$name+'.json') ([pscustomobject]@{ Success=$true })
                        $null = $trafficDone.Add($name)
                    }
                }
            }
            $worker.Refresh()
            Start-Sleep -Milliseconds 200
        }
        if (-not $worker.HasExited) { throw 'The bounded privileged worker did not finish; it was left to finish its owned action safely.' }
        $workerReport = Wait-Marker 'worker-report.json' 5
        if (-not $workerReport.Success) { throw ('The privileged lifecycle observation failed: '+$workerReport.Error) }
        if (-not $client.WaitForExit(5000)) { throw 'The normal UI observer did not finish after the last event.' }
        $uiReport = Get-Content -LiteralPath (Join-Path $RunDirectory 'lifecycle-client.json') -Raw | ConvertFrom-Json
        if (-not $uiReport.Success -or $client.ExitCode -ne 0) { throw ('The normal UI observer failed: '+$uiReport.Error) }
    }
    catch { $failure = $_.Exception.Message }
    finally {
        if (-not $client.HasExited) { Stop-Process -Id $client.Id -Force; $client.WaitForExit() }
        [IO.File]::WriteAllText((Join-Path $RunDirectory 'ui-stdout.log'),$stdout.GetAwaiter().GetResult())
        [IO.File]::WriteAllText((Join-Path $RunDirectory 'ui-stderr.log'),$stderr.GetAwaiter().GetResult())
        $client.Dispose()
        if ($worker) { $worker.Dispose() }
    }
    $summary = [pscustomobject]@{ Success=($null -eq $failure); Error=$failure; EvidenceDirectory=$RunDirectory; Events=$eventNames;
        NetTraceElevated=$false; HistoryClearInvoked=$false; UninstallInvoked=$false; RebootPerformed=$false;
        RebootReason='Deferred while the interactive IDE and validation session are active.' }
    Write-Json 'workflow-summary.json' $summary
    $summary | ConvertTo-Json -Depth 4
    if ($failure) { throw $failure }
    return
}

if (-not $isAdministrator) { throw 'The explicit service lifecycle worker requires elevation.' }
$RunDirectory = Assert-VerificationPath $RunDirectory
$WpfBuildDirectory = Assert-BuildPath $WpfBuildDirectory
$ProbeBuildDirectory = Assert-BuildPath $ProbeBuildDirectory
Assert-NoReparse $RunDirectory
$context = Get-Content -LiteralPath (Join-Path $RunDirectory 'normal-context.json') -Raw | ConvertFrom-Json
if ([Security.Principal.WindowsIdentity]::GetCurrent().User.Value -ne $ClientSid -or $context.ClientSid -ne $ClientSid -or
    $context.Events -cne $Events -or $context.WpfDirectory -cne $WpfBuildDirectory -or $context.ProbeDirectory -cne $ProbeBuildDirectory) {
    throw 'The privileged worker does not match the normal client evidence context.'
}
$installer = Join-Path $WpfBuildDirectory 'AppTracking\Installer\NetTrace.AppUsageInstaller.exe'
Load-ObservationAssemblies

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public sealed class AppUsageReadOnlySnapshot {
    public string[][] Schema { get; set; }
    public string[][] Rows { get; set; }
    public string ApplicationId { get; set; }
    public string UserVersion { get; set; }
}
public static class AppUsageReadOnlyNative {
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern IntPtr LoadLibrary(string path);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern bool WaitNamedPipe(string path, uint timeout);
    [DllImport("e_sqlite3.dll", CallingConvention=CallingConvention.Cdecl)] private static extern int sqlite3_open_v2(byte[] path, out IntPtr db, int flags, IntPtr vfs);
    [DllImport("e_sqlite3.dll", CallingConvention=CallingConvention.Cdecl)] private static extern int sqlite3_close(IntPtr db);
    [DllImport("e_sqlite3.dll", CallingConvention=CallingConvention.Cdecl)] private static extern int sqlite3_busy_timeout(IntPtr db, int milliseconds);
    [DllImport("e_sqlite3.dll", CallingConvention=CallingConvention.Cdecl)] private static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int length, out IntPtr statement, IntPtr tail);
    [DllImport("e_sqlite3.dll", CallingConvention=CallingConvention.Cdecl)] private static extern int sqlite3_step(IntPtr statement);
    [DllImport("e_sqlite3.dll", CallingConvention=CallingConvention.Cdecl)] private static extern int sqlite3_finalize(IntPtr statement);
    [DllImport("e_sqlite3.dll", CallingConvention=CallingConvention.Cdecl)] private static extern int sqlite3_column_count(IntPtr statement);
    [DllImport("e_sqlite3.dll", CallingConvention=CallingConvention.Cdecl)] private static extern IntPtr sqlite3_column_text(IntPtr statement, int column);
    [DllImport("e_sqlite3.dll", CallingConvention=CallingConvention.Cdecl)] private static extern int sqlite3_column_bytes(IntPtr statement, int column);
    [DllImport("e_sqlite3.dll", CallingConvention=CallingConvention.Cdecl)] private static extern int sqlite3_column_type(IntPtr statement, int column);
    private static byte[] Utf8(string value) { return Encoding.UTF8.GetBytes(value+"\0"); }
    private static string[][] Query(IntPtr db, string sql) {
        IntPtr statement;
        if (sqlite3_prepare_v2(db,Utf8(sql),-1,out statement,IntPtr.Zero)!=0) throw new InvalidOperationException("Read-only SQLite statement failed.");
        try {
            var rows=new List<string[]>();
            int result;
            while ((result=sqlite3_step(statement))==100) {
                if (rows.Count>=200000) throw new InvalidOperationException("Read-only SQLite observation exceeded its row bound.");
                var row=new string[sqlite3_column_count(statement)];
                for (int index=0;index<row.Length;index++) {
                    if (sqlite3_column_type(statement,index)==5) { row[index]=null; continue; }
                    var bytes=new byte[sqlite3_column_bytes(statement,index)];
                    Marshal.Copy(sqlite3_column_text(statement,index),bytes,0,bytes.Length);
                    row[index]=Encoding.UTF8.GetString(bytes);
                }
                rows.Add(row);
            }
            if (result!=101) throw new InvalidOperationException("Read-only SQLite snapshot did not complete.");
            return rows.ToArray();
        }
        finally { sqlite3_finalize(statement); }
    }
    public static AppUsageReadOnlySnapshot Read(string database, string nativeLibrary) {
        if (LoadLibrary(nativeLibrary)==IntPtr.Zero) throw new InvalidOperationException("The owned native SQLite dependency did not load.");
        IntPtr db;
        int open=sqlite3_open_v2(Utf8(database),out db,1,IntPtr.Zero);
        if (open!=0) { if (db!=IntPtr.Zero) sqlite3_close(db); throw new InvalidOperationException("The existing protected database could not be opened read-only."); }
        try {
            sqlite3_busy_timeout(db,3000);
            Query(db,"BEGIN;");
            var schema=Query(db,"SELECT type,name,tbl_name,sql FROM sqlite_schema WHERE name NOT GLOB 'sqlite_*' ORDER BY type,name;");
            var applicationId=Query(db,"PRAGMA application_id;")[0][0];
            var version=Query(db,"PRAGMA user_version;")[0][0];
            if (applicationId!="1314144597" || version!="1") throw new InvalidOperationException("The protected database is not the existing App Usage schema.");
            var check=Query(db,"PRAGMA quick_check;");
            if (check.Length!=1 || check[0][0]!="ok") throw new InvalidOperationException("Read-only SQLite integrity verification failed.");
            var rows=Query(db,"SELECT local_date,application_identity,display_name,executable_name,executable_path,download_bytes,upload_bytes,last_seen_utc FROM daily_app_usage ORDER BY local_date,application_identity;");
            Query(db,"COMMIT;");
            return new AppUsageReadOnlySnapshot { Schema=schema,Rows=rows,ApplicationId=applicationId,UserVersion=version };
        }
        finally { sqlite3_close(db); }
    }
    public static bool ManagementEndpointExists() {
        if (WaitNamedPipe(@"\\.\pipe\NetTrace.AppUsage0054.Management.938F90CEB2A34EF7A67BA5A43521EE77",0)) return true;
        int error=Marshal.GetLastWin32Error();
        if (error==2 || error==3) return false;
        if (error==5 || error==121 || error==231) return true;
        throw new InvalidOperationException("Read-only management endpoint observation failed.");
    }
}
'@

Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
public static class LifecycleSleepValidation {
    [StructLayout(LayoutKind.Sequential,Pack=1)] struct Privileges { public int Count; public long Luid; public int Attributes; }
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("advapi32.dll",SetLastError=true)] static extern bool OpenProcessToken(IntPtr process,int access,out IntPtr token);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool LookupPrivilegeValue(string system,string name,out long luid);
    [DllImport("advapi32.dll",SetLastError=true)] static extern bool AdjustTokenPrivileges(IntPtr token,bool disable,ref Privileges privileges,int length,IntPtr previous,IntPtr required);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr CreateWaitableTimer(IntPtr attributes,bool manual,string name);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool SetWaitableTimer(IntPtr timer,ref long due,int period,IntPtr routine,IntPtr argument,bool resume);
    [DllImport("kernel32.dll")] static extern bool CancelWaitableTimer(IntPtr timer);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("powrprof.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.U1)]
    static extern bool SetSuspendState([MarshalAs(UnmanagedType.U1)] bool hibernate,[MarshalAs(UnmanagedType.U1)] bool force,[MarshalAs(UnmanagedType.U1)] bool disableWake);
    public static void SleepWithWakeTimer() {
        IntPtr token;
        if (!OpenProcessToken(GetCurrentProcess(),0x28,out token)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try {
            long luid;
            if (!LookupPrivilegeValue(null,"SeShutdownPrivilege",out luid)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var privileges = new Privileges { Count=1,Luid=luid,Attributes=2 };
            if (!AdjustTokenPrivileges(token,false,ref privileges,0,IntPtr.Zero,IntPtr.Zero) || Marshal.GetLastWin32Error()==1300)
                throw new Win32Exception(Marshal.GetLastWin32Error());
        } finally { CloseHandle(token); }
        IntPtr timer = CreateWaitableTimer(IntPtr.Zero,true,null);
        if (timer==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try {
            long due=DateTime.UtcNow.AddSeconds(30).ToFileTimeUtc();
            if (!SetWaitableTimer(timer,ref due,0,IntPtr.Zero,IntPtr.Zero,true) || Marshal.GetLastWin32Error()==50)
                throw new InvalidOperationException("An automatic wake timer is not supported; real sleep was not attempted.");
            if (!SetSuspendState(false,false,false)) throw new Win32Exception(Marshal.GetLastWin32Error());
        } finally { CancelWaitableTimer(timer); CloseHandle(timer); }
    }
}
'@

function Read-ProtectedObservation {
    param([string]$Name)
    $status = Read-InstallerStatus
    if ($status.BinaryCommandLine -notmatch '\\Versions\\([0-9a-f]{64})\\NetTrace.AppUsageService.exe') { throw 'Unexpected registered collector version path.' }
    $installed = Assert-OwnedRegistration $status $Matches[1]
    if (-not $status.Automatic -or -not $status.DelayedAutomaticStart) { throw 'The production startup mode changed.' }
    Assert-NoReparse $databasePath
    $snapshot = [AppUsageReadOnlyNative]::Read($databasePath,(Join-Path $WpfBuildDirectory 'AppTracking\Service\runtimes\win-x64\native\e_sqlite3.dll'))
    foreach ($row in $snapshot.Rows) { if ([long]$row[5] -lt 0 -or [long]$row[6] -lt 0) { throw 'A persisted application byte counter is negative.' } }
    $nativeSession = [AppUsageEtwProbe.NativeEtwController]::Query('NetTrace-AppUsage003-Collector-938F90CEB2A34EF7A67BA5A43521EE77')
    if (-not $nativeSession -or $nativeSession.SessionGuid.ToString() -ne '938f90ce-b2a3-4ef7-a67b-a5a43521ee77' -or $nativeSession.HasLogFile -or
        -not [AppUsageEtwProbe.NativeEtwController]::IsExpectedRealtimeMode($nativeSession.LogFileMode)) { throw 'The single owned real-time ETW session identity is invalid.' }
    $sessions = @(& logman.exe query -ets 2>&1 | ForEach-Object { if ($_ -match '^(NetTrace[^\s]+)\s+') { $Matches[1] } })
    if ($LASTEXITCODE -ne 0 -or $sessions.Count -ne 1 -or $sessions[0] -cne $nativeSession.Name) { throw 'Unexpected duplicate/orphan NetTrace ETW sessions.' }
    $processes = @(Get-CimInstance Win32_Process -Filter "Name='NetTrace.AppUsageService.exe'")
    if ($processes.Count -ne 1 -or $processes[0].ProcessId -ne $status.ProcessId -or $processes[0].ExecutablePath -cne $installed.Image) {
        throw 'Expected exactly one owned collector process matching SCM.'
    }
    $file = Get-Item -LiteralPath $databasePath
    $observation = [pscustomobject]@{ Success=$true; Name=$Name; ObservedAtUtc=[DateTimeOffset]::UtcNow.ToString('O'); Status=$status; Installed=$installed;
        Configuration=[pscustomobject]@{ Identity=$status.Identity; Automatic=$status.Automatic; DelayedAutomaticStart=$status.DelayedAutomaticStart; Description=$status.Description; Recovery=$status.Recovery };
        Database=[pscustomobject]@{ Rows=$snapshot.Rows; Integrity='ok'; SchemaVersion=$snapshot.UserVersion; ApplicationId=$snapshot.ApplicationId; CreationTimeUtcTicks=$file.CreationTimeUtc.Ticks; SchemaHash=(Get-TextHash ($snapshot.Schema | ConvertTo-Json -Depth 8 -Compress)) };
        CollectorProcessCount=$processes.Count; NetTraceEtwSessions=$sessions; Etw=$nativeSession; LostEventCountAvailable=$true; LostEvents=$nativeSession.EventsLost }
    Write-Json ('snapshot-'+$Name+'.json') $observation
    return $observation
}

function Assert-Preservation {
    param([object]$Before,[object]$After)
    if (($Before.Configuration | ConvertTo-Json -Depth 12 -Compress) -cne ($After.Configuration | ConvertTo-Json -Depth 12 -Compress)) { throw 'The service configuration changed across a lifecycle event.' }
    if ($Before.Database.SchemaHash -cne $After.Database.SchemaHash -or $Before.Database.CreationTimeUtcTicks -ne $After.Database.CreationTimeUtcTicks) { throw 'The App Usage database file/schema was replaced.' }
    $records = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
    foreach ($row in $After.Database.Rows) { $records.Add((Get-HistoryDateKey $row[0])+[char]0+$row[1],$row) }
    foreach ($row in $Before.Database.Rows) {
        $key = (Get-HistoryDateKey $row[0])+[char]0+$row[1]
        if (-not $records.ContainsKey($key)) { throw 'Committed history disappeared across recovery.' }
        $current = $records[$key]
        if ([long]$current[5] -lt [long]$row[5] -or [long]$current[6] -lt [long]$row[6] -or (Get-HistoryTimestamp $current[7]) -lt (Get-HistoryTimestamp $row[7])) {
            throw 'Committed history bytes or timestamps decreased across recovery.'
        }
    }
}

function Wait-Running {
    param([int]$PreviousPid=0,[int]$Seconds=100)
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt $Seconds) {
        $status = Read-InstallerStatus
        if ($status.Running -and $status.ProcessId -gt 0 -and ($PreviousPid -eq 0 -or $status.ProcessId -ne $PreviousPid)) { return $status }
        Start-Sleep -Milliseconds 250
    }
    throw 'The service did not reach the expected Running recovery state.'
}

$failure = $null
$results = @()
try {
    $baseline = Read-ProtectedObservation 'Baseline'
    if ($SnapshotOnly) {
        Get-Content -LiteralPath (Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'NetTrace\AppUsageService\service.log') -Tail 100 |
            Set-Content -LiteralPath (Join-Path $RunDirectory 'service-log-tail.txt') -Encoding UTF8
        return
    }
    foreach ($eventName in $eventNames) {
        $null = Wait-Marker ('ready-'+$eventName+'.json') 180
        $protectedTraffic = $eventName -eq 'Update'
        if ($protectedTraffic) { $null = Read-ProtectedObservation ('TrafficStart-Before-'+$eventName) }
        Write-Json ('traffic-ready-Before-'+$eventName+'.json') ([pscustomobject]@{ Ready=$true; ProtectedEvidence=$protectedTraffic })
        $null = Wait-Marker ('traffic-done-Before-'+$eventName+'.json') 120
        if ($protectedTraffic) { Start-Sleep -Milliseconds 5500 }
        $before = Read-ProtectedObservation ('Before-'+$eventName)
        if ($protectedTraffic) {
            $generated = Get-Content -LiteralPath (Join-Path $RunDirectory ('traffic-Before-'+$eventName+'.json')) -Raw | ConvertFrom-Json
            $serverBytes = Get-PersistedApplicationBytes $before.Database $generated.ServerPath
            $delta = [pscustomobject]@{ ServerDownload=$serverBytes.Download-$generated.ServerBefore.Download; ServerUpload=$serverBytes.Upload-$generated.ServerBefore.Upload }
            $exact = $delta.ServerDownload -ge $generated.ResponseContentBytes -and $delta.ServerUpload -gt 0 -and
                $delta.ServerDownload -le $generated.MaximumDownloadDelta -and $delta.ServerUpload -le $generated.MaximumUploadDelta
            Write-Json ('traffic-verified-Before-'+$eventName+'.json') ([pscustomobject]@{ Success=$exact; CounterDelta=$delta; Evidence='Protected read-only SQLite rows; old IPC may be unavailable' })
            if (-not $exact) { throw 'Before-update local payload did not match its exact protected persisted counters.' }
        }
        Write-Json ('event-started-'+$eventName+'.json') ([pscustomobject]@{ StartedAtUtc=[DateTimeOffset]::UtcNow.ToString('O'); BeforeProcessId=$before.Status.ProcessId })
        $watch = [Diagnostics.Stopwatch]::StartNew()
        $sleepEvents = @()
        switch ($eventName) {
            'Update' {
                & $installer install --source (Join-Path $WpfBuildDirectory 'AppTracking\Service') --client-sid $ClientSid *> (Join-Path $RunDirectory 'installer-update.log')
                if ($LASTEXITCODE -ne 0) { throw 'The real production installer update failed.' }
                $null = Wait-Running $before.Status.ProcessId
            }
            'Restart' {
                Stop-Service -Name $serviceName
                (Get-Service -Name $serviceName).WaitForStatus([ServiceProcess.ServiceControllerStatus]::Stopped,[TimeSpan]::FromSeconds(35))
                if ([AppUsageEtwProbe.NativeEtwController]::Query($before.Etw.Name)) { throw 'The owned ETW session survived a completed graceful SCM stop.' }
                Write-Json 'graceful-stop-clean.json' ([pscustomobject]@{ OwnedEtwAbsent=$true })
                Start-Service -Name $serviceName
                $null = Wait-Running $before.Status.ProcessId
            }
            'ForcedRecovery' {
                $ownedProcess = Get-CimInstance Win32_Process -Filter ('ProcessId='+$before.Status.ProcessId)
                $current = Read-InstallerStatus
                if (-not $current.Running -or $current.ProcessId -ne $ownedProcess.ProcessId -or $ownedProcess.ExecutablePath -cne $before.Installed.Image) {
                    throw 'The exact owned SCM process changed before forced termination.'
                }
                Stop-Process -Id $ownedProcess.ProcessId -Force
                $null = Wait-Running $before.Status.ProcessId
            }
            'Sleep' {
                $powerAvailable = (& powercfg.exe /a) -join "`n"
                $wakePolicy = (& powercfg.exe /q SCHEME_CURRENT SUB_SLEEP RTCWAKE) -join "`n"
                if ($powerAvailable -notmatch 'Standby \(S3\)' -or $wakePolicy -notmatch 'Current AC Power Setting Index: 0x00000001' -or $wakePolicy -notmatch 'Current DC Power Setting Index: 0x00000001') {
                    throw 'S3 sleep with existing enabled wake timers is not available; power settings were not changed.'
                }
                $sleepStart = [DateTime]::UtcNow
                Write-Json 'sleep-about-to-suspend.json' ([pscustomobject]@{ StartedAtUtc=$sleepStart.ToString('O'); WakeDueAtUtc=$sleepStart.AddSeconds(30).ToString('O') })
                [LifecycleSleepValidation]::SleepWithWakeTimer()
                $null = Wait-Running
                $sleepEvents = @(Get-WinEvent -FilterHashtable @{ LogName='System'; ProviderName='Microsoft-Windows-Kernel-Power'; Id=@(42,107); StartTime=$sleepStart.ToLocalTime().AddSeconds(-1) } -ErrorAction SilentlyContinue |
                    Where-Object { $_.TimeCreated.ToUniversalTime() -ge $sleepStart.AddSeconds(-1) } | Select-Object Id,TimeCreated,RecordId)
                if (-not ($sleepEvents | Where-Object Id -EQ 42) -or -not ($sleepEvents | Where-Object Id -EQ 107)) { throw 'The real S3 sleep and resume event-log pair was not observed.' }
            }
        }
        $after = Read-ProtectedObservation ('After-'+$eventName)
        Assert-Preservation $before $after
        Assert-Preservation $baseline $after
        $result = [pscustomobject]@{ Success=$true; Event=$eventName; CompletedAtUtc=[DateTimeOffset]::UtcNow.ToString('O'); ElapsedSeconds=$watch.Elapsed.TotalSeconds;
            BeforeProcessId=$before.Status.ProcessId; AfterProcessId=$after.Status.ProcessId; BeforeSessionHandle=$before.Etw.SessionHandle; AfterSessionHandle=$after.Etw.SessionHandle;
            BeforeVersion=$before.Installed.Version; AfterVersion=$after.Installed.Version; DatabasePreserved=$true; ConfigurationPreserved=$true;
            PreservedRows=@($before.Database.Rows).Count; BeforeLostEvents=$before.LostEvents; AfterLostEvents=$after.LostEvents; SleepEvents=$sleepEvents }
        Write-Json ('event-finished-'+$eventName+'.json') $result
        Write-Json ('traffic-ready-After-'+$eventName+'.json') ([pscustomobject]@{ Ready=$true })
        $null = Wait-Marker ('traffic-done-After-'+$eventName+'.json') 120
        $continued = Read-ProtectedObservation ('Continued-'+$eventName)
        Assert-Preservation $after $continued
        $null = Wait-Marker ('observed-'+$eventName+'.json') 70
        $results += $result
    }
}
catch {
    $failure = $_.Exception.Message
    if ($eventName -and -not (Test-Path -LiteralPath (Join-Path $RunDirectory ('event-finished-'+$eventName+'.json')))) {
        Write-Json ('event-finished-'+$eventName+'.json') ([pscustomobject]@{ Success=$false; Error=$failure; CompletedAtUtc=[DateTimeOffset]::UtcNow.ToString('O') })
    }
}
finally {
    # Preserve the installed production service even after a validation failure.
    # A failed automatic recovery remains a failure even if this restores service.
    try { if (-not $SnapshotOnly -and -not (Read-InstallerStatus).Running) { Start-Service -Name $serviceName; $null = Wait-Running } }
    catch { $failure = ($failure+'; restoring the existing service failed: '+$_.Exception.Message) }
    Write-Json 'worker-report.json' ([pscustomobject]@{ Success=($null -eq $failure); Error=$failure; Results=$results;
        FinalServiceRunning=(Read-InstallerStatus).Running; HistoryClearInvoked=$false; UninstallInvoked=$false; RebootPerformed=$false })
}
if ($failure) { exit 1 }
