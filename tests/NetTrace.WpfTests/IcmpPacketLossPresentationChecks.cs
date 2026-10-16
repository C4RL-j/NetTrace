using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NetTrace.Controls;
using NetTrace.Models;
using NetTrace.Presentation;

internal static class IcmpPacketLossPresentationChecks
{
    public static void Run(string root, Action<bool, string> check)
    {
        var failed = Sample(0);
        foreach (var reachability in new[] { InternetReachabilityStatus.Available, InternetReachabilityStatus.Limited })
        {
            check(IcmpPacketLossPresentation.Text(failed, reachability) == "Unavailable" &&
                IcmpPacketLossPresentation.Percent(failed, reachability) is null && failed.PacketLossPercent == 100,
                $"{reachability} internet with zero of four ICMP replies displays unavailable packet loss while preserving the raw observation");
            check(IcmpPacketLossPresentation.Explanation(failed, reachability, VpnTunnelStatus.Active) ==
                "Packet loss can't be measured for these samples. Ping replies may be blocked by the active VPN.",
                $"{reachability} internet with blocked ICMP and an active VPN explains why packet loss cannot be measured");
        }
        check(IcmpPacketLossPresentation.Text(failed, InternetReachabilityStatus.Unavailable) == "100%" &&
            IcmpPacketLossPresentation.Percent(failed, InternetReachabilityStatus.Unavailable) == 100 &&
            IcmpPacketLossPresentation.Explanation(failed, InternetReachabilityStatus.Unavailable, VpnTunnelStatus.Active) == "",
            "Unavailable internet with zero of four ICMP replies retains measurable full loss without a blocked-ping explanation");
        check(IcmpPacketLossPresentation.Text(failed, InternetReachabilityStatus.Checking) == "100%",
            "An unfinished reachability check preserves the previous packet-loss presentation");
        foreach (var replies in new[] { 3, 2 })
        {
            var sample = Sample(replies);
            var expected = replies == 3 ? 25 : 50;
            check(Enum.GetValues<InternetReachabilityStatus>().All(status =>
                    IcmpPacketLossPresentation.Text(sample, status) == $"{expected}%" &&
                    IcmpPacketLossPresentation.Percent(sample, status) == expected &&
                    IcmpPacketLossPresentation.Explanation(sample, status, VpnTunnelStatus.Active) == ""),
                $"{replies} of four ICMP replies retains {expected}% loss across reachability and active-VPN contexts");
        }
        check(IcmpPacketLossPresentation.Explanation(failed, InternetReachabilityStatus.Available, VpnTunnelStatus.Inactive) ==
                "Packet loss can't be measured for these samples." &&
            !IcmpPacketLossPresentation.Explanation(failed, InternetReachabilityStatus.Available, VpnTunnelStatus.Unknown)
                .Contains("VPN", StringComparison.Ordinal),
            "Unavailable packet loss explains the missing measurement without attributing it to an inactive or unknown VPN");

        CheckProjectionCache(check);
        CheckNativeCharts(root, check);
    }

    private static void CheckProjectionCache(Action<bool, string> check)
    {
        IcmpProbeResult[] samples = [Sample(3), Sample(0), Sample(2)];
        var line = new IcmpStabilityChart();
        var loss = new IcmpPacketLossChart();
        line.UpdateSamples(samples, InternetReachabilityStatus.Available);
        loss.UpdateData(line.Data);
        var reachableData = line.Data;
        check(reachableData.PacketLossPoints.Select(point => point.Percent).SequenceEqual(new double?[] { 25, null, 50 }) &&
            ReferenceEquals(line.Data, loss.Data) && reachableData.Samples.SequenceEqual(samples),
            "Reachable mixed history leaves a gap for blocked ICMP between the real 25% and 50% losses in the shared snapshot");
        line.UpdateSamples(samples, InternetReachabilityStatus.Limited);
        check(ReferenceEquals(reachableData, line.Data),
            "Available-to-Limited transitions reuse unchanged raw samples and identical unavailable loss points");
        line.UpdateSamples(samples, InternetReachabilityStatus.Unavailable);
        loss.UpdateData(line.Data);
        var unreachableData = line.Data;
        check(!ReferenceEquals(reachableData, unreachableData) && ReferenceEquals(line.Data, loss.Data) &&
            unreachableData.PacketLossPoints.Select(point => point.Percent).SequenceEqual(new double?[] { 25, 100, 50 }) &&
            unreachableData.LatencySegments.SelectMany(segment => segment).SequenceEqual(reachableData.LatencySegments.SelectMany(segment => segment)) &&
            unreachableData.JitterSegments.SelectMany(segment => segment).SequenceEqual(reachableData.JitterSegments.SelectMany(segment => segment)) &&
            unreachableData.MaxMilliseconds == reachableData.MaxMilliseconds,
            "Reachability alone restores genuine 100% loss without changing either latency or jitter series");
        line.UpdateSamples(samples, InternetReachabilityStatus.Available);
        loss.UpdateData(line.Data);
        check(line.Data.PacketLossPoints[1].Percent is null && ReferenceEquals(line.Data, loss.Data) &&
            samples[1].PacketLossPercent == 100 && reachableData.PacketLossPoints[1].Percent is null &&
            unreachableData.PacketLossPoints[1].Percent == 100,
            "Restored reachability removes the false full-loss bar while retained snapshots and raw measurements remain immutable");
    }

    private static void CheckNativeCharts(string root, Action<bool, string> check)
    {
        var fixtures = new[]
        {
            (Name: "available-zero-replies", Status: InternetReachabilityStatus.Available, Samples: new[] { Sample(0) }, HasBars: false, Neutral: true),
            (Name: "limited-zero-replies", Status: InternetReachabilityStatus.Limited, Samples: new[] { Sample(0) }, HasBars: false, Neutral: true),
            (Name: "unreachable-full-loss", Status: InternetReachabilityStatus.Unavailable, Samples: new[] { Sample(0) }, HasBars: true, Neutral: false),
            (Name: "available-quarter-loss", Status: InternetReachabilityStatus.Available, Samples: new[] { Sample(3) }, HasBars: true, Neutral: false),
            (Name: "available-half-loss", Status: InternetReachabilityStatus.Available, Samples: new[] { Sample(2) }, HasBars: true, Neutral: false),
            (Name: "available-mixed-gaps", Status: InternetReachabilityStatus.Available, Samples: new[] { Sample(0), Sample(3), Sample(0), Sample(2) }, HasBars: true, Neutral: false),
        };
        foreach (var fixture in fixtures)
        {
            var chart = new IcmpPacketLossChart { Width = 680, Height = 70 };
            chart.UpdateData(IcmpStabilityChartData.Create(fixture.Samples, fixture.Status));
            var host = new Window
            {
                Content = chart, SizeToContent = SizeToContent.WidthAndHeight,
                ShowActivated = false, ShowInTaskbar = false, Opacity = 0,
            };
            try
            {
                host.Show();
                host.UpdateLayout();
                Pump();
                var bitmap = Render(chart);
                var lossPixels = CountLossPixels(bitmap);
                check((lossPixels > 0) == fixture.HasBars &&
                    chart.Message == (fixture.Neutral ? "Packet loss can't be measured for these samples." : ""),
                    $"Native {fixture.Name} rendering shows the correct neutral message or measured bars without false 100% loss");
                Save(bitmap, System.IO.Path.Combine(root, "packet-loss-presentation-previews", fixture.Name + ".png"));
            }
            finally { host.Close(); }
        }
        check(new IcmpPacketLossChart().Message == "Waiting for packet loss samples...",
            "An empty packet-loss chart retains its initial waiting state distinct from unavailable ICMP");
    }

    private static IcmpProbeResult Sample(int replies) => new(
        new DateTimeOffset(2026, 10, 14, 0, 0, 0, TimeSpan.Zero), "1.1.1.1", replies, 4 - replies,
        replies > 0 ? 40 : null, (4 - replies) * 25, replies >= 2 ? 4 : null);

    private static RenderTargetBitmap Render(FrameworkElement chart)
    {
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(15, 23, 42)), null, new Rect(0, 0, chart.Width, chart.Height));
            context.DrawRectangle(new VisualBrush(chart) { Stretch = Stretch.Fill }, null, new Rect(0, 0, chart.Width, chart.Height));
        }
        var bitmap = new RenderTargetBitmap((int)chart.Width, (int)chart.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        return bitmap;
    }

    private static int CountLossPixels(RenderTargetBitmap bitmap)
    {
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return Enumerable.Range(0, pixels.Length / 4).Count(index =>
            pixels[index * 4] > 220 && pixels[index * 4 + 1] is > 115 and < 160 &&
            pixels[index * 4 + 2] is > 145 and < 190);
    }

    private static void Save(RenderTargetBitmap bitmap, string path)
    {
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = System.IO.File.Create(path);
        encoder.Save(output);
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
