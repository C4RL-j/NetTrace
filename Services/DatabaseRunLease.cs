using System.IO;

namespace NetTrace.Services;

// Windows sharing locks belong to the open handle, not a thread or a persisted PID.
// Killing the owner releases the handle; a leftover file is safe to open again.
public sealed class DatabaseRunLease : IDisposable
{
    private readonly object _gate = new();
    private FileStream? _handle;

    public string DatabasePath { get; }
    public bool IsHeld { get { lock (_gate) return _handle is not null; } }

    public DatabaseRunLease(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        try
        {
            // Share.None also rejects duplicates in this process and other Windows sessions.
            // Never delete the sidecar: deleting/recreating locks would introduce ownership races.
            _handle = new FileStream(DatabasePath + ".nettrace.lock", FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException exception) when ((exception.HResult & 0xffff) is 32 or 33)
        {
            throw new InvalidOperationException(
                "NetTrace is already monitoring this history database. Close the other instance before starting another.", exception);
        }
    }

    public void EnsureOwns(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_handle is null, this);
            if (!string.Equals(DatabasePath, Path.GetFullPath(databasePath), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The run lease belongs to a different history database.");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _handle?.Dispose();
            _handle = null;
        }
    }
}
