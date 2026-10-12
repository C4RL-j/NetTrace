namespace AppUsageStorage;

public enum AppUsageStorageFailure
{
    OpenFailed,
    IncompatibleSchema,
    CorruptDatabase,
    TransactionFailed,
    QueryFailed
}

public sealed class AppUsageStorageException : InvalidOperationException
{
    public AppUsageStorageException(AppUsageStorageFailure failure, string databasePath, string message,
        Exception? innerException = null)
        : base($"App Usage storage {failure} at '{databasePath}': {message}", innerException)
    {
        Failure = failure;
        DatabasePath = databasePath;
    }

    public AppUsageStorageFailure Failure { get; }
    public string DatabasePath { get; }
}
