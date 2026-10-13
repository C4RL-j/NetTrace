[CmdletBinding()]
param(
    [ValidateSet('Before','After')][string]$ReadOnlyWorker,
    [string]$RunDirectory,
    [string]$WpfBuildDirectory,
    [string]$ClientSid,
    [string]$ExpectedInstalledVersion = 'af379cbad2ab8ec23ac647a01a3e28731dee8f25adfa0dfa826d243f3543a7f2'
)

# Explicit interactive validation only; NetTrace never launches this script.
# The two elevated workers only read protected service/database state. The real
# normal-token Settings button performs the sole update. No clear/uninstall runs.
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$verificationRoot = Join-Path $repository 'bin\AppUsage0055Verification'
$serviceName = 'NetTraceAppUsageCollector'
$databasePath = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'NetTrace\app-usage.db'
$installationRoot = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'NetTrace\AppUsageCollector'
$isAdministrator = ([Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

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

if (-not $ReadOnlyWorker) {
    if ($isAdministrator) { throw 'Start this workflow with the normal desktop token. Only the explicit installer and protected readers elevate.' }
    if ($ExpectedInstalledVersion -notmatch '^[0-9a-f]{64}$') { throw 'ExpectedInstalledVersion must be an exact installed package SHA-256.' }
    New-Item -ItemType Directory -Path $verificationRoot -Force | Out-Null
    $RunDirectory = Join-Path $verificationRoot ('run-'+[Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $RunDirectory | Out-Null
    if (-not $WpfBuildDirectory) { $WpfBuildDirectory = Join-Path $verificationRoot 'AllBuilds\NetTrace.WpfTests' }
    $WpfBuildDirectory = Assert-VerificationPath $WpfBuildDirectory
    Assert-NoReparse $WpfBuildDirectory
    $installer = Join-Path $WpfBuildDirectory 'AppTracking\Installer\NetTrace.AppUsageInstaller.exe'
    $harness = Join-Path $WpfBuildDirectory 'NetTrace.WpfTests.exe'
    foreach ($path in @($harness,$installer,(Join-Path $WpfBuildDirectory 'AppTracking\Service\NetTrace.AppUsageService.exe'))) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw 'The complete prebuilt 005.5 WPF output is missing.' }
    }
    $ClientSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $bundledVersion = Read-KitVersion (Join-Path $WpfBuildDirectory 'AppTracking\Service')
    $baseline = Read-InstallerStatus
    if (-not $baseline.Running -or $baseline.BinaryCommandLine -notlike ('*\'+$ExpectedInstalledVersion+'\NetTrace.AppUsageService.exe"*') -or $bundledVersion -eq $ExpectedInstalledVersion) {
        throw 'This live workflow requires the exact expected older running collector and a different current bundled kit.'
    }
    Write-Json 'normal-context.json' ([pscustomobject]@{ ClientSid=$ClientSid; ClientElevated=$false; ExpectedInstalledVersion=$ExpectedInstalledVersion; BundledVersion=$bundledVersion; WpfDirectory=$WpfBuildDirectory })

    function Invoke-ReadOnlyWorker {
        param([string]$Phase)
        Write-Host ('A separate explicit UAC prompt will read protected '+$Phase.ToLowerInvariant()+'-update history/configuration. It makes no service or database changes.')
        $arguments = '-NoProfile -ExecutionPolicy RemoteSigned -File "'+$PSCommandPath+'" -ReadOnlyWorker '+$Phase+' -RunDirectory "'+$RunDirectory+'" -WpfBuildDirectory "'+$WpfBuildDirectory+'" -ClientSid '+$ClientSid+' -ExpectedInstalledVersion '+$ExpectedInstalledVersion
        $worker = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -Verb RunAs -WindowStyle Hidden -PassThru -ArgumentList $arguments
        try {
            $watch = [Diagnostics.Stopwatch]::StartNew()
            while (-not $worker.HasExited -and $watch.Elapsed.TotalSeconds -lt 80) {
                $null = $worker.WaitForExit(250)
                $worker.Refresh()
            }
            if (-not $worker.HasExited) { throw 'The bounded protected read-only worker did not finish; it was left alive to finish its read.' }
            $path = Join-Path $RunDirectory ($Phase.ToLowerInvariant()+'-protected.json')
            if (-not (Test-Path -LiteralPath $path)) { throw 'The protected read-only report is missing.' }
            $report = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
            if (-not $report.Success) { throw ('Protected read-only validation failed: '+$report.Error) }
            return $report
        }
        finally { $worker.Dispose() }
    }

    $before = Invoke-ReadOnlyWorker 'Before'
    if (@($before.Database.Rows).Count -eq 0) { throw 'The requested preservation validation requires existing production history.' }
    Write-Output 'The real normal-token Settings Update App Tracking button will now request its installer UAC prompt. The service stays installed afterward.'
    $clientReportPath = Join-Path $RunDirectory 'Update-client.json'
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $harness
    $start.Arguments = '--app-tracking-update-live-validation --output "'+$clientReportPath+'"'
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $client = [Diagnostics.Process]::Start($start)
    $stdout = $client.StandardOutput.ReadToEndAsync()
    $stderr = $client.StandardError.ReadToEndAsync()
    $clientReport = $null
    $after = $null
    $workflowError = $null
    try {
        $watch = [Diagnostics.Stopwatch]::StartNew()
        while (-not $client.HasExited -and $watch.Elapsed.TotalSeconds -lt 205) {
            $null = $client.WaitForExit(250)
            $client.Refresh()
        }
        if (-not $client.HasExited) { throw 'The bounded real update page observation did not finish.' }
        [IO.File]::WriteAllText((Join-Path $RunDirectory 'Update-client.log'),$stdout.GetAwaiter().GetResult())
        [IO.File]::WriteAllText((Join-Path $RunDirectory 'Update-client-error.log'),$stderr.GetAwaiter().GetResult())
        if (-not (Test-Path -LiteralPath $clientReportPath)) { throw 'The normal-token update report is missing.' }
        $clientReport = Get-Content -LiteralPath $clientReportPath -Raw | ConvertFrom-Json
        if (-not $clientReport.Completed -or -not $clientReport.Success -or $client.ExitCode -ne 0) { throw ('Real update page validation failed: '+$clientReport.Error) }
        $after = Invoke-ReadOnlyWorker 'After'
        $records = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
        foreach ($row in @($after.Database.Rows)) { $records.Add((Get-HistoryDateKey $row[0])+[char]0+$row[1],$row) }
        foreach ($row in @($before.Database.Rows)) {
            $key = (Get-HistoryDateKey $row[0])+[char]0+$row[1]
            if (-not $records.ContainsKey($key)) { throw 'An existing persisted App Usage date/application row disappeared during update.' }
            $current = $records[$key]
            $beforeSeen = Get-HistoryTimestamp $row[7]
            $afterSeen = Get-HistoryTimestamp $current[7]
            if ([long]$current[5] -lt [long]$row[5] -or [long]$current[6] -lt [long]$row[6] -or
                $afterSeen -lt $beforeSeen) { throw 'An existing persisted App Usage row lost bytes or its last-seen timestamp during update.' }
            if ($afterSeen -eq $beforeSeen -and ($current[2] -cne $row[2] -or $current[3] -cne $row[3] -or $current[4] -cne $row[4])) { throw 'Existing application metadata changed without a new observation.' }
        }
        if ($before.Database.SchemaHash -cne $after.Database.SchemaHash -or
            $before.Database.CreationTimeUtcTicks -ne $after.Database.CreationTimeUtcTicks) { throw 'The App Usage database file/schema was replaced during update.' }
        $configBefore = $before.Configuration | ConvertTo-Json -Depth 10 -Compress
        $configAfter = $after.Configuration | ConvertTo-Json -Depth 10 -Compress
        if ($configBefore -cne $configAfter) { throw 'The service startup, identity, description, or recovery configuration changed.' }
        if ($before.Status.ProcessId -eq $after.Status.ProcessId -or -not $after.ClearHistoryEndpointAvailable) { throw 'The collector did not restart into the new history-management-capable build.' }
    }
    catch { $workflowError = $_.Exception.Message }
    finally {
        # Killing this isolated UI observer never terminates a launched installer
        # or the production collector; those must finish under their own lifetime.
        if (-not $client.HasExited) { Stop-Process -Id $client.Id -Force }
        $client.Dispose()
    }
    $summary = [pscustomobject]@{
        Success=($null -eq $workflowError); Error=$workflowError; OldVersion=$before.Installed.Version; NewVersion=$bundledVersion;
        Client=$clientReport; DatabasePreserved=($null -eq $workflowError -and $null -ne $after);
        ExistingRowsBefore=@($before.Database.Rows).Count; RowsAfter=if ($after) { @($after.Database.Rows).Count } else { $null };
        ServiceConfigurationPreserved=($null -eq $workflowError -and $null -ne $after);
        ClearHistoryEndpointAvailable=if ($after) { $after.ClearHistoryEndpointAvailable } else { $null };
        FinalServiceRunning=if ($after) { $after.Status.Running } else { (Read-InstallerStatus).Running };
        HistoryClearInvoked=$false; UninstallInvoked=$false; EvidenceDirectory=$RunDirectory
    }
    Write-Json 'workflow-summary.json' $summary
    $summary | ConvertTo-Json -Depth 20
    if ($workflowError) { throw ($workflowError+'. Evidence: '+$RunDirectory) }
    return
}

if (-not $isAdministrator) { throw 'The explicitly invoked protected read-only worker requires elevation.' }
$RunDirectory = Assert-VerificationPath $RunDirectory
$WpfBuildDirectory = Assert-VerificationPath $WpfBuildDirectory
Assert-NoReparse $RunDirectory
Assert-NoReparse $WpfBuildDirectory
if ([Security.Principal.WindowsIdentity]::GetCurrent().User.Value -ne $ClientSid) { throw 'Elevate the same account that runs the normal page.' }
$context = Get-Content -LiteralPath (Join-Path $RunDirectory 'normal-context.json') -Raw | ConvertFrom-Json
if ($context.ClientSid -ne $ClientSid -or $context.ExpectedInstalledVersion -cne $ExpectedInstalledVersion -or $context.WpfDirectory -cne $WpfBuildDirectory) { throw 'The explicit protected observation context is invalid.' }
$installer = Join-Path $WpfBuildDirectory 'AppTracking\Installer\NetTrace.AppUsageInstaller.exe'
$success = $false
$errorText = $null
$status = $null
$installed = $null
$database = $null
$configuration = $null
$managementAvailable = $false

# Native SQLite reads only: flags SQLITE_OPEN_READONLY, no extension loading,
# schema initialization, migration, checkpoint, maintenance, or file deletion.
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

try {
    $status = Read-InstallerStatus
    $expectedVersion = if ($ReadOnlyWorker -eq 'Before') { $ExpectedInstalledVersion } else { $context.BundledVersion }
    $installed = Assert-OwnedRegistration $status $expectedVersion
    if (-not $status.Automatic -or -not $status.DelayedAutomaticStart) { throw 'The production startup mode is not Automatic (Delayed Start).' }
    Assert-NoReparse $databasePath
    if (-not (Test-Path -LiteralPath $databasePath -PathType Leaf)) { throw 'The existing production App Usage database is absent.' }
    $native = Join-Path $WpfBuildDirectory 'AppTracking\Service\runtimes\win-x64\native\e_sqlite3.dll'
    if (-not (Test-Path -LiteralPath $native -PathType Leaf)) { throw 'The prebuilt owned win-x64 SQLite native dependency is absent.' }
    $snapshot = [AppUsageReadOnlyNative]::Read($databasePath,$native)
    $file = Get-Item -LiteralPath $databasePath
    $database = [pscustomobject]@{
        Path=$databasePath; Length=$file.Length; CreationTimeUtcTicks=$file.CreationTimeUtc.Ticks;
        ApplicationId=$snapshot.ApplicationId; SchemaVersion=$snapshot.UserVersion; Rows=$snapshot.Rows;
        SchemaHash=(Get-TextHash ($snapshot.Schema | ConvertTo-Json -Depth 8 -Compress));
        RowsHash=(Get-TextHash ($snapshot.Rows | ConvertTo-Json -Depth 8 -Compress))
    }
    $configuration = [pscustomobject]@{ Identity=$status.Identity; Automatic=$status.Automatic; DelayedAutomaticStart=$status.DelayedAutomaticStart; Description=$status.Description; Recovery=$status.Recovery }
    $managementAvailable = [AppUsageReadOnlyNative]::ManagementEndpointExists()
    $current = Read-InstallerStatus
    if (-not $current.Running -or $current.ProcessId -ne $status.ProcessId -or $current.BinaryCommandLine -cne $status.BinaryCommandLine) { throw 'The running registration changed during the bounded read-only snapshot.' }
    $success = $true
}
catch { $errorText = $_.Exception.Message }
finally {
    Write-Json ($ReadOnlyWorker.ToLowerInvariant()+'-protected.json') ([pscustomobject]@{ Success=$success; Error=$errorText; ReadOnly=$true; Phase=$ReadOnlyWorker; Status=$status; Installed=$installed; Database=$database; Configuration=$configuration; ClearHistoryEndpointAvailable=$managementAvailable; HistoryClearInvoked=$false; ServiceMutated=$false })
}
if (-not $success) { exit 1 }
