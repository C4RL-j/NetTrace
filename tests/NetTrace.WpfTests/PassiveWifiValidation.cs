using System.Net.NetworkInformation;
using NetTrace.Presentation;
using NetTrace.Services;

// Explicit one-shot local observation. No scan, configuration change, real
// database, ping, DNS lookup or HTTPS request is involved in this command.
internal static class PassiveWifiValidation
{
    public static int Run()
    {
        try
        {
            var network = new NetworkMonitor();
            var source = new WindowsWifiRadioSource();
            var adapters = network.GetActiveAdapters().Where(adapter =>
                string.Equals(adapter.Type, "Wireless80211", StringComparison.OrdinalIgnoreCase)).ToArray();
            var observed = false;
            foreach (var adapter in adapters)
            {
                var details = network.GetAdapterDetails(adapter.Id);
                if (details?.Status != OperationalStatus.Up) continue;
                var radio = source.Read(adapter.Id);
                var values = WifiRadioPresentation.Create(adapter, details, radio);
                Console.WriteLine($"Passive local Wi-Fi observation: adapter {adapter.Name}; connected {radio.IsConnected}.");
                Console.WriteLine($"Signal quality {values.SignalQuality}; RSSI {values.Rssi}; Channel {values.Channel}; Band / Frequency {values.BandFrequency}.");
                Console.WriteLine($"BSSID {values.Bssid}; PHY / radio type {values.Phy}; Receive link rate {values.ReceiveLinkRate}; Transmit link rate {values.TransmitLinkRate}.");
                if (!string.IsNullOrWhiteSpace(radio.Diagnostic)) Console.WriteLine("Windows metadata: " + radio.Diagnostic);
                observed |= values.Show;
            }
            if (!observed) Console.WriteLine("Live Wi-Fi radio values are unavailable on the current connected interfaces. The network was left unchanged.");
            Console.WriteLine("Only passive local adapter and current-association WLAN information was read. No nearby-network scan, network changes, internet requests or real NetTrace database access occurred.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Passive Wi-Fi validation failed: " + exception.Message);
            return 1;
        }
    }

}
