using System.Security.Cryptography;
using System.Text;

namespace AppUsageIpc;

// Production installation directories have always used the complete kit's
// content identity. AssemblyVersion alone cannot distinguish these collectors.
// Keep this algorithm identical for the installer and unelevated status reader.
public static class CollectorPackageVersion
{
    public static string Compute(IReadOnlyDictionary<string, string> files)
    {
        string inventory = string.Join('\n', files.OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => $"{entry.Key}|{entry.Value}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(inventory))).ToLowerInvariant();
    }

    public static bool IsVersion(string? version)
        => version is { Length: 64 } && version.All(Uri.IsHexDigit);
}
