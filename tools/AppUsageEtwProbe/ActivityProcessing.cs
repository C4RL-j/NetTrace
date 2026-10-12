using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace AppUsageEtwProbe;

public static class ActivityParser
{
    public static bool TryParse(RawNetworkEvent raw, ProviderSchema schema,
        IReadOnlySet<IPAddress> localAddresses, out NetworkActivity? activity)
    {
        activity = null;
        var descriptor = schema.Events.FirstOrDefault(x => x.EventId == raw.EventId && x.Version == raw.Version);
        if (descriptor is null || !TryUnsigned(raw.Payload, descriptor.PidField, out var pid) || pid > int.MaxValue ||
            !TryUnsigned(raw.Payload, descriptor.SizeField, out var bytes)) return false;
        var source = Address(raw.Payload, descriptor.SourceAddressField);
        var destination = Address(raw.Payload, descriptor.DestinationAddressField);
        if ((source is not null && !MatchesFamily(source, descriptor.Family)) ||
            (destination is not null && !MatchesFamily(destination, descriptor.Family))) return false;
        int? interfaceIndex = null;
        if (descriptor.InterfaceIndexField is not null &&
            TryUnsigned(raw.Payload, descriptor.InterfaceIndexField, out var index) && index <= int.MaxValue)
            interfaceIndex = (int)index;
        activity = new((int)pid, raw.TimestampUtc, bytes, descriptor.Direction, descriptor.Family,
            Classify(source, destination, localAddresses), source, destination, interfaceIndex);
        return true;
    }

    private static bool MatchesFamily(IPAddress address, IpFamily family) =>
        address.AddressFamily == (family == IpFamily.Ipv4 ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6);

    private static IPAddress? Address(IReadOnlyDictionary<string, object?> payload, string field)
    {
        if (!payload.TryGetValue(field, out var value)) return null;
        return value switch
        {
            IPAddress address => address,
            byte[] bytes when bytes.Length is 4 or 16 => new IPAddress(bytes),
            string text when IPAddress.TryParse(text, out var parsed) => parsed,
            _ => null
        };
    }

    private static bool TryUnsigned(IReadOnlyDictionary<string, object?> payload, string field, out long number)
    {
        number = 0;
        if (!payload.TryGetValue(field, out var value) || value is null) return false;
        // Reject signed negatives, floating point and strings; capture already applies the manifest types.
        if (value is not (byte or ushort or uint or ulong or sbyte or short or int or long)) return false;
        try { number = Convert.ToInt64(value, CultureInfo.InvariantCulture); return number >= 0; }
        catch (OverflowException) { return false; }
    }

    public static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4()
        : address.AddressFamily == AddressFamily.InterNetworkV6 ? new IPAddress(address.GetAddressBytes()) : address;

    public static TrafficClass Classify(IPAddress? source, IPAddress? destination, IReadOnlySet<IPAddress> localAddresses)
    {
        if (source is null || destination is null) return TrafficClass.Unknown;
        source = Normalize(source); destination = Normalize(destination);
        if (IPAddress.IsLoopback(source) && IPAddress.IsLoopback(destination)) return TrafficClass.Loopback;
        if (localAddresses.Contains(source) && localAddresses.Contains(destination)) return TrafficClass.LocalToLocal;
        // One loopback endpoint with an external endpoint is anomalous; never count it as physical traffic.
        if (IPAddress.IsLoopback(source) || IPAddress.IsLoopback(destination)) return TrafficClass.Unknown;
        return TrafficClass.NonLoopback;
    }
}

public interface IProcessResolver
{
    ProcessIdentity Resolve(int pid, DateTime eventTimeUtc);
}

public sealed class WindowsProcessResolver : IProcessResolver, IDisposable
{
    private readonly Dictionary<int, (Process Process, ProcessIdentity Identity)> cache = new();
    public ProcessIdentity Resolve(int pid, DateTime eventTimeUtc)
    {
        if (pid == 0) return new(pid, "Unknown / Unattributed", null, null, "Payload PID is zero");
        if (pid == 4) return new(pid, "System", null, null, "System payload PID");
        try
        {
            if (cache.TryGetValue(pid, out var cached))
            {
                if (!cached.Process.HasExited || eventTimeUtc <= cached.Process.ExitTime.ToUniversalTime())
                    return ValidateLifetime(cached.Identity, eventTimeUtc);
                cached.Process.Dispose(); cache.Remove(pid);
            }
            var process = Process.GetProcessById(pid);
            string name;
            DateTime? start = null;
            string? path = null;
            try { name = process.ProcessName + ".exe"; }
            catch { name = $"PID {pid}"; }
            try { start = process.StartTime.ToUniversalTime(); } catch { }
            try { path = process.MainModule?.FileName; } catch { }
            var identity = new ProcessIdentity(pid, name, path, start,
                path is null ? "Executable path unavailable; name identity is provisional" : "Executable path resolved");
            cache[pid] = (process, identity);
            return ValidateLifetime(identity, eventTimeUtc);
        }
        catch { return Unknown(pid, "Process exited or lookup denied"); }
    }
    private static ProcessIdentity ValidateLifetime(ProcessIdentity identity, DateTime eventTimeUtc) =>
        identity.StartTimeUtc > eventTimeUtc ? Unknown(identity.Pid, "Event predates current PID lifetime") : identity;
    public static ProcessIdentity Unknown(int pid, string reason) => new(pid, $"PID {pid}", null, null, reason);
    public void Dispose() { foreach (var entry in cache.Values) entry.Process.Dispose(); cache.Clear(); }
}

public sealed class ActivityAggregator(IProcessResolver resolver)
{
    private readonly Dictionary<string, ProcessObservation> processes = new(StringComparer.Ordinal);
    public IReadOnlyList<ProcessObservation> Processes => processes.Values.OrderByDescending(x => x.Bytes.Total).ToArray();
    public Dictionary<TrafficClass, ByteTotals> Traffic { get; } = new();
    public long EventCount { get; private set; }
    public bool ObservedIpv4 { get; private set; }
    public bool ObservedIpv6 { get; private set; }
    public bool AllEventsHaveInterface { get; private set; } = true;
    public HashSet<int> NonLoopbackInterfaces { get; } = new();
    public bool NonLoopbackInterfaceMissing { get; private set; }
    public void Add(NetworkActivity activity)
    {
        if (activity.Bytes < 0 || activity.Pid < 0) return;
        ProcessIdentity identity;
        try { identity = resolver.Resolve(activity.Pid, activity.TimestampUtc); }
        catch { identity = WindowsProcessResolver.Unknown(activity.Pid, "Process lookup failed"); }
        if (!processes.TryGetValue(identity.LifetimeKey, out var observation))
            processes[identity.LifetimeKey] = observation = new(identity);
        observation.Add(activity);
        if (!Traffic.TryGetValue(activity.TrafficClass, out var totals)) Traffic[activity.TrafficClass] = totals = new();
        totals.Add(activity.Direction, activity.Bytes);
        EventCount++;
        ObservedIpv4 |= activity.Family == IpFamily.Ipv4;
        ObservedIpv6 |= activity.Family == IpFamily.Ipv6;
        AllEventsHaveInterface &= activity.InterfaceIndex is not null;
        if (activity.TrafficClass == TrafficClass.NonLoopback)
        {
            if (activity.InterfaceIndex is int index) NonLoopbackInterfaces.Add(index);
            else NonLoopbackInterfaceMissing = true;
        }
    }
    public IReadOnlyList<ApplicationObservation> Applications => processes.Values.GroupBy(x => x.Identity.ApplicationKey)
        .Select(group =>
        {
            var totals = new ByteTotals();
            foreach (var process in group) { totals.Add(TrafficDirection.Receive, process.Bytes.Receive); totals.Add(TrafficDirection.Send, process.Bytes.Send); }
            var identity = group.First().Identity;
            return new ApplicationObservation(group.Key, identity.Name, identity.ExecutablePath, group.ToArray(), totals);
        }).OrderByDescending(x => x.Bytes.Total).ToArray();
}

public static class ByteFormatting
{
    public static string Format(long bytes)
    {
        var amount = (double)Math.Max(0, bytes);
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB"];
        var unit = 0;
        while (amount >= 1024 && unit < units.Length - 1) { amount /= 1024; unit++; }
        return amount.ToString(unit == 0 ? "0" : "0.##", CultureInfo.InvariantCulture) + " " + units[unit];
    }
}
