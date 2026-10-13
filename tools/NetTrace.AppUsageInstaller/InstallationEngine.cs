namespace NetTrace.AppUsageInstaller;

// Mutations are behind a narrow seam so install/update and failure ordering can be
// verified without elevation or touching SCM, ETW or the production database.
public interface IServiceInstallationSystem
{
    void RequireElevation();
    void ValidateEnvironment(string? sourceDirectory, string? clientSid);
    ServiceRegistration? ReadService();
    InstallationConfiguration ValidateOwnedRegistration(ServiceRegistration registration);
    InstallationConfiguration PrepareFiles(string sourceDirectory, string clientSid);
    void Configure(InstallationConfiguration configuration, bool create);
    void DisableRecovery();
    void Stop();
    void Delete();
    void VerifyAbsentAndCleanOwnedResources();
    Task StartAndVerifyAsync(InstallationConfiguration configuration, CancellationToken cancellationToken);
}

public sealed class InstallationEngine(IServiceInstallationSystem system)
{
    public async Task<InstallationResult> InstallAsync(string sourceDirectory, string clientSid,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        system.RequireElevation();
        InstallationConfiguration.ValidateClientSid(clientSid);
        system.ValidateEnvironment(sourceDirectory, clientSid);
        var existing = system.ReadService();
        var previous = existing is not null ? system.ValidateOwnedRegistration(existing) : null;
        var decision = InstallationDecision.ForInstall(existing, owned: existing is not null);
        var configuration = system.PrepareFiles(sourceDirectory, clientSid);
        bool configurationAttempted = false;
        try
        {
            configurationAttempted = true;
            if (existing is not null) { system.DisableRecovery(); system.Stop(); }
            system.Configure(configuration, create: decision == InstallDecision.Create);
            await system.StartAndVerifyAsync(configuration, cancellationToken).ConfigureAwait(false);
            return new("install", configuration.ServiceName, true, true, true, decision);
        }
        catch (Exception original)
        {
            if (configurationAttempted)
            {
                try
                {
                    // A failed first install removes only its owned SCM entry.
                    // An update restores the retained protected previous version.
                    var partial = system.ReadService();
                    if (partial is not null)
                    {
                        system.ValidateOwnedRegistration(partial);
                        system.DisableRecovery(); system.Stop();
                        if (decision == InstallDecision.Create)
                        { system.Delete(); system.VerifyAbsentAndCleanOwnedResources(); }
                        else
                        {
                            system.Configure(previous!, create: false);
                            await system.StartAndVerifyAsync(previous!, CancellationToken.None).ConfigureAwait(false);
                        }
                    }
                }
                catch (Exception cleanup)
                {
                    try
                    {
                        var remaining = system.ReadService();
                        if (remaining is not null)
                        { system.ValidateOwnedRegistration(remaining); system.DisableRecovery(); system.Stop(); }
                    }
                    catch (Exception stopping) { cleanup = new AggregateException(cleanup, stopping); }
                    throw new AggregateException("Installation failed; cleanup or previous-version rollback also failed. The database was retained.", original, cleanup);
                }
            }
            throw;
        }
    }

    public Task<InstallationResult> UninstallAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        system.RequireElevation();
        system.ValidateEnvironment(null, null);
        var existing = system.ReadService();
        if (existing is not null)
        {
            system.ValidateOwnedRegistration(existing);
            system.DisableRecovery(); system.Stop(); system.Delete();
        }
        system.VerifyAbsentAndCleanOwnedResources();
        return Task.FromResult(new InstallationResult("uninstall", InstallationConfiguration.ProductionServiceName,
            false, false, true));
    }
}
