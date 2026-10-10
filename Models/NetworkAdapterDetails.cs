using System.Net;
using System.Net.NetworkInformation;

namespace NetTrace.Models;

// A passive observation of one interface; it does not establish internet reachability.
public sealed record NetworkAdapterDetails(
    string AdapterId,
    OperationalStatus Status,
    IReadOnlyList<IPAddress> UnicastAddresses,
    IReadOnlyList<IPAddress> GatewayAddresses,
    IReadOnlyList<IPAddress> DnsAddresses,
    long SpeedBitsPerSecond);
