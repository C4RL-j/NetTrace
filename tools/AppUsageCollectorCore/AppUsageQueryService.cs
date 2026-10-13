using AppUsageEtwProbe;
using AppUsageIpc;
using AppUsageStorage;
using ApplicationIdentity = AppUsageStorage.ApplicationIdentity;

namespace AppUsageCollectorCore;

// Only approved local calendar ranges enter the repository. Queries never
// inspect processes/files, subtract VPN usage, or alter checkpoint accounting.
public sealed class AppUsageQueryService
{
    public static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(2);
    private const string UnavailableDiagnostic = "Usage history temporarily unavailable.";
    private const string DegradedDiagnostic = "App tracking unavailable; persisted usage history remains available.";
    private readonly Func<DateOnly, DateOnly, CancellationToken, IReadOnlyList<DailyApplicationUsage>> readRange;
    private readonly Func<UsagePersistenceHealth>? storageHealth;
    private readonly TimeProvider timeProvider;
    private readonly TimeZoneInfo localTimeZone;
    private readonly SemaphoreSlim queryWorkers = new(NativePipeSecurity.MaximumClients,
        NativePipeSecurity.MaximumClients);

    public AppUsageQueryService(Func<DateOnly, DateOnly, IReadOnlyList<DailyApplicationUsage>> readRange,
        Func<UsagePersistenceHealth>? storageHealth = null, TimeProvider? timeProvider = null,
        TimeZoneInfo? localTimeZone = null)
        : this(AdaptReader(readRange), storageHealth, timeProvider, localTimeZone)
    {
    }

    public AppUsageQueryService(Func<DateOnly, DateOnly, CancellationToken, IReadOnlyList<DailyApplicationUsage>> readRange,
        Func<UsagePersistenceHealth>? storageHealth = null, TimeProvider? timeProvider = null,
        TimeZoneInfo? localTimeZone = null)
    {
        ArgumentNullException.ThrowIfNull(readRange);
        this.readRange = readRange;
        this.storageHealth = storageHealth;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.localTimeZone = localTimeZone ?? TimeZoneInfo.Local;
    }

    public static AppUsageQueryService ForDatabase(string? commonDataRoot = null,
        Func<UsagePersistenceHealth>? storageHealth = null, TimeProvider? timeProvider = null,
        TimeZoneInfo? localTimeZone = null)
    {
        var store = DailyAppUsageStore.OpenReadOnly(commonDataRoot);
        return new((start, end, token) => store.GetUsageForRange(start, end, token),
            storageHealth, timeProvider, localTimeZone);
    }

    public async Task<AppUsageQueryResult> QueryAsync(AppUsageRange range,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(range)) throw new ArgumentOutOfRangeException(nameof(range));
        cancellationToken.ThrowIfCancellationRequested();
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), localTimeZone).DateTime);
        var start = AppUsageRangeRules.GetStartLocalDate(range, today);
        using var timeout = new CancellationTokenSource(QueryTimeout);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var queryToken = bounded.Token;
        try
        {
            // SQLite's synchronous read has its own one-second lock timeout.
            // Admission remains owned until actual work finishes, even when a
            // caller stops waiting. Repeated timeouts cannot pile up readers.
            await queryWorkers.WaitAsync(queryToken).ConfigureAwait(false);
            Task<AppUsageQueryResult> work;
            try
            {
                // Do not pass a scheduling token: a task cancelled before its
                // callback ran would strand the admitted worker's permit.
                work = Task.Run(() =>
                {
                    try { return Read(range, start, today, queryToken); }
                    finally { queryWorkers.Release(); }
                });
            }
            catch
            {
                queryWorkers.Release();
                throw;
            }
            return await work.WaitAsync(queryToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // Storage paths, exception details and persistence error text stay
            // inside the privileged process instead of reaching pipe clients.
            return new(range, start, today, 0, 0, 0, 0, Array.Empty<AppUsageApplication>(),
                AppUsageAvailability.Unavailable, UnavailableDiagnostic);
        }
    }

    private static Func<DateOnly, DateOnly, CancellationToken, IReadOnlyList<DailyApplicationUsage>> AdaptReader(
        Func<DateOnly, DateOnly, IReadOnlyList<DailyApplicationUsage>> reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return (start, end, _) => reader(start, end);
    }

    private AppUsageQueryResult Read(AppUsageRange range, DateOnly start, DateOnly end, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var aggregate = new Dictionary<string, Aggregate>(StringComparer.Ordinal);
        foreach (var row in readRange(start, end, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(row.ApplicationIdentity) || string.IsNullOrWhiteSpace(row.DisplayName))
                throw new InvalidOperationException("Stored application metadata is invalid.");
            if (row.DownloadBytes < 0 || row.UploadBytes < 0)
                throw new InvalidOperationException("Stored application byte counts cannot be negative.");
            if (row.LocalDate < start || row.LocalDate > end)
                throw new InvalidOperationException("Stored application usage is outside the requested range.");
            if (!aggregate.TryGetValue(row.ApplicationIdentity, out var application))
                aggregate[row.ApplicationIdentity] = application = new(row);
            application.Add(row);
        }

        var applications = aggregate.Values
            .Select(application =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return application.ToResult();
            })
            .Where(application => application.TotalBytes > 0)
            .OrderByDescending(application => application.TotalBytes)
            .ThenBy(application => application.ApplicationIdentity, StringComparer.Ordinal)
            .ToList().AsReadOnly();
        long download = 0, upload = 0;
        foreach (var application in applications)
        {
            cancellationToken.ThrowIfCancellationRequested();
            download = ByteTotals.SaturatingAdd(download, application.DownloadBytes);
            upload = ByteTotals.SaturatingAdd(upload, application.UploadBytes);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var health = storageHealth?.Invoke();
        var degraded = health is not null && (!health.PersistenceAvailable || health.PersistenceDegraded);
        return new(range, start, end, applications.Count, download, upload,
            ByteTotals.SaturatingAdd(download, upload), applications,
            degraded ? AppUsageAvailability.Degraded : AppUsageAvailability.Available,
            degraded ? DegradedDiagnostic : null);
    }

    private sealed class Aggregate(DailyApplicationUsage metadata)
    {
        private DailyApplicationUsage latest = metadata;
        private long download;
        private long upload;

        internal void Add(DailyApplicationUsage row)
        {
            download = ByteTotals.SaturatingAdd(download, row.DownloadBytes);
            upload = ByteTotals.SaturatingAdd(upload, row.UploadBytes);
            if (row.LastSeen > latest.LastSeen
                || (row.LastSeen == latest.LastSeen && row.LocalDate > latest.LocalDate)) latest = row;
        }

        internal AppUsageApplication ToResult() => new(latest.ApplicationIdentity, latest.DisplayName,
            latest.ExecutableName, latest.ExecutablePath, download, upload,
            ByteTotals.SaturatingAdd(download, upload),
            StringComparer.Ordinal.Equals(latest.ApplicationIdentity, ApplicationIdentity.System.Key),
            StringComparer.Ordinal.Equals(latest.ApplicationIdentity, ApplicationIdentity.Unknown.Key));
    }
}
