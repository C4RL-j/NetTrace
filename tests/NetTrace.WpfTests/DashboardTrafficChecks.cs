using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NetTrace;
using NetTrace.Controls;
using NetTrace.Models;
using NetTrace.Presentation;
using NetTrace.Services;
using NetTrace.Utils;

internal static class DashboardTrafficChecks
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Run(string root, Action<bool, string> check)
    {
        var previews = System.IO.Path.Combine(root, "dashboard-traffic-previews");
        CheckLiveSeries(root, previews, check);
        CheckVisibilityAndGaps(root, previews, check);
        Console.WriteLine("Dashboard live traffic previews: " + previews);
    }

    private static void CheckLiveSeries(string root, string previews, Action<bool, string> check)
    {
        var store = new UsageStore(System.IO.Path.Combine(root, "dashboard-traffic-series"));
        var clock = new OwnedClock();
        var counters = new OwnedCounters(clock);
        using var monitoring = CreateMonitoring(store, clock, counters);
        var window = CreateWindow(monitoring);
        try
        {
            window.Show();
            HistoryOverviewChecks.StopViewTimer(window);
            Layout(window);
            var chart = Control<DashboardTrafficChart>(window, "DashboardTrafficChart");
            check(Control<TabItem>(window, "DashboardTab").IsSelected &&
                Text(window, "DashboardUsageRangeText") == "TODAY" &&
                System.Windows.Automation.AutomationProperties.GetName(chart).Contains("130 seconds", StringComparison.Ordinal) &&
                chart.Data.Samples.Count == 0 && monitoring.GetSnapshot().TrafficSamples.Count == 0,
                "The native Dashboard graph opens beside the Today summary without inventing measurements before monitoring starts");
            Start(monitoring);
            monitoring.SampleNow();
            Step(window, monitoring, clock, counters, 28_800, 8);
            var snapshot = monitoring.GetSnapshot();
            var sample = chart.Data.Samples[^1];
            check(sample.TimestampUtc == clock.GetUtcNow() && sample.DownloadBytesPerSecond == 28_800 &&
                sample.UploadBytesPerSecond == 8 && sample.DownloadBytesPerSecond == snapshot.DownloadBytesPerSecond &&
                sample.UploadBytesPerSecond == snapshot.UploadBytesPerSecond &&
                Text(window, "DownloadRateText") == ByteRateFormatter.Format(sample.DownloadBytesPerSecond.GetValueOrDefault()) &&
                Text(window, "UploadRateText") == ByteRateFormatter.Format(sample.UploadBytesPerSecond.GetValueOrDefault()),
                "The graph and existing live cards display the same owned raw directional KB/s sample");
            Step(window, monitoring, clock, counters, 1_420_000, 1250);
            Step(window, monitoring, clock, counters, 5_000_000, 2_000_000);
            check(chart.Data.Samples[^1].DownloadBytesPerSecond == 5_000_000 &&
                chart.Data.Samples[^1].UploadBytesPerSecond == 2_000_000 &&
                chart.Data.MaximumBytesPerSecond >= 5_000_000 &&
                chart.Data.MaximumText.Contains("MB/s", StringComparison.Ordinal) &&
                Text(window, "DownloadRateText") == "5.00 MB/s" && Text(window, "UploadRateText") == "2.00 MB/s",
                "Asymmetric MB/s traffic raises the shared native Y scale while keeping both raw series exact");
            ShowGraph(window);
            CheckNativeDrawing(chart, check);
            CheckLayout(window, check, "mixed KB/s and MB/s");
            HistoryOverviewChecks.SavePreview(window, System.IO.Path.Combine(previews, "760x600-live-rates.png"));
            CheckTooltip(chart, chart.Data.Samples.Count - 1, check, "live rates",
                System.IO.Path.Combine(previews, "tooltip-live-rates.png"));
            check(chart.GetSampleAt(new Point(chart.PlotBounds.Left - 1, chart.PlotBounds.Top)) is null &&
                chart.GetSampleAt(new Point(double.NaN, chart.PlotBounds.Top)) is null,
                "Live graph hit testing rejects axis margins and invalid pointer coordinates");

            var data = chart.Data;
            var usage = monitoring.GetUsageTotals();
            var session = monitoring.GetSnapshot().Session;
            var captures = counters.Captures;
            var timer = Field<object>(window, "_timer");
            var workerNames = new[] { "_timer", "_profileMonitor", "_icmpMonitor", "_reachabilityMonitor", "_vpnSessions" };
            var workers = workerNames.Select(name => Field<object>(monitoring, name)).ToArray();
            foreach (var title in new[] { "LAST 7 DAYS", "LAST 30 DAYS", "TODAY" })
            {
                Cycle(window);
                check(Text(window, "DashboardUsageRangeText") == title && ReferenceEquals(chart.Data, data) &&
                    chart.Data.Samples[^1].DownloadBytesPerSecond == 5_000_000,
                    "Selecting " + title + " changes the cumulative summary while preserving the rolling live graph");
            }
            for (var index = 0; index < 5; index++) window.RefreshDashboard();
            Layout(window);
            check(ReferenceEquals(chart.Data, data) && counters.Captures == captures &&
                monitoring.GetUsageTotals() == usage && monitoring.GetSnapshot().Session == session &&
                ReferenceEquals(timer, Field<object>(window, "_timer")) &&
                workerNames.Select((name, index) => ReferenceEquals(workers[index], Field<object>(monitoring, name))).All(value => value) &&
                typeof(MainWindow).GetFields(PrivateInstance).Count(field => field.FieldType == typeof(System.Windows.Threading.DispatcherTimer)) == 1 &&
                typeof(MonitoringService).GetFields(PrivateInstance).Count(field => field.FieldType == typeof(System.Threading.Timer)) == 1,
                "Repeated same-second view and range updates neither duplicate graph samples nor add sampling, workers or usage");

            var oldEnd = chart.Data.WindowEndUtc;
            var oldStart = chart.Data.WindowStartUtc;
            var oldSampleTime = chart.Data.Samples[^1].TimestampUtc;
            Step(window, monitoring, clock, counters, 0, 0);
            check(chart.Data.WindowEndUtc == oldEnd.AddSeconds(1) && chart.Data.WindowStartUtc == oldStart.AddSeconds(1) &&
                chart.Data.WindowEndUtc - chart.Data.WindowStartUtc == TimeSpan.FromSeconds(130) &&
                chart.Data.Samples.Any(point => point.TimestampUtc == oldSampleTime) &&
                chart.Data.Samples[^1].DownloadBytesPerSecond == 0 && chart.Data.Samples[^1].UploadBytesPerSecond == 0,
                "One monitoring second advances the continuous 130-second X window and keeps genuine idle traffic at zero");
            CheckTooltip(chart, chart.Data.Samples.Count - 1, check, "idle zero rates",
                System.IO.Path.Combine(previews, "tooltip-zero-rates.png"));
            var tip = Hover(chart, chart.Data.Samples.Count - 1);
            tip.IsOpen = true;
            Step(window, monitoring, clock, counters, 650_000, 40_000);
            var selected = chart.SelectedSample;
            check(ReferenceEquals(tip, chart.ToolTip) && tip.IsOpen && selected is not null && selected.TimestampUtc == clock.GetUtcNow() &&
                selected.DownloadBytesPerSecond == 650_000 && ((TextBlock)tip.Content).Text == selected.ToolTipText,
                "A stationary tooltip updates to the exact sample below its pointer as the live graph moves");
            tip.IsOpen = false;

            for (var index = 0; index < 134; index++)
                Step(window, monitoring, clock, counters, (index % 8 + 1) * 50_000, (index % 5) * 20_000);
            check(chart.Data.Samples.Count == 130 &&
                chart.Data.Samples.Select(point => point.TimestampUtc).Distinct().Count() == chart.Data.Samples.Count &&
                chart.Data.Samples[0].TimestampUtc == chart.Data.WindowEndUtc.AddSeconds(-129) &&
                chart.Data.Samples.All(point => point.TimestampUtc > chart.Data.WindowStartUtc && point.TimestampUtc <= chart.Data.WindowEndUtc) &&
                chart.Data.Samples.Zip(chart.Data.Samples.Skip(1), (first, second) => second.TimestampUtc - first.TimestampUtc == TimeSpan.FromSeconds(1)).All(value => value),
                "The moving graph retains exactly 130 unique chronological one-second samples in the rolling 130-second window");
            var oldest = chart.Data.Samples[0];
            var secondOldest = chart.Data.Samples[1];
            Step(window, monitoring, clock, counters, 400_000, 80_000);
            check(chart.Data.Samples.Count == 130 && chart.Data.Samples[0] == secondOldest &&
                !chart.Data.Samples.Contains(oldest) && chart.Data.Samples[^1].TimestampUtc == clock.GetUtcNow(),
                "The next one-second observation removes only the oldest retained graph sample");
            check(chart.Data.MaximumBytesPerSecond >= 400_000 && chart.Data.MaximumBytesPerSecond < 5_000_000 &&
                chart.Data.MaximumText.Contains("KB/s", StringComparison.Ordinal),
                "The Y scale returns to KB/s after the earlier MB/s peak expires from the rolling window");
            ShowGraph(window);
            CheckNativeDrawing(chart, check);
            CheckLayout(window, check, "full rolling 130 seconds");
            HistoryOverviewChecks.SavePreview(window, System.IO.Path.Combine(previews, "760x600-rolling-130-seconds.png"));
            for (var index = 0; index < 130; index++)
                Step(window, monitoring, clock, counters, (index % 8 + 1) * 500_000, (index % 6) * 240_000);
            check(chart.Data.Samples.Count == 130 && chart.Data.Samples.Any(point => point.DownloadBytesPerSecond >= 1_000_000) &&
                chart.Data.Samples.Any(point => point.UploadBytesPerSecond >= 1_000_000) &&
                chart.Data.Samples.Any(point => point.DownloadBytesPerSecond is > 0 and < 1_000_000),
                "A full rolling 130 seconds preserves visibly distinct KB/s and MB/s activity in both live directions");
            ShowGraph(window);
            CheckNativeDrawing(chart, check);
            CheckLayout(window, check, "full mixed-unit rolling 130 seconds");
            HistoryOverviewChecks.SavePreview(window, System.IO.Path.Combine(previews, "760x600-full-mixed-rates.png"));
            window.Width = 920;
            window.Height = 740;
            Layout(window);
            CheckLayout(window, check, "resized 920x740");
            HistoryOverviewChecks.SavePreview(window, System.IO.Path.Combine(previews, "920x740-rolling-130-seconds.png"));
        }
        finally { window.Close(); }
    }

    private static void CheckVisibilityAndGaps(string root, string previews, Action<bool, string> check)
    {
        var store = new UsageStore(System.IO.Path.Combine(root, "dashboard-traffic-visibility"));
        var clock = new OwnedClock();
        var counters = new OwnedCounters(clock);
        using var monitoring = CreateMonitoring(store, clock, counters);
        var window = CreateWindow(monitoring);
        try
        {
            window.Show();
            HistoryOverviewChecks.StopViewTimer(window);
            Start(monitoring);
            monitoring.SampleNow();
            Step(window, monitoring, clock, counters, 3_000_000, 1_000_000);
            Step(window, monitoring, clock, counters, 2_000_000, 400_000);
            var chart = Control<DashboardTrafficChart>(window, "DashboardTrafficChart");
            counters.Disconnected = true;
            Step(window, monitoring, clock, counters, 0, 0);
            check(monitoring.GetSnapshot().Status == "Adapter disconnected" &&
                chart.Data.Samples[^1].DownloadBytesPerSecond is null && chart.Data.Samples[^1].UploadBytesPerSecond is null,
                "Unavailable physical traffic appears as a gap rather than manufactured idle zero rates");
            var unavailable = chart.Data.Samples[^1];
            var tip = Hover(chart, chart.Data.Samples.Count - 1);
            check(chart.SelectedSample?.TimestampUtc == unavailable.TimestampUtc && ((TextBlock)tip.Content).Text == unavailable.ToolTipText &&
                unavailable.ToolTipText.Contains("unavailable", StringComparison.OrdinalIgnoreCase),
                "An unavailable interval remains hoverable with an honest unavailable tooltip");
            counters.Disconnected = false;
            Step(window, monitoring, clock, counters, 0, 0);
            Step(window, monitoring, clock, counters, 900_000, 90_000);
            check(chart.Data.Samples[^1].DownloadBytesPerSecond == 900_000 &&
                chart.Data.Samples[^1].UploadBytesPerSecond == 90_000 &&
                chart.Data.Samples.Any(point => point.DownloadBytesPerSecond is null),
                "Live series resume after reconnection while preserving the unavailable gap");
            ShowGraph(window);
            HistoryOverviewChecks.SavePreview(window, System.IO.Path.Combine(previews, "760x600-offline-gap.png"));

            Cycle(window);
            Cycle(window);
            Hover(chart, chart.Data.Samples.Count - 1);
            var data = chart.Data;
            var navigation = Control<TabControl>(window, "MainNavigation");
            navigation.SelectedItem = Control<TabItem>(window, "SettingsTab");
            Layout(window);
            check(chart.SelectedSample is null && chart.ToolTip is null,
                "Leaving Dashboard clears graph hover and closes its native popup");
            HiddenSteps(window, monitoring, clock, counters, 140, 1_500_000, 200_000, data, check, "hidden Dashboard page");
            navigation.SelectedItem = Control<TabItem>(window, "DashboardTab");
            Layout(window);
            CheckReturnedHistory(window, monitoring, clock, data, 1_500_000, 200_000, check, "Returning to Dashboard");
            check(Text(window, "DashboardUsageRangeText") == "LAST 30 DAYS" &&
                Text(window, "DashboardUsageTitleText") == "LAST 30 DAYS",
                "Returning to Dashboard preserves the selected summary range during continuous background sampling");

            Hover(chart, chart.Data.Samples.Count - 1);
            data = chart.Data;
            window.Hide();
            Layout(window);
            HiddenSteps(window, monitoring, clock, counters, 136, 600_000, 100_000, data, check, "hidden window");
            check(chart.SelectedSample is null && chart.ToolTip is null,
                "Hiding the main window releases the graph tooltip and hover state");
            window.Show();
            HistoryOverviewChecks.StopViewTimer(window);
            Layout(window);
            CheckReturnedHistory(window, monitoring, clock, data, 600_000, 100_000, check, "Showing the main window");

            Hover(chart, chart.Data.Samples.Count - 1);
            data = chart.Data;
            window.WindowState = WindowState.Minimized;
            Layout(window);
            HiddenSteps(window, monitoring, clock, counters, 135, 800_000, 160_000, data, check, "minimized window");
            check(chart.SelectedSample is null && chart.ToolTip is null,
                "Minimizing clears graph hover as well as suspending its view updates");
            window.WindowState = WindowState.Normal;
            HistoryOverviewChecks.StopViewTimer(window);
            Layout(window);
            CheckReturnedHistory(window, monitoring, clock, data, 800_000, 160_000, check, "Restoring the window");
            ShowGraph(window);
            CheckLayout(window, check, "restored continuous history");
            HistoryOverviewChecks.SavePreview(window, System.IO.Path.Combine(previews, "760x600-restored-continuous.png"));
            counters.Disconnected = true;
            Step(window, monitoring, clock, counters, 0, 0);
            counters.Disconnected = false;
            Step(window, monitoring, clock, counters, 0, 0);
            Step(window, monitoring, clock, counters, 800_000, 160_000);
            check(chart.Data.Samples.Any(sample => sample.DownloadBytesPerSecond is null && sample.UploadBytesPerSecond is null) &&
                chart.Data.DownloadSegments.Count == 2 && chart.Data.UploadSegments.Count == 2 &&
                !chart.Data.Samples.Any(sample => sample.BreakBefore),
                "A genuine unavailable measurement still splits both series after hidden-page and minimize continuity is restored");
        }
        finally { window.Close(); }
    }

    private static void HiddenSteps(MainWindow window, MonitoringService monitoring, OwnedClock clock,
        OwnedCounters counters, int seconds, long download, long upload, DashboardTrafficChartData data,
        Action<bool, string> check, string state)
    {
        var chart = Control<DashboardTrafficChart>(window, "DashboardTrafficChart");
        var drawing = DrawingContent(chart);
        var capturesBefore = counters.Captures;
        var sourceBefore = monitoring.GetSnapshot().TrafficSamples;
        var timestampsBefore = sourceBefore.Select(sample => sample.TimestampUtc).ToArray();
        var timer = Field<object>(window, "_timer");
        var workerNames = new[] { "_timer", "_profileMonitor", "_icmpMonitor", "_reachabilityMonitor", "_vpnSessions" };
        var workers = workerNames.Select(name => Field<object>(monitoring, name)).ToArray();
        for (var index = 0; index < seconds; index++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            counters.Advance(download, upload);
            monitoring.SampleNow();
            window.RefreshDashboard();
            typeof(MainWindow).GetMethod("Monitoring_OnChanged", PrivateInstance)!.Invoke(window, new object[] { monitoring, EventArgs.Empty });
            // The control must also guard direct updates while it cannot render.
            chart.UpdateSamples(monitoring.GetSnapshot().TrafficSamples);
            Layout(window);
        }
        var snapshot = monitoring.GetSnapshot();
        var samples = snapshot.TrafficSamples;
        check(samples.Count == 130 && samples[0].TimestampUtc == clock.GetUtcNow().AddSeconds(-129) &&
            samples[^1].TimestampUtc == clock.GetUtcNow() && samples.All(sample => !sample.BreakBefore &&
                sample.DownloadBytesPerSecond == download && sample.UploadBytesPerSecond == upload) &&
            samples.Zip(samples.Skip(1), (first, second) => second.TimestampUtc - first.TimestampUtc == TimeSpan.FromSeconds(1)).All(value => value),
            "The " + state + " continues exact existing raw-rate samples in a bounded chronological 130-second timeline without artificial gaps");
        check(ReferenceEquals(chart.Data, data) && ReferenceEquals(DrawingContent(chart), drawing) &&
            ReferenceEquals(samples, monitoring.GetSnapshot().TrafficSamples) &&
            sourceBefore.Select(sample => sample.TimestampUtc).SequenceEqual(timestampsBefore),
            "The " + state + " freezes chart data and drawing while publishing cached immutable source snapshots");
        var capturesAfter = counters.Captures;
        var baseline = Field<object>(monitoring, "_previousSnapshot");
        window.RefreshDashboard();
        Layout(window);
        check(capturesAfter - capturesBefore == seconds && counters.Captures == capturesAfter &&
            ReferenceEquals(baseline, Field<object>(monitoring, "_previousSnapshot")) &&
            monitoring.GetSnapshot().SessionDownloaded == snapshot.SessionDownloaded &&
            monitoring.GetSnapshot().SessionUploaded == snapshot.SessionUploaded &&
            ReferenceEquals(timer, Field<object>(window, "_timer")) &&
            workerNames.Select((name, index) => ReferenceEquals(workers[index], Field<object>(monitoring, name))).All(value => value),
            "The " + state + " takes one existing adapter capture per sample and retains accounting, sampler and worker ownership");
    }

    private static void CheckReturnedHistory(MainWindow window, MonitoringService monitoring, OwnedClock clock,
        DashboardTrafficChartData previous, long download, long upload, Action<bool, string> check, string state)
    {
        var chart = Control<DashboardTrafficChart>(window, "DashboardTrafficChart");
        var snapshot = monitoring.GetSnapshot();
        check(!ReferenceEquals(chart.Data, previous) && chart.Data.Samples.SequenceEqual(snapshot.TrafficSamples) &&
            chart.Data.Samples.Count == 130 && chart.Data.Samples[0].TimestampUtc == clock.GetUtcNow().AddSeconds(-129) &&
            chart.Data.Samples[^1].TimestampUtc == clock.GetUtcNow() &&
            chart.Data.WindowEndUtc - chart.Data.WindowStartUtc == TimeSpan.FromSeconds(130) &&
            chart.Data.Samples.All(sample => !sample.BreakBefore && sample.DownloadBytesPerSecond == download && sample.UploadBytesPerSecond == upload) &&
            chart.Data.DownloadSegments.Count == 1 && chart.Data.UploadSegments.Count == 1 &&
            chart.Data.DownloadSegments[0].Count == 130 && chart.Data.UploadSegments[0].Count == 130 &&
            Text(window, "DownloadRateText") == ByteRateFormatter.Format(download) &&
            Text(window, "UploadRateText") == ByteRateFormatter.Format(upload),
            state + " immediately renders the already-collected continuous recent 130 seconds and the same latest rates as the cards");
    }

    private static object? DrawingContent(UIElement element) => typeof(UIElement)
        .GetField("_drawingContent", PrivateInstance)!.GetValue(element);

    private static void CheckTooltip(DashboardTrafficChart chart, int index, Action<bool, string> check,
        string state, string path)
    {
        var sample = chart.Data.Samples[index];
        var tip = Hover(chart, index);
        var text = (TextBlock)tip.Content;
        var local = sample.TimestampUtc.ToLocalTime();
        check(chart.GetSampleAt(PointAt(chart, index))?.TimestampUtc == sample.TimestampUtc &&
            chart.SelectedSample?.TimestampUtc == sample.TimestampUtc && text.Text == sample.ToolTipText &&
            text.Text.Contains(local.ToString("HH:mm:ss", CultureInfo.InvariantCulture), StringComparison.Ordinal) &&
            text.Text.Contains("Download: " + ByteRateFormatter.Format(sample.DownloadBytesPerSecond!.Value), StringComparison.Ordinal) &&
            text.Text.Contains("Upload: " + ByteRateFormatter.Format(sample.UploadBytesPerSecond!.Value), StringComparison.Ordinal) &&
            text.TextWrapping == TextWrapping.Wrap && double.IsFinite(text.MaxWidth) && text.MaxWidth > 0 && double.IsFinite(tip.MaxWidth),
            "The " + state + " tooltip exposes local sample time and the exact adaptive directional rates");
        tip.PlacementTarget = chart;
        tip.IsOpen = true;
        HistoryOverviewChecks.Pump();
        check(tip.IsVisible && text.ActualWidth <= text.MaxWidth + 0.5,
            "The " + state + " details open in a compact native WPF tooltip");
        HistoryUsageChecks.SaveTooltip(tip, path);
        tip.IsOpen = false;
    }

    private static void CheckNativeDrawing(DashboardTrafficChart chart, Action<bool, string> check)
    {
        var drawings = Drawings(VisualTreeHelper.GetDrawing(chart)).ToArray();
        var glyphs = string.Concat(drawings.OfType<GlyphRunDrawing>().Select(drawing =>
            drawing.GlyphRun.Characters is { } characters ? new string(characters.ToArray()) : ""));
        check(glyphs.Contains("Download", StringComparison.Ordinal) && glyphs.Contains("Upload", StringComparison.Ordinal) &&
            glyphs.Contains("/s", StringComparison.Ordinal) &&
            DashboardTrafficChart.DownloadBrush is SolidColorBrush download && download.IsFrozen &&
            DashboardTrafficChart.UploadBrush is SolidColorBrush upload && upload.IsFrozen &&
            download.Color == Color.FromRgb(0x22, 0xC5, 0x5E) && upload.Color == Color.FromRgb(0x38, 0xBD, 0xF8),
            "The native graph uses green Download and blue Upload consistently for its curves and legend");
        var ticks = drawings.OfType<GlyphRunDrawing>().Where(drawing => drawing.GlyphRun.Characters is { } characters &&
            new[] { "130s", "60s", "Now" }.Contains(new string(characters.ToArray()))).ToArray();
        var middle = ticks.SingleOrDefault(drawing => new string(drawing.GlyphRun.Characters.ToArray()) == "60s");
        check(ticks.Length == 3 && middle is not null &&
            Math.Abs(middle.Bounds.Left + middle.Bounds.Width / 2 -
                (chart.PlotBounds.Left + chart.PlotBounds.Width * (1 - 60.0 / 130))) < 2 &&
            ticks.All(tick => tick.Bounds.Left >= chart.PlotBounds.Left - 1 &&
                tick.Bounds.Right <= chart.PlotBounds.Right + 1 && tick.Bounds.Top >= chart.PlotBounds.Bottom),
            "The native X axis shows 130s, 60s and Now with the 60-second label at its true timeline position");
        var curves = drawings.OfType<GeometryDrawing>().Where(drawing => drawing.Pen is not null &&
            drawing.Geometry.Bounds.IntersectsWith(chart.PlotBounds) &&
            (ReferenceEquals(drawing.Pen.Brush, DashboardTrafficChart.DownloadBrush) ||
                ReferenceEquals(drawing.Pen.Brush, DashboardTrafficChart.UploadBrush))).ToArray();
        var expandedPlot = chart.PlotBounds;
        expandedPlot.Inflate(2, 2);
        check(curves.Any(drawing => ReferenceEquals(drawing.Pen!.Brush, DashboardTrafficChart.DownloadBrush)) &&
            curves.Any(drawing => ReferenceEquals(drawing.Pen!.Brush, DashboardTrafficChart.UploadBrush)) &&
            curves.All(drawing => expandedPlot.Contains(drawing.Geometry.Bounds)),
            "Both native directional speed curves render inside the autoscaled plot");
    }

    private static void CheckLayout(MainWindow window, Action<bool, string> check, string state)
    {
        ShowGraph(window);
        var page = (ScrollViewer)Control<TabItem>(window, "DashboardTab").Content;
        var section = Control<Grid>(window, "DashboardUsageSection");
        var summary = Control<Grid>(window, "DashboardUsageSummary");
        var chart = Control<DashboardTrafficChart>(window, "DashboardTrafficChart");
        var bounds = chart.TransformToAncestor(page).TransformBounds(new Rect(0, 0, chart.ActualWidth, chart.ActualHeight));
        var chartTop = chart.TranslatePoint(new Point(), section).Y;
        var card = Control<Border>(window, "DashboardUsageCard");
        var cardTop = card.TranslatePoint(new Point(), section).Y;
        var previousEqualWidth = (section.ActualWidth - section.ColumnDefinitions[1].ActualWidth) / 2;
        if (state is "mixed KB/s and MB/s" or "resized 920x740")
            Console.WriteLine(FormattableString.Invariant($"Dashboard traffic layout {window.Width:0}x{window.Height:0}: section {section.ActualWidth:0.##} DIP, summary {summary.ActualWidth:0.##} DIP, graph {chart.ActualWidth:0.##} DIP, previous equal width {previousEqualWidth:0.##} DIP."));
        check(window.MinWidth == 760 && window.MinHeight == 600 &&
            page.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled && page.ScrollableWidth < 0.01 &&
            bounds.Left >= -0.5 && bounds.Right <= page.ViewportWidth + 0.5 &&
            bounds.Top >= -0.5 && bounds.Bottom <= page.ActualHeight + 0.5 &&
            Math.Abs(chartTop - cardTop) < 0.5 && Grid.GetColumn(chart) == 2 && Grid.GetRow(chart) == 1 &&
            summary.ActualWidth < previousEqualWidth - 60 && chart.ActualWidth > previousEqualWidth + 60 &&
            Math.Abs(chart.ActualWidth + summary.ActualWidth + section.ColumnDefinitions[1].ActualWidth - section.ActualWidth) < 0.5 &&
            chart.PlotBounds.Width > 250 && chart.PlotBounds.Height > 40,
            "The " + state + " graph aligns to the refreshed summary card, keeps all freed width and fits the minimum Dashboard without horizontal overflow");
        var drawing = VisualTreeHelper.GetDrawing(chart);
        check(drawing is not null && drawing.Bounds.Left >= -2 && drawing.Bounds.Top >= -2 &&
            drawing.Bounds.Right <= chart.ActualWidth + 2 && drawing.Bounds.Bottom <= chart.ActualHeight + 2,
            "The " + state + " graph keeps its native axes, legend and curves inside its compact bounds");
    }

    private static void ShowGraph(MainWindow window)
    {
        Control<Grid>(window, "DashboardUsageSection").BringIntoView();
        Layout(window);
    }

    private static ToolTip Hover(DashboardTrafficChart chart, int index)
    {
        typeof(DashboardTrafficChart).GetMethod("UpdateHover", PrivateInstance)!.Invoke(chart, new object[] { PointAt(chart, index) });
        return chart.ToolTip as ToolTip ?? throw new InvalidOperationException("Live traffic hover did not create a tooltip.");
    }

    private static Point PointAt(DashboardTrafficChart chart, int index) => new(
        chart.PlotBounds.Left + chart.PlotBounds.Width *
            (chart.Data.Samples[index].TimestampUtc - chart.Data.WindowStartUtc).TotalSeconds /
            (chart.Data.WindowEndUtc - chart.Data.WindowStartUtc).TotalSeconds,
        chart.PlotBounds.Top + chart.PlotBounds.Height / 2);

    private static IEnumerable<Drawing> Drawings(Drawing? drawing)
    {
        if (drawing is null) yield break;
        yield return drawing;
        if (drawing is DrawingGroup group)
            foreach (var child in group.Children)
                foreach (var item in Drawings(child)) yield return item;
    }

    private static void Step(MainWindow window, MonitoringService monitoring, OwnedClock clock,
        OwnedCounters counters, long download, long upload)
    {
        clock.Advance(TimeSpan.FromSeconds(1));
        counters.Advance(download, upload);
        monitoring.SampleNow();
        window.RefreshDashboard();
        Layout(window);
    }

    private static void Start(MonitoringService monitoring)
    {
        monitoring.Start();
        Field<System.Threading.Timer>(monitoring, "_timer").Change(Timeout.Infinite, Timeout.Infinite);
        var elapsed = Stopwatch.StartNew();
        while (monitoring.GetSnapshot().Session is null)
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Owned traffic session did not start.");
            HistoryOverviewChecks.Pump();
        }
        HistoryOverviewChecks.Pump();
    }

    private static MonitoringService CreateMonitoring(UsageStore store, OwnedClock clock, OwnedCounters counters) =>
        new(store, new HistoryOverviewChecks.OwnedIdentity(new NetworkIdentity("WiFi", "Owned traffic network", "Owned traffic network", HistoryOverviewChecks.AdapterId)),
            vpnVerifier: new VpnRouteVerifier(new VpnDetectionService(new NoTunnel()), new NoRoutes()),
            networkMonitor: counters, timeProvider: clock, icmpMonitor: new IcmpMonitor(new NoPing()),
            reachabilityMonitor: TestReachability.Create());

    private static MainWindow CreateWindow(MonitoringService monitoring) => new(monitoring,
        new OwnedStartup(), new WifiRadioService(new NoRadio()),
        appUsageClient: new AppUsageTodayChecks.UnavailableClient(),
        appTrackingSetupService: new AppUsageSetupChecks.RunningSetupService())
        { ShowActivated = false, ShowInTaskbar = false, Opacity = 0, Width = 760, Height = 600 };

    private static T Control<T>(Window window, string name) where T : class => HistoryOverviewChecks.Control<T>(window, name);
    private static string Text(Window window, string name) => Control<TextBlock>(window, name).Text;
    private static void Cycle(MainWindow window)
    {
        typeof(Button).GetMethod("OnClick", PrivateInstance)!.Invoke(Control<Button>(window, "DashboardUsageSelectorButton"), null);
        Layout(window);
    }
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, PrivateInstance)!.GetValue(instance)!;
    private static void Layout(Window window) { window.UpdateLayout(); HistoryOverviewChecks.Pump(); }

    private sealed class OwnedClock : TimeProvider
    {
        private long _ticks = new DateTimeOffset(new DateTime(2026, 10, 13, 12, 0, 0),
            TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 13, 12, 0, 0))).UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        public void Advance(TimeSpan duration) => Interlocked.Add(ref _ticks, duration.Ticks);
    }

    private sealed class OwnedCounters(TimeProvider clock) : INetworkMonitor
    {
        private long _download;
        private long _upload;
        public bool Disconnected { get; set; }
        public int Captures { get; private set; }
        public void Advance(long download, long upload) { _download += download; _upload += upload; }
        public IReadOnlyList<AdapterInfo> GetActiveAdapters() =>
            [new(HistoryOverviewChecks.AdapterId, "Owned traffic adapter", "Deterministic traffic counters", "Ethernet", 1_000_000_000, "192.0.2.10", true)];
        public NetworkSnapshot? Capture(string adapterId)
        {
            Captures++;
            return Disconnected ? null : new(_download, _upload, clock.GetUtcNow());
        }
        public NetworkAdapterDetails? GetAdapterDetails(string adapterId) =>
            new(adapterId, OperationalStatus.Up, [IPAddress.Parse("192.0.2.10")], [], [], 1_000_000_000);
    }
    private sealed class NoTunnel : IVpnInterfaceSource { public VpnInterfaceInventory Read() => new([], []); }
    private sealed class NoRoutes : IVpnRouteSource { public VpnRouteInventory Read(AddressFamily family) => new(family, [], Availability: IpFamilyAvailability.Unavailable); }
    private sealed class NoPing : IPingSource
    {
        public Task<IcmpPingReply> SendAsync(IPAddress target, TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(new IcmpPingReply(IPStatus.TimedOut));
    }
    private sealed class NoRadio : IWifiRadioSource { public WifiRadioSnapshot Read(string adapterId) => new(adapterId, false); }
    private sealed class OwnedStartup : IWindowsStartupRegistration
    {
        public bool IsEnabled() => false;
        public void Enable() => throw new InvalidOperationException("Traffic verification cannot change startup settings.");
        public void Disable() => throw new InvalidOperationException("Traffic verification cannot change startup settings.");
    }
}
