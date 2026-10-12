namespace AppUsageStorage;

// This identity never depends on a PID, process lifetime, or the file still existing.
public sealed record ApplicationIdentity
{
    private ApplicationIdentity(string key, string displayName, string? executableName, string? executablePath)
    {
        Key = key;
        DisplayName = displayName;
        ExecutableName = executableName;
        ExecutablePath = executablePath;
    }

    public string Key { get; }
    public string DisplayName { get; }
    public string? ExecutableName { get; }
    public string? ExecutablePath { get; }

    public static ApplicationIdentity System { get; } = new("system", "System", null, null);
    public static ApplicationIdentity Unknown { get; } = new("unknown", "Unknown / Unattributed", null, null);

    public static ApplicationIdentity FromExecutablePath(string executablePath, string? displayName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        var path = executablePath.Replace('/', '\\');
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            path = @"\\" + path[8..];
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
            path = path[4..];

        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\.\", StringComparison.Ordinal)
            || path.IndexOfAny(['\0', '"', '<', '>', '|', '*', '?']) >= 0
            || path.Any(char.IsControl))
            throw new ArgumentException("An application identity requires an absolute Windows executable path.", nameof(executablePath));

        path = Path.GetFullPath(path);
        var name = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(name) || name.Contains(':'))
            throw new ArgumentException("An application identity requires a path ending in an executable filename.", nameof(executablePath));
        if (displayName is not null && string.IsNullOrWhiteSpace(displayName))
            throw new ArgumentException("The application display name cannot be blank.", nameof(displayName));

        return new("path:" + path.ToUpperInvariant(), displayName ?? Path.GetFileNameWithoutExtension(name), name, path);
    }
}
