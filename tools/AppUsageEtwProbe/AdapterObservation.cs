using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json.Serialization;
using NetTrace.Models;
using NetTrace.Services;

namespace AppUsageEtwProbe;

// Passive diagnostic reads only. The production monitoring service and database
// are never constructed. Counter capture reuses NetTrace's existing implementation.
public sealed class AdapterObservation
{
    private readonly NetworkMonitor _monitor = new();
    private readonly VpnDetectionService _vpnDetection = new();
    private HashSet<IPAddress> _localAddresses = new();
    private readonly string? _selectionDiagnostic;

    private AdapterObservation(AdapterMetadata? adapter, string? diagnostic)
    {
        Adapter = adapter;
        _selectionDiagnostic = diagnostic;
        _localAddresses = ReadLocalAddresses();
        Vpn = ReadVpn();
    }

    public AdapterMetadata? Adapter { get; }
    public AdapterMetadata? Metadata => Adapter;

    // Addresses are used only for endpoint classification in memory. They must
    // never be serialized or included in the console/report output.
    [JsonIgnore]
    public IReadOnlySet<IPAddress> LocalAddresses => _localAddresses;

    public VpnObservation Vpn { get; private set; }

    public static AdapterObservation Create(string? requestedAdapterId = null)
    {
        try
        {
            var candidates = ReadPhysicalCandidates();
            if (requestedAdapterId is not null)
            {
                var requested = candidates.FirstOrDefault(candidate =>
                    string.Equals(candidate.Interface.Id, requestedAdapterId, StringComparison.OrdinalIgnoreCase));
                return requested is null
                    ? new(null, "The requested adapter is not an active hardware Wi-Fi or Ethernet interface.")
                    : new(MetadataFor(requested, "Explicit diagnostic adapter selection."), null);
            }

            // Reuse the application's physical identity preference. Neither the
            // SSID nor any network identity is retained in a diagnostic result.
            string? recognizedAdapterId = null;
            try
            {
                using var identity = new NetworkIdentityService();
                recognizedAdapterId = identity.GetCurrentNetwork().Network?.AdapterId;
            }
            catch
            {
                // Native identity permission failures do not prevent passive
                // hardware-counter validation on a confirmed physical adapter.
            }

            var recognized = candidates.FirstOrDefault(candidate =>
                string.Equals(candidate.Interface.Id, recognizedAdapterId, StringComparison.OrdinalIgnoreCase));
            if (recognized is not null)
                return new(MetadataFor(recognized, "NetTrace physical identity selection."), null);

            // This is the same hardware/gateway/route-metric/Ethernet/ID ranking
            // used by NetworkIdentityService when identity details are unavailable.
            var fallback = candidates.FirstOrDefault();
            return fallback is null
                ? new(null, "No active hardware Wi-Fi or Ethernet adapter is available.")
                : new(MetadataFor(fallback,
                    "NetTrace physical candidate ordering; network identity unavailable."), null);
        }
        catch
        {
            return new(null, "Windows could not enumerate physical adapters for the diagnostic comparison.");
        }
    }

    public bool IsLocalAddress(IPAddress address) =>
        IPAddress.IsLoopback(address) || _localAddresses.Contains(NormalizeAddress(address));

    public AdapterCounterSnapshot Capture()
    {
        // Refresh local addresses without exposing address values. Publishing a
        // new set avoids mutating the set observed by the ETW consumer callback.
        _localAddresses = ReadLocalAddresses();
        Vpn = ReadVpn();
        if (Adapter is null)
            return new(DateTimeOffset.UtcNow, null, null, null, null, null, false, Vpn,
                _selectionDiagnostic ?? "No physical adapter was selected.");

        try
        {
            var selected = ReadPhysicalCandidates().FirstOrDefault(candidate =>
                string.Equals(candidate.Interface.Id, Adapter.Id, StringComparison.OrdinalIgnoreCase));
            if (selected is null)
                return new(DateTimeOffset.UtcNow, Adapter.Id, null, null, null, null, false, Vpn,
                    "The selected physical adapter disconnected or could not be read.");

            var counters = _monitor.Capture(selected.Interface.Id);
            return counters is null
                ? new(DateTimeOffset.UtcNow, Adapter.Id, selected.InterfaceIndex,
                    null, null, null, false, Vpn, "NetTrace could not read the selected adapter byte counters.")
                : new(DateTimeOffset.UtcNow, Adapter.Id, selected.InterfaceIndex,
                    counters.Timestamp.ToUniversalTime(), counters.BytesReceived, counters.BytesSent,
                    true, Vpn, null);
        }
        catch
        {
            return new(DateTimeOffset.UtcNow, Adapter.Id, null, null, null, null, false, Vpn,
                "Windows could not read the selected physical adapter for this observation.");
        }
    }

    public static AdapterComparison Compare(AdapterCounterSnapshot start, AdapterCounterSnapshot end)
    {
        var elapsed = ((end.CounterTimestampUtc ?? end.CapturedAtUtc) -
            (start.CounterTimestampUtc ?? start.CapturedAtUtc)).TotalSeconds;
        AdapterComparison Unavailable(string reason) => new(false, null, null, null, elapsed, reason);

        if (!start.Operational || !end.Operational || start.RxBytes is null || end.RxBytes is null ||
            start.TxBytes is null || end.TxBytes is null)
            return Unavailable("Physical-adapter counters were unavailable at an observation boundary.");
        if (start.AdapterId is null || !string.Equals(start.AdapterId, end.AdapterId,
                StringComparison.OrdinalIgnoreCase) || start.InterfaceIndex != end.InterfaceIndex)
            return Unavailable("The physical interface changed during the observation window.");
        if (elapsed <= 0)
            return Unavailable("The adapter observation timestamps did not advance.");
        if (start.RxBytes < 0 || start.TxBytes < 0 || end.RxBytes < start.RxBytes || end.TxBytes < start.TxBytes)
            return Unavailable("The physical-adapter counters reset during the observation window.");

        var rx = end.RxBytes.Value - start.RxBytes.Value;
        var tx = end.TxBytes.Value - start.TxBytes.Value;
        if (long.MaxValue - rx < tx)
            return Unavailable("The combined physical-adapter delta exceeds the diagnostic numeric range.");
        return new(true, rx, tx, rx + tx, elapsed, null);
    }

    // A provider lacking an interface index cannot establish that its non-local
    // traffic crossed this selected adapter, even without a detected VPN.
    public static CoverageAssessment AssessCoverage(AdapterComparison comparison,
        VpnObservation startVpn, VpnObservation endVpn, bool allRelevantEventsMatchSelectedInterface,
        long relevantEtwBytes)
    {
        if (!comparison.Valid || comparison.TotalBytes is not > 0)
            return new(null, "No positive valid physical-adapter delta is available.");
        if (startVpn.Status != nameof(VpnStatus.NoVpn) || endVpn.Status != nameof(VpnStatus.NoVpn))
            return new(null,
                "VPN/tunnel evidence is active or uncertain; logical app and outer transport bytes are not interchangeable.");
        if (!allRelevantEventsMatchSelectedInterface)
            return new(null,
                "Observed ETW events cannot all be attributed to the selected physical interface.");
        if (relevantEtwBytes < 0)
            return new(null, "The relevant ETW byte count is invalid.");
        return new((double)relevantEtwBytes / comparison.TotalBytes.Value,
            "Diagnostic event-byte to adapter-byte ratio; protocol overhead and event semantics may differ.");
    }

    private VpnObservation ReadVpn()
    {
        try
        {
            var state = _vpnDetection.GetCurrentState();
            return new(state.Status.ToString(), state.Status == VpnStatus.TunnelDetected ? state.Interfaces.Count : 0,
                state.Interfaces.Select(item => item.Evidence).Distinct(StringComparer.Ordinal).ToArray(),
                state.Status == VpnStatus.Indeterminate
                    ? "Local VPN/tunnel inventory is incomplete or ambiguous."
                    : state.Status == VpnStatus.TunnelDetected
                        ? "Local evidence confirms an operational tunnel; routing and peer reachability are not inferred."
                        : "No supported active tunnel was detected by NetTrace's local inventory.");
        }
        catch
        {
            return new(nameof(VpnStatus.Indeterminate), 0, Array.Empty<string>(),
                "Windows could not read the local VPN/tunnel inventory.");
        }
    }

    private static IReadOnlyList<PhysicalCandidate> ReadPhysicalCandidates()
    {
        var metrics = WindowsNetworkNative.GetPhysicalDefaultRouteMetrics();
        var candidates = new List<PhysicalCandidate>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType is not
                (NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet)) continue;
            try
            {
                var properties = nic.GetIPProperties();
                var index = nic.Supports(NetworkInterfaceComponent.IPv4)
                    ? properties.GetIPv4Properties()?.Index : properties.GetIPv6Properties()?.Index;
                if (index is null || !WindowsNetworkNative.IsConnectedHardwareInterface((uint)index.Value)) continue;
                var gateway = properties.GatewayAddresses.Any(item =>
                    !item.Address.Equals(IPAddress.Any) && !item.Address.Equals(IPAddress.IPv6Any));
                candidates.Add(new(nic, (uint)index.Value, gateway,
                    metrics.GetValueOrDefault((uint)index.Value, uint.MaxValue)));
            }
            catch
            {
                // A transient or unreadable interface is not physical evidence.
            }
        }
        return candidates.OrderByDescending(candidate => candidate.HasGateway)
            .ThenBy(candidate => candidate.DefaultRouteMetric)
            .ThenBy(candidate => candidate.Interface.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? 0 : 1)
            .ThenBy(candidate => candidate.Interface.Id, StringComparer.Ordinal).ToArray();
    }

    private static AdapterMetadata MetadataFor(PhysicalCandidate candidate, string selectionReason) =>
        new(candidate.Interface.Id, candidate.Interface.Name, candidate.Interface.NetworkInterfaceType.ToString(),
            candidate.InterfaceIndex, selectionReason);

    private static HashSet<IPAddress> ReadLocalAddresses()
    {
        var addresses = new HashSet<IPAddress> { IPAddress.Loopback, IPAddress.IPv6Loopback };
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    foreach (var item in nic.GetIPProperties().UnicastAddresses)
                        addresses.Add(NormalizeAddress(item.Address));
                }
                catch { }
            }
        }
        catch { }
        return addresses;
    }

    internal static IPAddress NormalizeAddress(IPAddress address) => address.IsIPv4MappedToIPv6
        ? address.MapToIPv4()
        : address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0
            ? new IPAddress(address.GetAddressBytes()) : address;

    private sealed record PhysicalCandidate(NetworkInterface Interface, uint InterfaceIndex,
        bool HasGateway, uint DefaultRouteMetric);
}

public sealed record AdapterMetadata(string Id, string Name, string Type, uint InterfaceIndex,
    string SelectionReason);

public sealed record VpnObservation(string Status, int ActiveCandidates,
    IReadOnlyList<string> Evidence, string? Reason);

public sealed record AdapterCounterSnapshot(DateTimeOffset CapturedAtUtc, string? AdapterId,
    uint? InterfaceIndex, DateTimeOffset? CounterTimestampUtc, long? RxBytes, long? TxBytes,
    bool Operational, VpnObservation Vpn, string? Diagnostic);

public sealed record AdapterComparison(bool Valid, long? RxBytes, long? TxBytes, long? TotalBytes,
    double ObservationSeconds, string? Reason);

public sealed record CoverageAssessment(double? Ratio, string Reason);
