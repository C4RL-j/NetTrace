using System.Net.NetworkInformation;
using System.Net.Sockets;
using NetTrace.Models;

namespace NetTrace.Services;

public sealed class NetworkMonitor
{
    public IReadOnlyList<AdapterInfo> GetActiveAdapters()
    {
        var adapters = new List<AdapterInfo>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
                continue;

            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;

            try
            {
                var properties = nic.GetIPProperties();
                var ipv4 = properties.UnicastAddresses
                    .FirstOrDefault(x => x.Address.AddressFamily == AddressFamily.InterNetwork)
                    ?.Address.ToString() ?? "—";

                var hasGateway = properties.GatewayAddresses.Any(x =>
                    x.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !x.Address.Equals(System.Net.IPAddress.Any));

                adapters.Add(new AdapterInfo(
                    nic.Id,
                    nic.Name,
                    nic.Description,
                    nic.NetworkInterfaceType.ToString(),
                    nic.Speed,
                    ipv4,
                    hasGateway));
            }
            catch
            {
                // Ignore transient or unsupported interfaces.
            }
        }

        return adapters
            .OrderByDescending(x => x.HasGateway)
            .ThenByDescending(x => x.SpeedBitsPerSecond)
            .ThenBy(x => x.Name)
            .ToList();
    }

    public NetworkSnapshot? Capture(string adapterId)
    {
        var nic = NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(x => x.Id == adapterId);

        if (nic is null || nic.OperationalStatus != OperationalStatus.Up)
            return null;

        try
        {
            var stats = nic.GetIPv4Statistics();
            return new NetworkSnapshot(
                stats.BytesReceived,
                stats.BytesSent,
                DateTimeOffset.Now);
        }
        catch
        {
            return null;
        }
    }
}
