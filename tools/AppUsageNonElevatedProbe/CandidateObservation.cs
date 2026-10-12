using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using AppUsageEtwProbe;

namespace AppUsageNonElevatedProbe;

public sealed class CandidateObservation(CandidateProviderSchema schema,
    IProcessResolver resolver, IReadOnlySet<IPAddress> localAddresses)
{
    private readonly Dictionary<(int, int, bool, bool), ObservedEventCapability> observed = new();
    private readonly Dictionary<string, CandidateProcessObservation> processes = new();
    private readonly List<object> samples = new();
    private readonly HashSet<(int, int)> sampled = new();
    public IReadOnlyList<ObservedEventCapability> Events => observed.Values.ToArray();
    public IReadOnlyList<CandidateProcessObservation> Processes => processes.Values.ToArray();
    public IReadOnlyList<object> Samples => samples;
    public bool ObservedIpv4 { get; private set; }
    public bool ObservedIpv6 { get; private set; }
    public bool ObservedLoopback { get; private set; }
    public long AttributedTransferBytes { get; private set; }
    public long TransferBytesWithoutPid { get; private set; }

    public void Add(CandidateRawEvent raw)
    {
        var descriptor = schema.Events.FirstOrDefault(e => e.EventId == raw.EventId && e.Version == raw.Version);
        if (descriptor is null) return;
        bool pidValid = TryCount(raw.Payload, descriptor.PidField, out var pid) && pid is > 0 and <= int.MaxValue;
        bool bytesValid = descriptor.IsTransferAccounting && TryCount(raw.Payload, descriptor.ByteField, out _);
        TryCount(raw.Payload, descriptor.ByteField, out long bytes);
        var key = (raw.EventId, raw.Version);
        // Keep incomplete occurrence shapes separate. A PID from one occurrence
        // and a byte count from another must never manufacture coherent accounting.
        var capabilityKey = (raw.EventId, raw.Version, pidValid, bytesValid);
        observed.TryGetValue(capabilityKey, out var previous);
        observed[capabilityKey] = new(raw.EventId, raw.Version, (previous?.EventCount ?? 0) + 1,
            pidValid, false, bytesValid,
            descriptor.Direction is not null, descriptor.IsTransferAccounting, descriptor.IsConnectionActivity);
        IPAddress? local = ReadEndpoint(raw.Payload, descriptor.LocalEndpointField);
        IPAddress? remote = ReadEndpoint(raw.Payload, descriptor.RemoteEndpointField);
        IpFamily? family = local?.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? IpFamily.Ipv4
            : local?.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? IpFamily.Ipv6 : null;
        if (family is not null && remote is not null && local!.AddressFamily != remote.AddressFamily) family = null;
        var trafficClass = ActivityParser.Classify(local, remote, localAddresses);
        ObservedIpv4 |= family == IpFamily.Ipv4;
        ObservedIpv6 |= family == IpFamily.Ipv6;
        ObservedLoopback |= trafficClass == TrafficClass.Loopback;
        if (pidValid)
        {
            ProcessIdentity identity;
            try { identity = resolver.Resolve((int)pid, raw.TimestampUtc); }
            catch { identity = WindowsProcessResolver.Unknown((int)pid, "Lookup unavailable"); }
            if (!processes.TryGetValue(identity.LifetimeKey, out var process))
                processes[identity.LifetimeKey] = process = new(identity);
            process.EventCount++;
            if (bytesValid)
            {
                if (descriptor.Direction is not null) process.Bytes.Add(descriptor.Direction.Value, bytes);
                else process.UndirectedBytes = ByteTotals.SaturatingAdd(process.UndirectedBytes, bytes);
                AttributedTransferBytes = ByteTotals.SaturatingAdd(AttributedTransferBytes, bytes);
                var bucket = $"{family?.ToString() ?? "FamilyUnknown"}/{trafficClass}";
                if (!process.Breakdown.TryGetValue(bucket, out var totals)) process.Breakdown[bucket] = totals = new();
                if (descriptor.Direction is not null) totals.Add(descriptor.Direction.Value, bytes);
            }
        }
        else if (bytesValid) TransferBytesWithoutPid = ByteTotals.SaturatingAdd(TransferBytesWithoutPid, bytes);
        if (sampled.Add(key) && samples.Count < 8)
            samples.Add(new { raw.EventId, raw.Version, descriptor.TaskName, raw.HeaderPid,
                PayloadPid = pidValid ? (int?)pid : null, Bytes = bytesValid ? (long?)bytes : null,
                Direction = descriptor.Direction?.ToString(), Family = family?.ToString(),
                Classification = trafficClass.ToString(), Fields = raw.Payload.Keys.ToArray() });
    }

    public static bool TryCount(IReadOnlyDictionary<string, object?> fields, string? name, out long value)
    {
        value = 0;
        if (name is null || !fields.TryGetValue(name, out var raw)
            || raw is not (byte or ushort or uint or ulong or sbyte or short or int or long)) return false;
        try { value = Convert.ToInt64(raw, CultureInfo.InvariantCulture); return value >= 0; }
        catch (OverflowException) { return false; }
    }

    private static IPAddress? ReadEndpoint(IReadOnlyDictionary<string, object?> fields, string? name)
    {
        if (name is null || !fields.TryGetValue(name, out var value)) return null;
        return value switch { IPAddress ip => ip, byte[] bytes => DecodeSocketAddress(bytes), _ => null };
    }

    // Windows sockaddr is decoded only for fields whose installed outType is win:SocketAddress.
    // Address bytes remain in memory. These are SDK sockaddr layouts, not ETW event offsets.
    public static IPAddress? DecodeSocketAddress(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 2) return null;
        var family = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        return family switch
        {
            2 when bytes.Length >= 16 => new IPAddress(bytes.Slice(4, 4)),
            23 when bytes.Length >= 28 => new IPAddress(bytes.Slice(8, 16), BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(24, 4))),
            _ => null
        };
    }

    public ControlledCandidateCheck CheckTransfer(ControlledTransferResult transfer)
    {
        ByteTotals Select(int pid)
        {
            var bytes = new ByteTotals();
            foreach (var totals in Processes.Where(p => p.Identity.Pid == pid).SelectMany(p => p.Breakdown)
                .Where(b => b.Key == $"{transfer.Family}/Loopback").Select(b => b.Value))
            { bytes.Add(TrafficDirection.Send, totals.Send); bytes.Add(TrafficDirection.Receive, totals.Receive); }
            return bytes;
        }
        var client = Select(transfer.ClientPid); var server = Select(transfer.ServerPid);
        return new(transfer.Family, transfer.ClientPid, transfer.ServerPid,
            transfer.ClientExitedCleanly, Processes.Any(p => p.Identity.Pid == transfer.ClientPid),
            Processes.Any(p => p.Identity.Pid == transfer.ServerPid),
            transfer.UploadBytes, transfer.DownloadBytes + 1, transfer.DownloadBytes + 1, transfer.UploadBytes,
            client, server, transfer.ClientExitedCleanly && client.Send == transfer.UploadBytes
                && client.Receive == transfer.DownloadBytes + 1 && server.Send == transfer.DownloadBytes + 1
                && server.Receive == transfer.UploadBytes);
    }
}

public sealed class CandidateProcessObservation(ProcessIdentity identity)
{
    public ProcessIdentity Identity { get; } = identity;
    public long EventCount { get; set; }
    public ByteTotals Bytes { get; } = new();
    public long UndirectedBytes { get; set; }
    public Dictionary<string, ByteTotals> Breakdown { get; } = new();
}

public sealed record ControlledCandidateCheck(IpFamily Family, int ClientPid, int ServerPid,
    bool WorkloadPassed, bool ClientObserved, bool ServerObserved, long ExpectedClientSend,
    long ExpectedClientReceive, long ExpectedServerSend, long ExpectedServerReceive,
    ByteTotals ObservedClient, ByteTotals ObservedServer, bool ByteAccountingVerified);
