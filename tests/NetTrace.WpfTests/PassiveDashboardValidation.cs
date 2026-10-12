using System.Globalization;
using System.ComponentModel;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using NetTrace;
using NetTrace.Models;
using NetTrace.Presentation;
using NetTrace.Services;

// A bounded passive counter observation. This command generates no traffic and
// never constructs monitoring against the user's database or adapter.
internal static class PassiveDashboardValidation
{
    private const int ObservationCount = 35;

    public static int Run()
    {
        try
        {
            var network = new NetworkMonitor();
            var adapter = SelectPhysicalAdapter(network.GetActiveAdapters());
            if (adapter is null)
            {
                Console.WriteLine("Passive Dashboard validation: no confirmed active physical adapter is available; live readings could not be observed.");
                return 0;
            }
            Console.WriteLine($"Passive Dashboard traffic observation: {adapter.Type} adapter {adapter.Name}; {ObservationCount} manually awaited one-second intervals, using existing counter/delta readers.");
            var readings = ObserveAsync(network, adapter).GetAwaiter().GetResult();
            if (readings.Count == 0)
            {
                Console.WriteLine("No valid counter intervals were available. The adapter and real NetTrace monitoring were left unchanged.");
                return 0;
            }

            var byteReading = readings.FirstOrDefault(reading => HasUnit(reading, " B/s"));
            var kilobyteReading = readings.FirstOrDefault(reading => HasUnit(reading, " KB/s"));
            var megabyteReading = readings.FirstOrDefault(reading => HasUnit(reading, " MB/s"));
            var low = readings.OrderBy(reading => Math.Max(reading.Download, reading.Upload)).First();
            var peak = readings.OrderByDescending(reading => Math.Max(reading.Download, reading.Upload)).First();
            if (byteReading is not null) Report("Observed B/s example", byteReading);
            if (kilobyteReading is not null) Report("Observed KB/s example", kilobyteReading);
            Report("Observed peak pair", peak);
            Console.WriteLine(megabyteReading is null
                ? "An MB/s reading did not occur naturally during the bounded observation; higher-throughput live validation remains unavailable. No traffic or download was generated."
                : "An MB/s reading occurred naturally; no traffic or download was generated.");
            if (megabyteReading is not null) Report("Observed MB/s example", megabyteReading);
            RenderObservedPairs(low, peak);
            Console.WriteLine("Only local adapter metadata and existing byte counters were read. No internet probes, network/configuration changes, real history access, monitoring restart or new production worker/timer occurred.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Passive Dashboard validation failed: " + exception);
            return 1;
        }
        finally { Application.Current?.Shutdown(); }
    }

    private static AdapterInfo? SelectPhysicalAdapter(IReadOnlyList<AdapterInfo> adapters)
    {
        // Reuse the physical connection selector's native metadata helpers once,
        // without constructing its callback-owning identity service.
        var native = typeof(WindowsNetworkNative);
        var hardware = native.GetMethod("IsConnectedHardwareInterface", BindingFlags.Static | BindingFlags.NonPublic)!;
        var routes = (Dictionary<uint, uint>)native.GetMethod("GetPhysicalDefaultRouteMetrics", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, null)!;
        var interfaces = NetworkInterface.GetAllNetworkInterfaces();
        var candidates = new List<(AdapterInfo Adapter, uint Metric)>();
        foreach (var adapter in adapters)
        {
            if (adapter.Type is not ("Ethernet" or "Wireless80211")) continue;
            var nic = interfaces.FirstOrDefault(candidate => string.Equals(candidate.Id, adapter.Id, StringComparison.OrdinalIgnoreCase));
            if (nic is null) continue;
            try
            {
                var properties = nic.GetIPProperties();
                var index = nic.Supports(NetworkInterfaceComponent.IPv4)
                    ? properties.GetIPv4Properties()?.Index : properties.GetIPv6Properties()?.Index;
                if (index is null || !(bool)hardware.Invoke(null, new object[] { (uint)index.Value })!) continue;
                candidates.Add((adapter, routes.GetValueOrDefault((uint)index.Value, uint.MaxValue)));
            }
            catch (NetworkInformationException) { }
            catch (TargetInvocationException exception) when (exception.InnerException is Win32Exception) { }
        }
        return candidates.OrderByDescending(candidate => candidate.Adapter.HasGateway)
            .ThenBy(candidate => candidate.Metric)
            .ThenBy(candidate => candidate.Adapter.Type == "Ethernet" ? 0 : 1)
            .ThenBy(candidate => candidate.Adapter.Id, StringComparer.Ordinal)
            .Select(candidate => candidate.Adapter).FirstOrDefault();
    }

    private static async Task<IReadOnlyList<ObservedRates>> ObserveAsync(NetworkMonitor network, AdapterInfo adapter)
    {
        var readings = new List<ObservedRates>(ObservationCount);
        var previous = network.Capture(adapter.Id);
        for (var observation = 0; observation < ObservationCount; observation++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            var current = network.Capture(adapter.Id);
            if (current is null)
            {
                previous = null;
                continue;
            }
            var delta = BandwidthDeltaCalculator.Calculate(previous, current);
            previous = current;
            if (delta is null) continue;
            var reading = new ObservedRates(current.Timestamp,
                delta.DownloadBytes / delta.ElapsedSeconds, delta.UploadBytes / delta.ElapsedSeconds);
            readings.Add(reading);
            Report($"Interval {observation + 1:00}", reading);
        }
        return readings;
    }

    private static bool HasUnit(ObservedRates reading, string unit) =>
        LiveSpeedPresentation.Create(reading.Download).ByteRateText.EndsWith(unit, StringComparison.Ordinal) ||
        LiveSpeedPresentation.Create(reading.Upload).ByteRateText.EndsWith(unit, StringComparison.Ordinal);

    private static void Report(string label, ObservedRates reading)
    {
        var download = LiveSpeedPresentation.Create(reading.Download);
        var upload = LiveSpeedPresentation.Create(reading.Upload);
        Console.WriteLine($"{label} ({reading.Timestamp:O}): Download {Raw(reading.Download)} bytes/s => {download.ByteRateText} ({download.MbpsText}); " +
            $"Upload {Raw(reading.Upload)} bytes/s => {upload.ByteRateText} ({upload.MbpsText}).");
    }

    private static string Raw(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    private static void RenderObservedPairs(ObservedRates low, ObservedRates peak)
    {
        _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "NetTrace-dashboard-passive-" + Guid.NewGuid().ToString("N"));
        using var monitoring = HistoryOverviewChecks.CreateMonitoring(new UsageStore(System.IO.Path.Combine(root, "empty-shell")));
        var window = HistoryOverviewChecks.CreateWindow(monitoring);
        try
        {
            window.Show();
            HistoryOverviewChecks.StopViewTimer(window);
            var apply = typeof(MainWindow).GetMethod("ApplyLiveSpeed", BindingFlags.Instance | BindingFlags.NonPublic)!;
            foreach (var (name, reading) in new[] { ("low", low), ("peak", peak) })
            {
                apply.Invoke(window, new object[] { reading.Download, reading.Upload });
                var down = LiveSpeedPresentation.Create(reading.Download);
                var up = LiveSpeedPresentation.Create(reading.Upload);
                if (Text(window, "DownloadRateText") != down.ByteRateText || Text(window, "UploadRateText") != up.ByteRateText ||
                    Text(window, "DownloadMbpsText") != down.MbpsText || Text(window, "UploadMbpsText") != up.MbpsText)
                    throw new InvalidOperationException("The owned Dashboard did not render the exact observed primary and secondary rate pair.");
                window.UpdateLayout();
                HistoryOverviewChecks.Pump();
                HistoryOverviewChecks.SavePreview(window, System.IO.Path.Combine(root, "760x600-observed-" + name + ".png"));
            }
            Console.WriteLine("Observed-rate Dashboard screenshots: " + root);
            Console.WriteLine("Only the paired speed readings in these screenshots are real observations; other dashboard values come from an isolated empty fixture.");
        }
        finally { window.Close(); }
    }

    private static string Text(MainWindow window, string name) => HistoryOverviewChecks.Control<TextBlock>(window, name).Text;
    private sealed record ObservedRates(DateTimeOffset Timestamp, double Download, double Upload);
}
