using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace AppUsageEtwProbe;

public static class ActivityParser
{
    public static bool TryParse(RawNetworkEvent raw, ProviderSchema schema,
        IReadOnlySet<IPAddress> localAddresses, out NetworkActivity? activity)
    {
        activity = null;
        var descriptor = schema.Events.FirstOrDefault(x => x.EventId == raw.EventId && x.Version == raw.Version);
        return descriptor is not null && TryParse(raw, descriptor, localAddresses, out activity);
    }

    public static bool TryParse(RawNetworkEvent raw, NetworkEventSchema descriptor,
        IReadOnlySet<IPAddress> localAddresses, out NetworkActivity? activity)
    {
        activity = null;
        if (descriptor.EventId != raw.EventId || descriptor.Version != raw.Version ||
            !TryUnsigned(raw.Payload, descriptor.PidField, out var pid) || pid > int.MaxValue ||
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
        : address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0
            ? new IPAddress(address.GetAddressBytes()) : address;

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

public interface IProcessIdentityLease : IDisposable
{
    ProcessIdentity Identity { get; }
    bool LifetimeVerified => true;
    bool HasExited { get; }
    DateTime? ExitTimeUtc { get; }
}

// One retained process handle identifies a particular lifetime even when Windows
// reuses its PID. The hot path performs one zero-time kernel wait, never process
// name/path/start-time lookups. Denied/exited lookups are negatively cached briefly.
public sealed class WindowsProcessResolver : IProcessResolver, IDisposable
{
    private sealed record Entry(IProcessIdentityLease? Lease, ProcessIdentity Identity,
        long CreatedTimestamp, LinkedListNode<int> Node);
    private readonly Dictionary<int, Entry> cache = new();
    private readonly LinkedList<int> lru = new();
    private readonly Func<int, IProcessIdentityLease?> lookup;
    private readonly int capacity;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan unavailableRetryInterval;
    private bool disposed;
    private static readonly ProcessIdentity Unattributed = new(0, "Unknown / Unattributed", null, null, "Payload PID is zero");
    private static readonly ProcessIdentity SystemIdentity = new(4, "System", null, null, "System payload PID");
    public int CacheCount => cache.Count;
    public long LookupCount { get; private set; }

    public WindowsProcessResolver(Func<int, IProcessIdentityLease?>? lookup = null, int capacity = 4096,
        TimeProvider? timeProvider = null, TimeSpan? unavailableRetryInterval = null)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        this.lookup = lookup ?? ReadProcess;
        this.capacity = capacity;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.unavailableRetryInterval = unavailableRetryInterval ?? TimeSpan.FromSeconds(1);
        if (this.unavailableRetryInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(unavailableRetryInterval));
    }

    public ProcessIdentity Resolve(int pid, DateTime eventTimeUtc)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (pid == 0) return Unattributed;
        if (pid == 4) return SystemIdentity;
        if (cache.TryGetValue(pid, out var cached))
        {
            lru.Remove(cached.Node); lru.AddLast(cached.Node);
            try
            {
                if (cached.Lease is null || !cached.Lease.LifetimeVerified)
                {
                    if (timeProvider.GetElapsedTime(cached.CreatedTimestamp) < unavailableRetryInterval)
                        return cached.Identity;
                }
                else if (!cached.Lease.HasExited || eventTimeUtc <= cached.Lease.ExitTimeUtc)
                    return ValidateLifetime(cached.Identity, eventTimeUtc);
            }
            catch { }
            Remove(pid, cached);
        }
        IProcessIdentityLease? lease = null;
        try { LookupCount++; lease = lookup(pid); }
        catch { }
        var identity = lease?.Identity ?? Unknown(pid, "Process exited or lookup denied");
        if (identity.Pid != pid)
        {
            lease?.Dispose(); lease = null;
            identity = Unknown(pid, "Process lookup returned an inconsistent PID");
        }
        while (cache.Count >= capacity)
        {
            int oldest = lru.First!.Value;
            Remove(oldest, cache[oldest]);
        }
        cache[pid] = new(lease, identity, timeProvider.GetTimestamp(), lru.AddLast(pid));
        return ValidateLifetime(identity, eventTimeUtc);
    }

    private void Remove(int pid, Entry entry)
    {
        cache.Remove(pid); lru.Remove(entry.Node);
        try { entry.Lease?.Dispose(); } catch { }
    }

    private static IProcessIdentityLease ReadProcess(int pid)
    {
        var process = Process.GetProcessById(pid);
        try
        {
            string name;
            DateTime? start = null;
            string? path = null;
            try { name = process.ProcessName + ".exe"; }
            catch { name = $"PID {pid}"; }
            try { start = process.StartTime.ToUniversalTime(); } catch { }
            try { path = process.MainModule?.FileName; } catch { }
            var identity = new ProcessIdentity(pid, name, path, start,
                path is null ? "Executable path unavailable; name identity is provisional" : "Executable path resolved");
            return new NativeProcessLease(process, identity);
        }
        catch { process.Dispose(); throw; }
    }

    private sealed class NativeProcessLease : IProcessIdentityLease
    {
        private readonly Process process;
        private readonly Microsoft.Win32.SafeHandles.SafeProcessHandle? handle;
        private readonly bool canWait;
        private readonly bool timesVerified;
        private DateTime? exitTime;
        public NativeProcessLease(Process process, ProcessIdentity identity)
        {
            this.process = process;
            var openedHandle = OpenProcess(0x00101000, false, (uint)identity.Pid); // SYNCHRONIZE | QUERY_LIMITED_INFORMATION
            if (!openedHandle.IsInvalid) { handle = openedHandle; canWait = true; }
            else
            {
                openedHandle.Dispose();
                openedHandle = OpenProcess(0x00001000, false, (uint)identity.Pid); // QUERY_LIMITED_INFORMATION only
                if (openedHandle.IsInvalid) openedHandle.Dispose();
                else handle = openedHandle;
            }
            if (handle is not null)
            {
                var path = new StringBuilder(32768);
                uint length = (uint)path.Capacity;
                if (QueryFullProcessImageName(handle, 0, path, ref length))
                    identity = identity with
                    {
                        Name = Path.GetFileName(path.ToString()), ExecutablePath = path.ToString(),
                        AttributionStatus = canWait ? "Executable path and lifetime handle resolved"
                            : "Executable path and query-only lifetime handle resolved"
                    };
                if (GetProcessTimes(handle, out var creation, out var exit, out _, out _))
                {
                    timesVerified = true;
                    long createdTicks = creation.ToInt64(), exitedTicks = exit.ToInt64();
                    if (createdTicks > 0) identity = identity with { StartTimeUtc = DateTime.FromFileTimeUtc(createdTicks) };
                    if (GetExitCodeProcess(handle, out uint exitCode) && exitCode != 259 && exitedTicks > 0)
                        exitTime = DateTime.FromFileTimeUtc(exitedTicks);
                }
            }
            Identity = identity;
        }
        public ProcessIdentity Identity { get; }
        public bool LifetimeVerified => handle is not null && (canWait || timesVerified);
        public bool HasExited
        {
            get
            {
                if (handle is null) return true;
                if (canWait) return WaitForSingleObject(handle, 0) switch
                {
                    0 => true, 258 => false, _ => throw new InvalidOperationException("Process lifetime handle is unavailable.")
                };
                if (!GetExitCodeProcess(handle, out uint exitCode))
                    throw new InvalidOperationException("Query-only process lifetime handle is unavailable.");
                return exitCode != 259; // STILL_ACTIVE; a live process's exit FILETIME is undefined.
            }
        }
        public DateTime? ExitTimeUtc
        {
            get
            {
                if (exitTime is not null) return exitTime;
                if (handle is not null && GetProcessTimes(handle, out _, out var exited, out _, out _))
                {
                    long ticks = exited.ToInt64();
                    if (ticks > 0) return exitTime = DateTime.FromFileTimeUtc(ticks);
                }
                try { return exitTime = process.ExitTime.ToUniversalTime(); } catch { return null; }
            }
        }
        public void Dispose() { handle?.Dispose(); process.Dispose(); }
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(Microsoft.Win32.SafeHandles.SafeProcessHandle handle, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern Microsoft.Win32.SafeHandles.SafeProcessHandle OpenProcess(uint access, bool inherit, uint processId);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "QueryFullProcessImageNameW")]
        private static extern bool QueryFullProcessImageName(Microsoft.Win32.SafeHandles.SafeProcessHandle handle, uint flags,
            StringBuilder name, ref uint length);
        [StructLayout(LayoutKind.Sequential)]
        private struct FileTime
        {
            public uint Low, High;
            public long ToInt64() => unchecked((long)(((ulong)High << 32) | Low));
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessTimes(Microsoft.Win32.SafeHandles.SafeProcessHandle handle,
            out FileTime creation, out FileTime exit, out FileTime kernel, out FileTime user);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeProcess(Microsoft.Win32.SafeHandles.SafeProcessHandle handle, out uint exitCode);
    }
    private static ProcessIdentity ValidateLifetime(ProcessIdentity identity, DateTime eventTimeUtc) =>
        identity.StartTimeUtc > eventTimeUtc ? Unknown(identity.Pid, "Event predates current PID lifetime") : identity;
    public static ProcessIdentity Unknown(int pid, string reason) => new(pid, $"PID {pid}", null, null, reason);
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var entry in cache.Values) { try { entry.Lease?.Dispose(); } catch { } }
        cache.Clear(); lru.Clear();
    }
}

public sealed class ActivityAggregator(IProcessResolver resolver)
{
    private readonly Dictionary<string, ProcessObservation> processes = new(StringComparer.Ordinal);
    private readonly Dictionary<int, (ProcessIdentity Identity, ProcessObservation Observation)> currentProcesses = new();
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
        ProcessObservation observation;
        if (currentProcesses.TryGetValue(identity.Pid, out var current) && ReferenceEquals(current.Identity, identity))
            observation = current.Observation;
        else
        {
            string lifetime = identity.LifetimeKey;
            if (!processes.TryGetValue(lifetime, out observation!)) processes[lifetime] = observation = new(identity);
            currentProcesses[identity.Pid] = (identity, observation);
        }
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
