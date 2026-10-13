using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;

namespace AppUsageCollectorCore;

// This lease coordinates the service's complete SQLite lifetime with an offline
// history clear. It never changes the protected ETW mutex or stops capture.
public sealed class AppUsageHistoryLease : IDisposable
{
    public const string FileName = "app-usage.history.lock";
    private readonly FileStream stream;
    private AppUsageHistoryLease(FileStream stream) => this.stream = stream;

    public static string GetPath(string commonDataRoot)
    {
        if (!Path.IsPathFullyQualified(commonDataRoot))
            throw new ArgumentException("The application usage data root must be absolute.", nameof(commonDataRoot));
        return Path.Combine(Path.GetFullPath(commonDataRoot), "NetTrace", FileName);
    }

    public static async Task<AppUsageHistoryLease> AcquireAsync(string commonDataRoot,
        TimeSpan timeout, CancellationToken cancellationToken = default, bool requireProtectedDirectory = false)
    {
        if (timeout < TimeSpan.Zero || timeout > TimeSpan.FromSeconds(15))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        cancellationToken.ThrowIfCancellationRequested();
        string path = GetPath(commonDataRoot);
        VerifyNoReparse(path);
        if (requireProtectedDirectory) VerifyProtected(Path.GetDirectoryName(path)!);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        VerifyNoReparse(path);
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                VerifyNoReparse(path);
                if (requireProtectedDirectory && File.Exists(path)) VerifyProtected(path);
                // The protected NetTrace directory supplies SYSTEM/Administrators
                // inheritance in production. Never truncate or delete this file.
                var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    FileShare.None, 1, FileOptions.None);
                try
                {
                    VerifyNoReparse(path);
                    if (requireProtectedDirectory) VerifyProtected(path);
                    return new(file);
                }
                catch { file.Dispose(); throw; }
            }
            catch (IOException exception) when ((exception.HResult & 0xffff) is 32 or 33)
            {
                if (elapsed.Elapsed >= timeout)
                    throw new InvalidOperationException("Application usage storage is busy; retry when the collector has finished starting or stopping.", exception);
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static void VerifyNoReparse(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("Application usage history paths cannot contain reparse points.");
    }

    private static void VerifyProtected(string path)
    {
        VerifyNoReparse(path);
        if (!Directory.Exists(path) && !File.Exists(path))
            throw new UnauthorizedAccessException("The production App Usage data directory must first be created by its installer.");
        FileSystemSecurity security = Directory.Exists(path) ? new DirectoryInfo(path).GetAccessControl()
            : new FileInfo(path).GetAccessControl();
        string[] trusted = ["S-1-5-18", "S-1-5-32-544", "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"];
        string owner = (security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier)?.Value
            ?? throw new UnauthorizedAccessException("The production history lease owner is unavailable.");
        if (!trusted.Contains(owner, StringComparer.Ordinal))
            throw new UnauthorizedAccessException("The production history lease must be owned by SYSTEM or administrators.");
        const FileSystemRights write = FileSystemRights.WriteData | FileSystemRights.AppendData
            | FileSystemRights.WriteAttributes | FileSystemRights.WriteExtendedAttributes | FileSystemRights.Delete
            | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & write) != 0
                && !trusted.Contains(((SecurityIdentifier)rule.IdentityReference).Value, StringComparer.Ordinal))
                throw new UnauthorizedAccessException("The production history lease path is writable by an untrusted principal.");
    }

    public void Dispose() => stream.Dispose();
}
