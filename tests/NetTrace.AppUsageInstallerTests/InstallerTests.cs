using NetTrace.AppUsageInstaller;

internal static class InstallerTests
{
    private const string UserSid = "S-1-5-21-111-222-333-1001";
    private const string Source = @"C:\Owned Service Publish";
    private static readonly string Version = new('a', 64);
    private static InstallationConfiguration Configuration => InstallationConfiguration.Create(
        @"C:\Program Files", @"C:\ProgramData", UserSid, Version);
    private static InstallationConfiguration PreviousConfiguration => InstallationConfiguration.Create(
        @"C:\Program Files", @"C:\ProgramData", UserSid, new string('b', 64));

    public static (string Name, Func<Task> Run)[] Cases =>
    [
        ("Production model fixes service identity, delayed automatic startup, and ownership marker", Check.Sync(ProductionModel)),
        ("Production paths keep versioned binaries in Program Files and database in ProgramData", Check.Sync(ProductionPaths)),
        ("Service command quotes paths with spaces and carries only intended host options", Check.Sync(CommandLine)),
        ("Binary versions normalize SHA-256 case while keeping database path stable", Check.Sync(BinaryVersion)),
        ("Relative installation roots cannot redirect privileged writes", Check.Sync(InvalidRoots)),
        ("Invalid binary version cannot become a privileged installation path", Check.Sync(InvalidVersions)),
        ("Malformed, broad, built-in, and domain-only SIDs cannot become intended users", Check.Sync(InvalidSids)),
        ("Recovery restarts after 5, 15, and 60 seconds then stops retrying", Check.Sync(RecoveryActions)),
        ("Recovery resets after 24 hours and includes non-crash failures", Check.Sync(RecoveryReset)),
        ("Shared production recovery actions cannot be changed by callers", Check.Sync(RecoveryReadOnly)),
        ("Missing registration chooses create without an ownership assertion", Check.Sync(CreateDecision)),
        ("Owned production registration chooses update for running or stopped state", Check.Sync(UpdateDecision)),
        ("Foreign registration under the production name is refused", Check.Sync(ForeignDecision)),
        ("A development or unrelated service name cannot enter production update", Check.Sync(WrongNameDecision)),
        ("Install invocation retains an absolute source and explicit intended user", Check.Sync(InstallInvocation)),
        ("Install without explicit SID leaves the caller identity choice to the elevated tool", Check.Sync(DefaultSidInvocation)),
        ("Uninstall and status parse without arbitrary privileged options", Check.Sync(ReadOnlyInvocation)),
        ("Unknown operation and case variants are rejected", Check.Sync(InvalidOperations)),
        ("Missing, unknown, and duplicate installer options are rejected", Check.Sync(InvalidOptions)),
        ("Installer invocation rejects relative source and invalid user SID", Check.Sync(InvalidInvocationValues)),
        ("Uninstall and status cannot accept install-only or destructive options", Check.Sync(InvalidUninstallOptions)),
        ("New install prepares protected files then configures and verifies IPC", NewInstall),
        ("Installing twice updates the same registration without a duplicate service", IdempotentInstall),
        ("Valid update prepares files before disabling recovery and stopping the old service", UpdateInstall),
        ("An already stopped installation follows the same safe update path", StoppedUpdate),
        ("File preparation failure leaves a running existing installation alone", PreparationFailure),
        ("New install rolls back an owned partial registration after configure failure", NewConfigureFailure),
        ("New install rolls back a running registration when IPC verification fails", NewStartFailure),
        ("Failed update restores previous protected version and verifies running IPC", UpdateStartFailure),
        ("Failed update configuration restores the existing installation", UpdateConfigureFailure),
        ("Failed previous-version rollback leaves owned service stopped and retains both errors", UpdateRollbackFailure),
        ("Rollback refuses a partial registration that became foreign", ForeignPartialRollback),
        ("Rollback cleanup failure retains both original and cleanup errors", CleanupFailure),
        ("Failure before creating any registration cannot delete another service", NoPartialRegistration),
        ("Foreign existing installation is rejected before preparing files", ForeignInstall),
        ("Uninstall disables recovery before stop and deletes only owned registration", UninstallRunning),
        ("Uninstall of an already stopped service still verifies absent and exact cleanup", UninstallStopped),
        ("Uninstall is idempotent when service is absent", UninstallAbsent),
        ("Uninstall refuses foreign registrations before stopping or deleting", UninstallForeign),
        ("Stop failure prevents deletion and never reports successful uninstall", UninstallStopFailure),
        ("Delete failure cannot report verified absence", UninstallDeleteFailure),
        ("Failure to verify absence remains an uninstall failure", UninstallVerificationFailure),
        ("Non-elevated install is rejected before validation or mutation", NonElevatedInstall),
        ("Non-elevated uninstall is rejected before reading SCM or mutation", NonElevatedUninstall),
        ("Invalid install environment is rejected before any service or file mutation", InvalidInstallEnvironment),
        ("Invalid uninstall environment is rejected before SCM or cleanup mutation", InvalidUninstallEnvironment),
        ("Invalid intended user is rejected before environment or mutation", InvalidInstallSid),
        ("Pre-canceled install does not prepare files or configure a service", CanceledInstall),
        ("Pre-canceled uninstall does not change recovery or stop a service", CanceledUninstall),
        ("CLI invalid operation reports a usage error without creating a platform operation", InvalidCli),
        ("CLI ordinary-token install clearly reports elevation and returns code 2", NonElevatedInstallCli),
        ("CLI ordinary-token uninstall clearly reports elevation and returns code 2", NonElevatedUninstallCli),
        ("CLI help is available unelevated and describes retained database and no automatic UAC", HelpCli),
    ];

    private static void ProductionModel()
    {
        var model = Configuration;
        Check.Equal("NetTraceAppUsageCollector", model.ServiceName, "production identity");
        Check.Equal("LocalSystem", model.Identity, "tested collector capability identity");
        Check.Equal("Automatic (Delayed Start)", model.StartupMode, "persistent production startup");
        Check.True(!string.IsNullOrWhiteSpace(InstallationConfiguration.OwnershipDescription), "owned installations have a marker");
    }
    private static void ProductionPaths()
    {
        var model = Configuration;
        Check.Equal(@"C:\Program Files\NetTrace\AppUsageCollector", model.InstallationRoot, "protected binary root");
        Check.Equal(Path.Combine(model.InstallationRoot, "Versions", Version), model.BinaryDirectory, "immutable versioned output");
        Check.Equal(Path.Combine(model.BinaryDirectory, "NetTrace.AppUsageService.exe"), model.ExecutablePath, "existing service host executable");
        Check.Equal(@"C:\ProgramData\NetTrace", model.DataDirectory, "production persistence directory");
        Check.Equal(@"C:\ProgramData\NetTrace\app-usage.db", model.DatabasePath, "existing database convention");
        Check.Equal(@"C:\ProgramData\NetTrace\AppUsageService\service.log", model.LogPath, "bounded service log separate from database");
    }
    private static void CommandLine()
    {
        var model = Configuration;
        Check.Equal($"\"{model.ExecutablePath}\" --client-sid {UserSid} --service-name NetTraceAppUsageCollector --log \"{model.LogPath}\"", model.BinaryCommandLine, "space-safe SCM command");
        Check.True(!model.BinaryCommandLine.Contains("--data-root", StringComparison.Ordinal), "production uses the established ProgramData persistence root");
        Check.True(!model.BinaryCommandLine.Contains("--cleanup", StringComparison.Ordinal), "normal service invocation cannot run cleanup mode");
    }
    private static void BinaryVersion()
    {
        var lower = Configuration;
        var upper = InstallationConfiguration.Create(@"C:\Program Files", @"C:\ProgramData", UserSid, Version.ToUpperInvariant());
        Check.Equal(lower.BinaryDirectory, upper.BinaryDirectory, "hash casing has one version path");
        var next = InstallationConfiguration.Create(@"C:\Program Files", @"C:\ProgramData", UserSid, new string('b', 64));
        Check.True(lower.BinaryDirectory != next.BinaryDirectory, "update stages a distinct binary version");
        Check.Equal(lower.DatabasePath, next.DatabasePath, "updating binaries cannot select a new history database");
    }
    private static void InvalidRoots()
    {
        foreach (string root in new[] { "", ".", @"relative\folder", @"C:relative" })
        {
            Check.Throws<ArgumentException>(() => InstallationConfiguration.Create(root, @"C:\ProgramData", UserSid, Version), "relative binary root");
            Check.Throws<ArgumentException>(() => InstallationConfiguration.Create(@"C:\Program Files", root, UserSid, Version), "relative data root");
        }
    }
    private static void InvalidVersions()
    {
        foreach (string version in new[] { "", "short", new string('a', 63), new string('a', 65), new string('g', 64), "../" + new string('a', 61) })
            Check.Throws<ArgumentException>(() => InstallationConfiguration.Create(@"C:\Program Files", @"C:\ProgramData", UserSid, version), "invalid version cannot escape installation root");
    }
    private static void InvalidSids()
    {
        foreach (string sid in new[] { "", "not-a-sid", "S-1-1-0", "S-1-5-7", "S-1-5-18", "S-1-5-19", "S-1-5-32-545", "S-1-5-21-111-222-333", "S-1-5-21-42", UserSid + " extra" })
            Check.Throws<ArgumentException>(() => InstallationConfiguration.ValidateClientSid(sid), "only complete Windows user SID is accepted");
        InstallationConfiguration.ValidateClientSid(UserSid);
    }
    private static void RecoveryActions()
    {
        Check.Sequence(new[] { "Restart", "Restart", "Restart", "None" }, RecoveryPolicy.Production.Actions.Select(action => action.Kind), "finite automatic retry policy");
        Check.Sequence(new[] { 5000, 15000, 60000, 0 }, RecoveryPolicy.Production.Actions.Select(action => action.DelayMilliseconds), "bounded retries use increasing delay");
    }
    private static void RecoveryReset()
    {
        Check.Equal(86400, RecoveryPolicy.Production.ResetPeriodSeconds, "failure counter resets after one day");
        Check.True(RecoveryPolicy.Production.RecoverNonCrashFailures, "service-reported fatal failures also restart");
        Check.True(ReferenceEquals(RecoveryPolicy.Production, Configuration.Recovery), "installer uses the reviewed policy");
    }
    private static void RecoveryReadOnly()
    {
        Check.True(RecoveryPolicy.Production.Actions is IList<RecoveryAction> { IsReadOnly: true }, "shared policy cannot be edited by caller");
        Check.Throws<NotSupportedException>(() => ((IList<RecoveryAction>)RecoveryPolicy.Production.Actions)[0] = new("None", 0), "changing restart policy is refused");
        Check.Equal("Restart", RecoveryPolicy.Production.Actions[0].Kind, "policy survives attempted mutation");
    }
    private static void CreateDecision()
    {
        Check.Equal(InstallDecision.Create, InstallationDecision.ForInstall(null, false), "no registration means create");
        Check.Equal(InstallDecision.Create, InstallationDecision.ForInstall(null, true), "absence cannot become duplicate update");
    }
    private static void UpdateDecision()
    {
        Check.Equal(InstallDecision.Update, InstallationDecision.ForInstall(Registration(running: true), true), "owned running installation updates");
        Check.Equal(InstallDecision.Update, InstallationDecision.ForInstall(Registration(running: false), true), "owned stopped installation updates");
    }
    private static void ForeignDecision() => Check.Throws<InvalidOperationException>(() => InstallationDecision.ForInstall(Registration(), false), "ownership is mandatory");
    private static void WrongNameDecision()
    {
        foreach (string name in new[] { "NetTraceAppUsageCollectorDev", "OtherService", "nettraceappusagecollector" })
            Check.Throws<InvalidOperationException>(() => InstallationDecision.ForInstall(Registration() with { ServiceName = name }, true), "exact production name is required");
    }
    private static void InstallInvocation()
    {
        var invocation = InstallerInvocation.Parse(["install", "--source", Source, "--client-sid", UserSid]);
        Check.Equal("install", invocation.Operation, "operation");
        Check.Equal(Source, invocation.SourceDirectory, "absolute source");
        Check.Equal(UserSid, invocation.ClientSid, "explicit intended user");
        var reversed = InstallerInvocation.Parse(["install", "--client-sid", UserSid, "--source", Source]);
        Check.Equal(invocation, reversed, "allowed option order is irrelevant");
    }
    private static void DefaultSidInvocation()
    {
        var invocation = InstallerInvocation.Parse(["install", "--source", Source]);
        Check.True(invocation.ClientSid is null, "no invented user identity in argument parser");
    }
    private static void ReadOnlyInvocation()
    {
        foreach (string operation in new[] { "uninstall", "status" })
        {
            var invocation = InstallerInvocation.Parse([operation]);
            Check.Equal(operation, invocation.Operation, "correct operation");
            Check.True(invocation.SourceDirectory is null && invocation.ClientSid is null, "no install inputs attached");
        }
    }
    private static void InvalidOperations()
    {
        foreach (string[] args in new[] { Array.Empty<string>(), new[] { "repair" }, new[] { "Install", "--source", Source }, new[] { "uninstall; Remove-Item" } })
            Check.Throws<ArgumentException>(() => InstallerInvocation.Parse(args), "unsupported operation");
    }
    private static void InvalidOptions()
    {
        foreach (string[] args in new[]
        {
            new[] { "install" }, new[] { "install", "--source" }, new[] { "install", "--source", "--client-sid", UserSid },
            new[] { "install", "--source", Source, "--source", Source },
            new[] { "install", "--source", Source, "--client-sid", UserSid, "--client-sid", UserSid },
            new[] { "install", "--source", Source, "--service-name", "OtherService" },
            new[] { "install", "--source", Source, "--identity", "Administrator" },
            new[] { "install", "--source", Source, "--data-root", @"C:\OtherDatabase" }
        }) Check.Throws<ArgumentException>(() => InstallerInvocation.Parse(args), "unknown or duplicate privileged option");
    }
    private static void InvalidInvocationValues()
    {
        foreach (string source in new[] { ".", @"relative\publish", @"C:publish" })
            Check.Throws<ArgumentException>(() => InstallerInvocation.Parse(["install", "--source", source]), "absolute publish source required");
        foreach (string sid in new[] { "invalid", "S-1-1-0", "S-1-5-18", "S-1-5-21-111-222-333" })
            Check.Throws<ArgumentException>(() => InstallerInvocation.Parse(["install", "--source", Source, "--client-sid", sid]), "intended user must be valid");
    }
    private static void InvalidUninstallOptions()
    {
        foreach (string operation in new[] { "uninstall", "status" })
        foreach (string[] args in new[] { new[] { operation, "--source", Source }, new[] { operation, "--client-sid", UserSid }, new[] { operation, "--delete-database" } })
            Check.Throws<ArgumentException>(() => InstallerInvocation.Parse(args), "database deletion and arbitrary uninstall inputs unavailable");
    }
    private static async Task NewInstall()
    {
        var system = new FakeSystem();
        var result = await new InstallationEngine(system).InstallAsync(Source, UserSid);
        Check.Sequence(new[] { "elevation", "validate", "read", "prepare", "configure:create", "start", "ipc" }, system.Events, "creation waits for both running and IPC");
        Check.True(result.ServiceRunning && result.IpcAvailable && result.DatabasePreserved, "complete success result");
        Check.Equal(InstallDecision.Create, result.Decision, "first operation creates");
        Check.Equal(1, system.CreateCount, "one owned service registration");
        system.AssertDatabasePreserved();
    }
    private static async Task IdempotentInstall()
    {
        var system = new FakeSystem(); var engine = new InstallationEngine(system);
        await engine.InstallAsync(Source, UserSid);
        system.Events.Clear();
        var result = await engine.InstallAsync(Source, UserSid);
        Check.Equal(InstallDecision.Update, result.Decision, "second install selects update");
        Check.Equal(1, system.CreateCount, "repeat install never creates a duplicate");
        Check.Equal(1, system.UpdateCount, "existing configuration is refreshed");
        Check.Equal(0, system.DeleteCount, "valid installation is never deleted");
        Check.True(system.Registration?.Running == true && result.IpcAvailable, "same registration comes back healthy");
        system.AssertDatabasePreserved();
    }
    private static async Task UpdateInstall()
    {
        var system = new FakeSystem { Registration = Registration() };
        var result = await new InstallationEngine(system).InstallAsync(Source, UserSid);
        Check.Sequence(new[] { "elevation", "validate", "read", "owned", "prepare", "disable", "stop", "configure:update", "start", "ipc" }, system.Events, "update stages first then prevents SCM restart races");
        Check.Equal(InstallDecision.Update, result.Decision, "owned service updates");
        Check.Equal(0, system.CreateCount, "no duplicate registration");
        Check.True(system.AppliedConfiguration?.Recovery == RecoveryPolicy.Production, "update reapplies reviewed recovery policy");
        system.AssertDatabasePreserved();
    }
    private static async Task StoppedUpdate()
    {
        var system = new FakeSystem { Registration = Registration(running: false) };
        var result = await new InstallationEngine(system).InstallAsync(Source, UserSid);
        Check.Equal(InstallDecision.Update, result.Decision, "stopped owned service is still an update");
        Check.True(system.Registration?.Running == true, "service is started after reconfiguration");
        Check.True(system.Events.IndexOf("disable") < system.Events.IndexOf("stop"), "recovery is disabled before stop");
        system.AssertDatabasePreserved();
    }
    private static async Task PreparationFailure()
    {
        var original = Registration(); var system = new FakeSystem { Registration = original, FailAt = "prepare" };
        await Check.ThrowsAsync<IOException>(() => new InstallationEngine(system).InstallAsync(Source, UserSid), "unsafe or failed staging");
        Check.Equal(original, system.Registration, "old running service remains unchanged");
        Check.Equal(0, system.Mutations, "environment and package preparation failure cannot stop or change SCM");
        system.AssertDatabasePreserved();
    }
    private static async Task NewConfigureFailure()
    {
        var system = new FakeSystem { ConfigureFailure = true };
        await Check.ThrowsAsync<IOException>(() => new InstallationEngine(system).InstallAsync(Source, UserSid), "partial registration failure");
        Check.Sequence(new[] { "elevation", "validate", "read", "prepare", "configure:create", "read", "owned", "disable", "stop", "delete", "verify" }, system.Events, "owned partial install is rolled back safely");
        Check.True(system.Registration is null, "first failed installation cannot linger in SCM");
        Check.Equal(1, system.VerifyCount, "absence and exact-owned cleanup verified");
        system.AssertDatabasePreserved();
    }
    private static async Task NewStartFailure()
    {
        var system = new FakeSystem { FailAt = "ipc" };
        await Check.ThrowsAsync<IOException>(() => new InstallationEngine(system).InstallAsync(Source, UserSid), "running but unavailable IPC is installation failure");
        Check.True(system.Events.Contains("start") && system.Events.Contains("ipc"), "failure occurs after running verification");
        Check.True(system.Registration is null && system.DeleteCount == 1, "failed first install stops and removes owned service");
        Check.True(system.Events.IndexOf("disable") < system.Events.IndexOf("stop") && system.Events.IndexOf("stop") < system.Events.IndexOf("delete"), "recovery cannot recreate removed collector");
        system.AssertDatabasePreserved();
    }
    private static async Task UpdateStartFailure()
    {
        var system = new FakeSystem { Registration = Registration(), FailAt = "ipc", FailAtTimes = 1 };
        await Check.ThrowsAsync<IOException>(() => new InstallationEngine(system).InstallAsync(Source, UserSid), "failed upgrade startup");
        Check.True(system.Registration is { Running: true }, "previous version is running after successful rollback");
        Check.Equal(PreviousConfiguration.BinaryCommandLine, system.Registration!.BinaryCommandLine, "rollback restores prior executable and intended user");
        Check.Equal(PreviousConfiguration, system.AppliedConfiguration, "rollback uses retained protected configuration");
        Check.Equal(2, system.Events.Count(item => item == "ipc"), "initial failure and restored previous version each verify IPC");
        Check.Equal(0, system.DeleteCount, "existing installation remains available for diagnosis or retry");
        Check.Equal(0, system.VerifyCount, "retained installation cannot claim absent");
        Check.Equal(2, system.Events.Count(item => item == "disable"), "initial stop and failed-start cleanup each disable recovery");
        system.AssertDatabasePreserved();
    }
    private static async Task UpdateConfigureFailure()
    {
        var system = new FakeSystem { Registration = Registration(), ConfigureFailure = true };
        await Check.ThrowsAsync<IOException>(() => new InstallationEngine(system).InstallAsync(Source, UserSid), "failed configuration update");
        Check.True(system.Registration is { Running: true }, "configuration failure restores prior running service");
        Check.Equal(PreviousConfiguration.BinaryCommandLine, system.Registration!.BinaryCommandLine, "previous protected version is reinstated");
        Check.Equal(0, system.CreateCount, "no competing registration created");
        Check.Equal(0, system.DeleteCount, "previous installation is not removed by failed update");
        system.AssertDatabasePreserved();
    }
    private static async Task UpdateRollbackFailure()
    {
        var system = new FakeSystem { Registration = Registration(), FailAt = "ipc" };
        try
        {
            await new InstallationEngine(system).InstallAsync(Source, UserSid);
            throw new InvalidOperationException("Expected aggregate failure.");
        }
        catch (AggregateException exception)
        {
            Check.Equal(2, exception.InnerExceptions.Count, "original upgrade and previous-version recovery errors retained");
            Check.True(exception.InnerExceptions.All(item => item is IOException), "both IPC errors remain inspectable");
        }
        Check.True(system.Registration is { Running: false }, "failed rollback cannot leave an unverified collector running");
        Check.Equal(0, system.DeleteCount, "existing protected service is retained for diagnosis and retry");
        Check.Equal(PreviousConfiguration.BinaryCommandLine, system.Registration!.BinaryCommandLine, "retained SCM entry points back to previous version");
        system.AssertDatabasePreserved();
    }
    private static async Task ForeignPartialRollback()
    {
        var system = new FakeSystem { ConfigureForeign = true, ConfigureFailure = true };
        try
        {
            await new InstallationEngine(system).InstallAsync(Source, UserSid);
            throw new InvalidOperationException("Expected aggregate failure.");
        }
        catch (AggregateException exception)
        {
            Check.Equal(2, exception.InnerExceptions.Count, "original configure and ownership cleanup errors are retained");
            Check.True(exception.InnerExceptions[0] is IOException && exception.Flatten().InnerExceptions.Any(item => item is InvalidOperationException), "ownership failure is explicit");
        }
        Check.True(system.Registration is not null, "foreign registration remains untouched");
        Check.True(!system.Events.Contains("disable") && !system.Events.Contains("stop") && !system.Events.Contains("delete"), "rollback may not stop an unowned registration");
        system.AssertDatabasePreserved();
    }
    private static async Task CleanupFailure()
    {
        var system = new FakeSystem { ConfigureFailure = true, FailAt = "stop" };
        try
        {
            await new InstallationEngine(system).InstallAsync(Source, UserSid);
            throw new InvalidOperationException("Expected aggregate failure.");
        }
        catch (AggregateException exception)
        {
            Check.Equal(2, exception.InnerExceptions.Count, "both failure causes reported");
            Check.True(exception.Message.Contains("database was retained", StringComparison.Ordinal), "failure explicitly reports preservation");
        }
        Check.Equal(0, system.DeleteCount, "cannot delete before a verified stop");
        system.AssertDatabasePreserved();
    }
    private static async Task NoPartialRegistration()
    {
        var system = new FakeSystem { FailAt = "configure:create" };
        await Check.ThrowsAsync<IOException>(() => new InstallationEngine(system).InstallAsync(Source, UserSid), "configure rejected before registration exists");
        Check.True(system.Registration is null, "no registration was created");
        Check.Equal(0, system.DeleteCount, "nothing owned to delete");
        Check.True(!system.Events.Contains("stop"), "no service stop attempted");
        system.AssertDatabasePreserved();
    }
    private static async Task ForeignInstall()
    {
        var original = Registration() with { Description = "Foreign installer" }; var system = new FakeSystem { Registration = original };
        await Check.ThrowsAsync<InvalidOperationException>(() => new InstallationEngine(system).InstallAsync(Source, UserSid), "foreign production-name collision");
        Check.Sequence(new[] { "elevation", "validate", "read", "owned" }, system.Events, "foreign refusal precedes files and SCM mutation");
        Check.Equal(original, system.Registration, "foreign running service is untouched");
        Check.Equal(0, system.Mutations, "no mutation on rejected ownership");
        system.AssertDatabasePreserved();
    }
    private static async Task UninstallRunning()
    {
        var system = new FakeSystem { Registration = Registration() };
        var result = await new InstallationEngine(system).UninstallAsync();
        Check.Sequence(new[] { "elevation", "validate", "read", "owned", "disable", "stop", "delete", "verify" }, system.Events, "disable-stop-delete-confirm ordering");
        Check.True(!result.ServiceRunning && !result.IpcAvailable && result.DatabasePreserved, "uninstall does not claim running IPC");
        Check.True(system.Registration is null, "owned SCM entry removed");
        Check.Equal(1, system.VerifyCount, "absence and exact-owned resource cleanup performed");
        system.AssertDatabasePreserved();
    }
    private static async Task UninstallStopped()
    {
        var system = new FakeSystem { Registration = Registration(running: false) };
        await new InstallationEngine(system).UninstallAsync();
        Check.True(system.Registration is null && system.DeleteCount == 1 && system.VerifyCount == 1, "stopped service cleanly removed");
        system.AssertDatabasePreserved();
    }
    private static async Task UninstallAbsent()
    {
        var system = new FakeSystem(); var engine = new InstallationEngine(system);
        var first = await engine.UninstallAsync(); var second = await engine.UninstallAsync();
        Check.Equal(first, second, "absent uninstall is stable and repeatable");
        Check.Equal(0, system.DeleteCount, "no invented SCM registration deletion");
        Check.Equal(2, system.VerifyCount, "each call confirms absence and checks exact stale owned resources");
        Check.True(!system.Events.Contains("stop") && !system.Events.Contains("disable"), "absent SCM entry is not controlled");
        system.AssertDatabasePreserved();
    }
    private static async Task UninstallForeign()
    {
        var original = Registration() with { Description = "Foreign installer" }; var system = new FakeSystem { Registration = original };
        await Check.ThrowsAsync<InvalidOperationException>(() => new InstallationEngine(system).UninstallAsync(), "unowned service cannot be uninstalled");
        Check.Equal(original, system.Registration, "foreign service remains running");
        Check.Equal(0, system.Mutations, "foreign uninstall does no SCM or cleanup mutation");
        system.AssertDatabasePreserved();
    }
    private static async Task UninstallStopFailure()
    {
        var system = new FakeSystem { Registration = Registration(), FailAt = "stop" };
        await Check.ThrowsAsync<IOException>(() => new InstallationEngine(system).UninstallAsync(), "stubborn service stop");
        Check.Equal(0, system.DeleteCount, "registration retained when service stop cannot be verified");
        Check.Equal(0, system.VerifyCount, "failure cannot claim SCM absent");
        system.AssertDatabasePreserved();
    }
    private static async Task UninstallDeleteFailure()
    {
        var system = new FakeSystem { Registration = Registration(), FailAt = "delete" };
        await Check.ThrowsAsync<IOException>(() => new InstallationEngine(system).UninstallAsync(), "SCM delete failure");
        Check.True(system.Registration is { Running: false }, "stopped registration remains for diagnosis");
        Check.Equal(0, system.VerifyCount, "failed removal is never reported as absent");
        system.AssertDatabasePreserved();
    }
    private static async Task UninstallVerificationFailure()
    {
        var system = new FakeSystem { Registration = Registration(), FailAt = "verify" };
        await Check.ThrowsAsync<IOException>(() => new InstallationEngine(system).UninstallAsync(), "exact owned session or removal check failed");
        Check.Equal(1, system.DeleteCount, "SCM delete was attempted before final cleanup check");
        system.AssertDatabasePreserved();
    }
    private static async Task NonElevatedInstall()
    {
        var system = new FakeSystem { Elevated = false, Registration = Registration() };
        await Check.ThrowsAsync<UnauthorizedAccessException>(() => new InstallationEngine(system).InstallAsync(Source, UserSid), "explicit elevation required");
        Check.Sequence(new[] { "elevation" }, system.Events, "no file, SCM, or SID work precedes elevation rejection");
        Check.Equal(0, system.Mutations, "no mutation on normal-token invocation");
        system.AssertDatabasePreserved();
    }
    private static async Task NonElevatedUninstall()
    {
        var system = new FakeSystem { Elevated = false, Registration = Registration() };
        await Check.ThrowsAsync<UnauthorizedAccessException>(() => new InstallationEngine(system).UninstallAsync(), "normal-token uninstall rejected");
        Check.Sequence(new[] { "elevation" }, system.Events, "no SCM read or cleanup before elevation check");
        Check.Equal(0, system.Mutations, "no privileged action on failed elevation");
        system.AssertDatabasePreserved();
    }
    private static async Task InvalidInstallEnvironment()
    {
        var system = new FakeSystem { EnvironmentValid = false, Registration = Registration() };
        await Check.ThrowsAsync<InvalidOperationException>(() => new InstallationEngine(system).InstallAsync(Source, UserSid), "unsafe ProgramData or source rejected");
        Check.Sequence(new[] { "elevation", "validate" }, system.Events, "environment checks precede service reads and staging");
        Check.Equal(0, system.Mutations, "environment failure makes no privileged changes");
        system.AssertDatabasePreserved();
    }
    private static async Task InvalidUninstallEnvironment()
    {
        var system = new FakeSystem { EnvironmentValid = false, Registration = Registration() };
        await Check.ThrowsAsync<InvalidOperationException>(() => new InstallationEngine(system).UninstallAsync(), "unsafe uninstall roots rejected");
        Check.Sequence(new[] { "elevation", "validate" }, system.Events, "root validation precedes SCM controls and trace cleanup");
        Check.Equal(0, system.Mutations, "invalid uninstall environment unchanged");
        system.AssertDatabasePreserved();
    }
    private static async Task InvalidInstallSid()
    {
        var system = new FakeSystem { Registration = Registration() };
        await Check.ThrowsAsync<ArgumentException>(() => new InstallationEngine(system).InstallAsync(Source, "S-1-5-18"), "SYSTEM cannot be intended normal user");
        Check.Sequence(new[] { "elevation" }, system.Events, "user validation before reading or mutation");
        Check.Equal(0, system.Mutations, "invalid user cannot change service");
        system.AssertDatabasePreserved();
    }
    private static async Task CanceledInstall()
    {
        var system = new FakeSystem { Registration = Registration() }; using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Check.ThrowsAsync<OperationCanceledException>(() => new InstallationEngine(system).InstallAsync(Source, UserSid, cancellation.Token), "pre-canceled install stops before staging");
        Check.True(system.Events.Count == 0 && system.Mutations == 0, "pre-canceled operation performs no platform actions");
        system.AssertDatabasePreserved();
    }
    private static async Task CanceledUninstall()
    {
        var system = new FakeSystem { Registration = Registration() }; using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Check.ThrowsAsync<OperationCanceledException>(() => new InstallationEngine(system).UninstallAsync(cancellation.Token), "pre-canceled uninstall rejected");
        Check.True(system.Events.Count == 0 && system.Mutations == 0, "cancel precedes all platform operations");
        system.AssertDatabasePreserved();
    }
    private static async Task InvalidCli()
    {
        var (exitCode, output, error) = await CaptureCli(["delete-database"]);
        Check.Equal(2, exitCode, "invalid invocation code");
        Check.True(error.Contains("Use install", StringComparison.Ordinal) && output.Length == 0, "clear usage error without success output");
    }
    private static async Task NonElevatedInstallCli()
    {
        var (exitCode, output, error) = await CaptureCli(["install", "--source", Source, "--client-sid", UserSid]);
        Check.Equal(2, exitCode, "ordinary-token install code");
        Check.True(error.Contains("elevated Administrator terminal", StringComparison.Ordinal) && error.Contains("NetTrace itself stays unelevated", StringComparison.Ordinal), "elevation requirement is explicit");
        Check.Equal("", output, "failed install never reports running IPC or success JSON");
    }
    private static async Task NonElevatedUninstallCli()
    {
        var (exitCode, output, error) = await CaptureCli(["uninstall"]);
        Check.Equal(2, exitCode, "ordinary-token uninstall code");
        Check.True(error.Contains("elevated Administrator terminal", StringComparison.Ordinal), "failed uninstall identifies required caller token");
        Check.Equal("", output, "failed uninstall does not report successful removal");
    }
    private static async Task HelpCli()
    {
        var (exitCode, output, error) = await CaptureCli(["--help"]);
        Check.Equal(0, exitCode, "help does not require elevation");
        Check.Equal("", error, "help has no failure diagnostic");
        Check.True(output.Contains("installer never triggers UAC", StringComparison.Ordinal), "explicit UAC boundary");
        Check.True(output.Contains("Uninstall retains app-usage.db", StringComparison.Ordinal), "database preservation is visible before operation");
        Check.True(output.Contains("LocalSystem; Automatic (Delayed Start)", StringComparison.Ordinal), "production identity and startup documented");
    }
    private static async Task<(int ExitCode, string Output, string Error)> CaptureCli(string[] arguments)
    {
        var originalOutput = Console.Out; var originalError = Console.Error;
        using var output = new StringWriter(); using var error = new StringWriter();
        try
        {
            Console.SetOut(output); Console.SetError(error);
            int exitCode = await NetTrace.AppUsageInstaller.Program.Main(arguments);
            return (exitCode, output.ToString(), error.ToString());
        }
        finally { Console.SetOut(originalOutput); Console.SetError(originalError); }
    }

    private static ServiceRegistration Registration(bool running = true) => new(PreviousConfiguration.ServiceName,
        PreviousConfiguration.BinaryCommandLine, PreviousConfiguration.Identity, InstallationConfiguration.OwnershipDescription, running, running ? 1234U : 0U);

    private sealed class FakeSystem : IServiceInstallationSystem
    {
        private static readonly byte[] SavedHistory = [0x53, 0x51, 0x4C, 0x69, 0x74, 0x65, 0x03, 0x01, 0x11];
        private readonly byte[] database = SavedHistory.ToArray();
        private int injectedFailures;
        private int configureAttempts;
        private readonly Dictionary<string, InstallationConfiguration> configurations = new(StringComparer.Ordinal)
        {
            [PreviousConfiguration.BinaryCommandLine] = PreviousConfiguration,
            [Configuration.BinaryCommandLine] = Configuration,
        };
        public List<string> Events { get; } = [];
        public ServiceRegistration? Registration { get; set; }
        public bool Elevated { get; init; } = true;
        public bool EnvironmentValid { get; init; } = true;
        public bool ConfigureFailure { get; init; }
        public bool ConfigureForeign { get; init; }
        public string? FailAt { get; init; }
        public int FailAtTimes { get; init; } = int.MaxValue;
        public int ConfigureFailureCount { get; init; } = 1;
        public int CreateCount { get; private set; }
        public int UpdateCount { get; private set; }
        public int DeleteCount { get; private set; }
        public int VerifyCount { get; private set; }
        public int Mutations { get; private set; }
        public InstallationConfiguration? AppliedConfiguration { get; private set; }

        public void RequireElevation()
        {
            Record("elevation");
            if (!Elevated) throw new UnauthorizedAccessException("Run the production installer from an elevated terminal.");
        }
        public void ValidateEnvironment(string? sourceDirectory, string? clientSid)
        {
            Record("validate");
            if (!EnvironmentValid) throw new InvalidOperationException("Unsafe installation environment.");
            if (sourceDirectory is not null && !Path.IsPathFullyQualified(sourceDirectory)) throw new ArgumentException("Source must be absolute.");
            if (clientSid is not null) InstallationConfiguration.ValidateClientSid(clientSid);
        }
        public ServiceRegistration? ReadService() { Record("read"); return Registration; }
        public InstallationConfiguration ValidateOwnedRegistration(ServiceRegistration registration)
        {
            Record("owned");
            if (registration.ServiceName != InstallationConfiguration.ProductionServiceName
                || registration.Identity != "LocalSystem" || registration.Description != InstallationConfiguration.OwnershipDescription)
                throw new InvalidOperationException("Registration is foreign.");
            if (!configurations.TryGetValue(registration.BinaryCommandLine, out var configuration))
                throw new InvalidOperationException("Registration does not match a protected version.");
            return configuration;
        }
        public InstallationConfiguration PrepareFiles(string sourceDirectory, string clientSid)
        {
            Record("prepare");
            Check.Equal(Source, sourceDirectory, "expected fake source");
            return InstallationConfiguration.Create(@"C:\Program Files", @"C:\ProgramData", clientSid, Version);
        }
        public void Configure(InstallationConfiguration configuration, bool create)
        {
            Record(create ? "configure:create" : "configure:update");
            Mutations++;
            if (create)
            {
                if (Registration is not null) throw new InvalidOperationException("Duplicate service creation.");
                CreateCount++;
            }
            else
            {
                if (Registration is null) throw new InvalidOperationException("Cannot update absent registration.");
                UpdateCount++;
            }
            AppliedConfiguration = configuration;
            configurations[configuration.BinaryCommandLine] = configuration;
            Registration = new(configuration.ServiceName, configuration.BinaryCommandLine, configuration.Identity,
                ConfigureForeign ? "Foreign installer" : InstallationConfiguration.OwnershipDescription, false);
            if (ConfigureFailure && ++configureAttempts <= ConfigureFailureCount) throw new IOException("Configuration failure after a partial SCM registration.");
        }
        public void DisableRecovery() { Record("disable"); Mutations++; }
        public void Stop()
        {
            Record("stop"); Mutations++;
            if (Registration is not null) Registration = Registration with { Running = false, ProcessId = 0 };
        }
        public void Delete() { Record("delete"); Mutations++; DeleteCount++; Registration = null; }
        public void VerifyAbsentAndCleanOwnedResources()
        {
            Record("verify"); Mutations++;
            if (Registration is not null) throw new InvalidOperationException("SCM is not absent.");
            VerifyCount++;
        }
        public Task StartAndVerifyAsync(InstallationConfiguration configuration, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Record("start"); Mutations++;
            if (Registration is null) throw new InvalidOperationException("Cannot start absent service.");
            Registration = Registration with { Running = true, ProcessId = 2345 };
            Record("ipc");
            return Task.CompletedTask;
        }
        private void Record(string action)
        {
            Events.Add(action);
            if (FailAt == action && ++injectedFailures <= FailAtTimes) throw new IOException($"Injected failure at {action}.");
        }
        public void AssertDatabasePreserved() => Check.Sequence(SavedHistory, database, "stored history remains byte-for-byte intact in success and failure paths");
    }
}
