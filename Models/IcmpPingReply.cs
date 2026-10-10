using System.Net.NetworkInformation;

namespace NetTrace.Models;

public sealed record IcmpPingReply(IPStatus Status, long RoundtripTimeMs = 0);
