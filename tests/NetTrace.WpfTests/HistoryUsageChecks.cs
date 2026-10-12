using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Data.Sqlite;
using NetTrace;
using NetTrace.Controls;
using NetTrace.Models;
using NetTrace.Presentation;
using NetTrace.Services;
using NetTrace.Utils;

internal static class HistoryUsageChecks
{
    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;

    public static void Run(string root, Action<bool, string> check)
    {
        var previews = System.IO.Path.Combine(root, "history-usage-previews");
        CheckStates(root, previews, check);
        CheckLifecycle(root, previews, check);
        Console.WriteLine("History usage previews: " + previews);
    }

    private static void CheckStates(string root, string previews, Action<bool, string> check)
    {
        var store = new UsageStore(System.IO.Path.Combine(root, "history-usage-states"));
        var clock = new OwnedClock();
        using var monitoring = HistoryOverviewChecks.CreateMonitoring(store, clock);
        var window = HistoryOverviewChecks.CreateWindow(monitoring);
        try
        {
            window.Show();
            HistoryOverviewChecks.StopViewTimer(window);
            HistoryOverviewChecks.SelectHistory(window);
            var chart = Control<HistoryUsageChart>(window, "HistoryUsageChart");
            var section = Control<StackPanel>(window, "HistoryUsageSection");
            var seven = Control<RadioButton>(window, "HistoryUsage7DButton");
            var thirty = Control<RadioButton>(window, "HistoryUsage30DButton");
            check(section.IsVisible && HistoryOverviewChecks.Descendants(section).OfType<TextBlock>().Any(text => text.Text == "DATA USAGE") &&
                seven.Content?.ToString() == "7D" && thirty.Content?.ToString() == "30D" &&
                seven.IsChecked == true && thirty.IsChecked == false && chart.Data.Range == HistoryUsageRange.SevenDays,
                "History exposes DATA USAGE and exclusive 7D/30D controls, defaulting to 7D");
            check(RangeIndicator(seven).A == 255 && RangeIndicator(thirty).A == 0,
                "The selected daily-usage range uses the existing accent underline language");
            check(chart.Data.Points.Count == 7 && chart.Data.Points.All(point => point.TotalBytes == 0) &&
                !chart.Data.HasUsage && chart.Data.MaximumTotalBytes == 0 && OrderedDates(chart.Data, clock) &&
                chart.Data.AxisLabels.Count == 7 && chart.PlotBounds.Width > 0 && chart.PlotBounds.Height > 0,
                "Empty 7D has seven chronological zero buckets, a valid plot and no invalid scale");
            ShowUsageSection(window);
            check(ChartGlyphs(chart).Contains("No recorded data usage in this period.", StringComparison.Ordinal) &&
                ChartGlyphs(chart).Contains("0 B", StringComparison.Ordinal),
                "The native zero-usage chart draws its lightweight message and readable baseline");
            CheckLayout(window, check, "no-usage 7D");
            HistoryOverviewChecks.SavePreview(window, System.IO.Path.Combine(previews, "760x600-no-usage-7D.png"));
            CheckTooltip(chart, 3, check, "zero-usage 7D", System.IO.Path.Combine(previews, "tooltip-zero-7D.png"));
            check(chart.GetPointAt(new Point(chart.PlotBounds.Left - 1, chart.PlotBounds.Top)) is null &&
                chart.GetPointAt(new Point(double.NaN, chart.PlotBounds.Top)) is null,
                "Daily tooltip hit testing rejects axis margins and invalid pointer coordinates");
            var legend = HistoryOverviewChecks.Descendants(section).OfType<Rectangle>().ToArray();
            check(legend.Any(shape => ReferenceEquals(shape.Fill, HistoryUsageChart.DownloadBrush)) &&
                legend.Any(shape => ReferenceEquals(shape.Fill, HistoryUsageChart.UploadBrush)) &&
                HistoryOverviewChecks.Descendants(section).OfType<TextBlock>().Any(text => text.Text == "Download") &&
                HistoryOverviewChecks.Descendants(section).OfType<TextBlock>().Any(text => text.Text == "Upload"),
                "The compact Download/Upload legend uses the same brushes as the stacked bar series");
            var downloadBrush = (SolidColorBrush)HistoryUsageChart.DownloadBrush;
            var uploadBrush = (SolidColorBrush)HistoryUsageChart.UploadBrush;
            check(downloadBrush.IsFrozen && uploadBrush.IsFrozen && downloadBrush.Color.R < downloadBrush.Color.G &&
                downloadBrush.Color.R < downloadBrush.Color.B && uploadBrush.Color.G < uploadBrush.Color.R &&
                uploadBrush.Color.G < uploadBrush.Color.B && downloadBrush.Color != uploadBrush.Color,
                "Daily byte series use distinct restrained cyan/violet measurement brushes rather than status colors");

            SelectRange(window, HistoryUsageRange.ThirtyDays);
            check(chart.Data.Points.Count == 30 && !chart.Data.HasUsage && OrderedDates(chart.Data, clock) &&
                chart.Data.AxisLabels.Count is >= 5 and <= 7 && RangeIndicator(thirty).A == 255 && RangeIndicator(seven).A == 0,
                "Selecting 30D redraws thirty zero buckets with sparse labels and the correct selected underline");
            HistoryOverviewChecks.SavePreview(window, System.IO.Path.Combine(previews, "760x600-no-usage-30D.png"));
            CheckTooltip(chart, 29, check, "zero-usage 30D", System.IO.Path.Combine(previews, "tooltip-zero-30D.png"));

            var profiles = new ProfileStore(store);
            var profile = profiles.CreateProfile("Owned historical provider with a very long descriptive name",
                "Owned long device name retained beneath the daily data chart");
            var history = new ConnectionHistoryStore(store);
            AddCompleted(history, LocalDay(clock, -6), profile.Id, 3_000_000, 1_000_000);
            AddCompleted(history, LocalDay(clock, -4), profile.Id, 761_000, 200_000);
            AddCompleted(history, LocalDay(clock, -2), profile.Id, 24_040_000, 10_000_000);
            AddCompleted(history, LocalDay(clock, 0), profile.Id, 360_000_000, 125_000_000);
            SelectRange(window, HistoryUsageRange.SevenDays);
            check(chart.Data.Points.Count == 7 && chart.Data.Points.Count(point => point.TotalBytes > 0) == 4 &&
                chart.Data.Points[1].TotalBytes == 0 && chart.Data.Points[3].TotalBytes == 0 &&
                chart.Data.Points[^1] is { DownloadBytes: 360_000_000, UploadBytes: 125_000_000 } && OrderedDates(chart.Data, clock),
                "Mixed 7D preserves the positions of zero days and places today's exact bytes at the right edge");
            check(Text(window, "HistoryTodayDownloadText") == ByteFormatter.Format(chart.Data.Points[^1].DownloadBytes) &&
                Text(window, "HistoryTodayUploadText") == ByteFormatter.Format(chart.Data.Points[^1].UploadBytes) &&
                Control<ItemsControl>(window, "HistoryRecentSessions").Items.Count == 4,
                "Daily today's bytes agree with History 001 while its four recent session rows remain present");
            ShowUsageSection(window);
            CheckStackedDrawing(chart, check);
            CheckLayout(window, check, "mixed 7D");
            HistoryOverviewChecks.SavePreview(window, System.IO.Path.Combine(previews, "760x600-mixed-7D.png"));
            CheckTooltip(chart, 6, check, "mixed-usage 7D", System.IO.Path.Combine(previews, "tooltip-mixed-7D.png"));
            window.Width = 920;
            window.Height = 740;
            Page(window).ScrollToTop();
            Layout(window);
            HistoryOverviewChecks.SavePreview(window, System.IO.Path.Combine(previews, "920x740-mixed-7D-page.png"));

            var beforeSessions = history.GetRecentSessions(1000).Count;
            var beforeEvents = history.GetRecentEvents(1000).Count;
            for (var day = -29; day < -6; day++)
                if (day % 3 != 0) AddCompleted(history, LocalDay(clock, day), null, (day + 30) * 2_000_000L, (day + 30) * 500_000L);
            beforeSessions = history.GetRecentSessions(1000).Count;
            for (var i = 0; i < 3; i++) { SelectRange(window, HistoryUsageRange.ThirtyDays); SelectRange(window, HistoryUsageRange.SevenDays); }
            check(history.GetRecentSessions(1000).Count == beforeSessions && history.GetRecentEvents(1000).Count == beforeEvents &&
                !monitoring.GetSnapshot().IsRunning,
                "Changing daily chart ranges neither modifies persisted history nor starts monitoring");
            SelectRange(window, HistoryUsageRange.ThirtyDays);
            check(chart.Data.Points.Count == 30 && chart.Data.Points.Count(point => point.TotalBytes > 0) > 7 &&
                chart.Data.AxisLabels.Count is >= 5 and <= 7 && chart.Data.AxisLabels[0].Index == 0 &&
                chart.Data.AxisLabels[^1].Index == 29 && OrderedDates(chart.Data, clock),
                "Varied 30D renders all daily buckets oldest-to-newest with readable first/intermediate/today labels");
            window.Width = 760;
            window.Height = 600;
            ShowUsageSection(window);
            CheckLayout(window, check, "varied 30D");
            HistoryOverviewChecks.SavePreview(window, System.IO.Path.Combine(previews, "760x600-varied-30D.png"));
            CheckTooltip(chart, 0, check, "varied-usage 30D", System.IO.Path.Combine(previews, "tooltip-varied-30D.png"));
            AddCompleted(history, LocalDay(clock, -1), null, 20_000_000_000, 5_000_000_000);
            RefreshUsage(window);
            check(chart.Data.MaximumTotalBytes == 25_000_000_000 && chart.Data.MaximumText == ByteFormatter.Format(25_000_000_000) &&
                chart.Data.MiddleText == ByteFormatter.Format(12_500_000_000),
                "A large GB day automatically rescales the chart with the application's existing byte formatter");
            ShowUsageSection(window);
            CheckStackedDrawing(chart, check);
            HistoryOverviewChecks.SavePreview(window, System.IO.Path.Combine(previews, "760x600-large-GB-30D.png"));
            Page(window).ScrollToEnd();
            Layout(window);
            var recentText = HistoryOverviewChecks.Descendants(Control<ItemsControl>(window, "HistoryRecentSessions"))
                .OfType<TextBlock>().Select(text => text.Text).ToArray();
            check(recentText.Any(text => text.Contains("Owned historical provider", StringComparison.Ordinal)) &&
                ButtonInViewport(window) && Page(window).ScrollableHeight > 0,
                "Long recent-session identities and View full history remain reachable below the chart by vertical scrolling");
            HistoryOverviewChecks.SavePreview(window, System.IO.Path.Combine(previews, "760x600-long-profiles-below-chart.png"));

            var cached = chart.Data;
            AddCompleted(history, LocalDay(clock, 0).AddMinutes(1), null, 100_000, 200_000);
            window.RefreshDashboard();
            HistoryOverviewChecks.Pump();
            check(ReferenceEquals(cached, chart.Data), "The ordinary view cadence reuses unchanged cached persisted chart data");
            NavigateAway(window);
            HistoryOverviewChecks.SelectHistory(window);
            check(chart.Data.Range == HistoryUsageRange.ThirtyDays && thirty.IsChecked == true &&
                chart.Data.Points[^1].DownloadBytes == 360_100_000,
                "Returning to History preserves 30D selection and immediately reloads newly persisted daily bytes");
            NavigateAway(window);
            RenameOwnedTable(store.DatabasePath, "ConnectionSessions", "OwnedHiddenUsageSessions");
            try
            {
                cached = chart.Data;
                typeof(MainWindow).GetMethod("Monitoring_OnChanged", PrivateInstance)!.Invoke(window, new object[] { monitoring, EventArgs.Empty });
                window.RefreshDashboard();
                HistoryOverviewChecks.Pump();
                check(ReferenceEquals(cached, chart.Data) && Text(window, "HistoryMessageText") == "" &&
                    Text(window, "HistoryUsageMessageText") == "",
                    "Hidden History notifications do not query unavailable owned session tables or rebuild the chart");
            }
            finally { RenameOwnedTable(store.DatabasePath, "OwnedHiddenUsageSessions", "ConnectionSessions"); }
            HistoryOverviewChecks.SelectHistory(window);
            ShowUsageSection(window);
            Hover(chart, chart.Data.Points.Count - 1);
            window.Hide();
            HistoryOverviewChecks.Pump();
            check(chart.SelectedPoint is null && chart.ToolTip is null,
                "Hiding the History window clears daily hover state and closes its tooltip popup");
            window.Show();
            HistoryOverviewChecks.StopViewTimer(window);
            Layout(window);
            window.Close();
            var recreated = HistoryOverviewChecks.CreateWindow(monitoring);
            try
            {
                recreated.Show();
                HistoryOverviewChecks.StopViewTimer(recreated);
                HistoryOverviewChecks.SelectHistory(recreated);
                check(Control<RadioButton>(recreated, "HistoryUsage30DButton").IsChecked == true &&
                    Control<HistoryUsageChart>(recreated, "HistoryUsageChart").Data.Range == HistoryUsageRange.ThirtyDays,
                    "Recreating the main window retains the app session's selected 30D range");
            }
            finally { recreated.Close(); }
            var detailed = new ConnectionHistoryWindow(history) { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
            try
            {
                detailed.Show();
                Layout(detailed);
                check(!HistoryOverviewChecks.Descendants((DependencyObject)detailed.Content).OfType<HistoryUsageChart>().Any() &&
                    Control<DataGrid>(detailed, "SessionsGrid").IsReadOnly && Control<DataGrid>(detailed, "EventsGrid").IsReadOnly &&
                    Control<DataGrid>(detailed, "SessionsGrid").Columns.Count == 7,
                    "The separate detailed history window keeps its existing tables and gains no chart or redesign");
            }
            finally { detailed.Close(); }
        }
        finally { window.Close(); }
        CheckMostlyZero(root, previews, check);
    }

    private static void CheckMostlyZero(string root, string previews, Action<bool, string> check)
    {
        var store = new UsageStore(System.IO.Path.Combine(root, "history-usage-mostly-zero"));
        var clock = new OwnedClock();
        AddCompleted(new ConnectionHistoryStore(store), LocalDay(clock, -2), null, 10_000_000, 2_000_000);
        using var monitoring = HistoryOverviewChecks.CreateMonitoring(store, clock);
        var window = HistoryOverviewChecks.CreateWindow(monitoring);
        try
        {
            window.Show();
            HistoryOverviewChecks.StopViewTimer(window);
            HistoryOverviewChecks.SelectHistory(window);
            SelectRange(window, HistoryUsageRange.ThirtyDays);
            ShowUsageSection(window);
            var chart = Control<HistoryUsageChart>(window, "HistoryUsageChart");
            check(chart.Data.Points.Count(point => point.TotalBytes > 0) == 1 && chart.Data.Points.Count(point => point.TotalBytes == 0) == 29 &&
                Enumerable.Range(0, 30).All(index => chart.GetPointAt(PointInDay(chart, index))?.LocalDate == chart.Data.Points[index].LocalDate),
                "Mostly-zero 30D retains every empty daily column as a useful tooltip target");
            HistoryOverviewChecks.SavePreview(window, System.IO.Path.Combine(previews, "760x600-mostly-zero-30D.png"));
        }
        finally { window.Close(); }
    }

    private static void CheckLifecycle(string root, string previews, Action<bool, string> check)
    {
        var store = new UsageStore(System.IO.Path.Combine(root, "history-usage-live"));
        var clock = new OwnedClock();
        AddCompleted(new ConnectionHistoryStore(store), LocalDay(clock, 0), null, 1_000_000, 500_000);
        var identity = new HistoryOverviewChecks.OwnedIdentity(new NetworkIdentity("WiFi", "Owned daily active", "Owned daily active", HistoryOverviewChecks.AdapterId));
        var counters = new HistoryOverviewChecks.OwnedCounters(clock);
        using var monitoring = HistoryOverviewChecks.CreateMonitoring(store, clock, identity, counters);
        var window = HistoryOverviewChecks.CreateWindow(monitoring);
        try
        {
            window.Show();
            HistoryOverviewChecks.StopViewTimer(window);
            HistoryOverviewChecks.SelectHistory(window);
            monitoring.Start();
            StopSampler(monitoring);
            Wait(() => monitoring.GetSnapshot().Session is not null);
            monitoring.SampleNow();
            clock.Advance(TimeSpan.FromSeconds(42));
            counters.Advance(3_000_000, 2_000_000);
            monitoring.SampleNow();
            window.RefreshDashboard();
            Layout(window);
            var chart = Control<HistoryUsageChart>(window, "HistoryUsageChart");
            check(monitoring.History.GetActiveSession() is { DownloadBytes: 3_000_000, UploadBytes: 2_000_000 } &&
                chart.Data.Points[^1] is { DownloadBytes: 4_000_000, UploadBytes: 2_500_000 } &&
                Text(window, "HistoryTodayDownloadText") == ByteFormatter.Format(4_000_000) &&
                Text(window, "HistoryTodayUploadText") == ByteFormatter.Format(2_500_000),
                "Persisted in-progress plus live active state contributes exactly once alongside completed bytes and matches Today");
            ShowUsageSection(window);
            HistoryOverviewChecks.SavePreview(window, System.IO.Path.Combine(previews, "760x600-active-7D.png"));
            var tip = Hover(chart, 6);
            tip.IsOpen = true;
            HistoryOverviewChecks.Pump();
            var selectedDate = chart.SelectedPoint!.LocalDate;
            clock.Advance(TimeSpan.FromSeconds(1));
            counters.Advance(100_000, 50_000);
            monitoring.SampleNow();
            window.RefreshDashboard();
            HistoryOverviewChecks.Pump();
            check(ReferenceEquals(tip, chart.ToolTip) && tip.IsOpen && chart.SelectedPoint?.LocalDate == selectedDate &&
                ((TextBlock)tip.Content).Text == chart.Data.Points[^1].ToolTipText &&
                chart.Data.Points[^1].DownloadBytes == 4_100_000,
                "A stationary daily tooltip stays open and updates its exact active byte values on the existing view cadence");
            SaveTooltip(tip, System.IO.Path.Combine(previews, "tooltip-active-updated.png"));
            tip.IsOpen = false;
            var viewTimer = Field<object>(window, "_timer");
            var workerNames = new[] { "_timer", "_profileMonitor", "_icmpMonitor", "_reachabilityMonitor", "_vpnSessions" };
            var workers = workerNames.Select(name => Field<object>(monitoring, name)).ToArray();
            var captures = counters.Captures;
            var sessionId = monitoring.GetSnapshot().Session!.Id;
            var recordCount = monitoring.History.GetRecentSessions().Count;
            var eventCount = monitoring.History.GetRecentEvents().Count;
            for (var i = 0; i < 4; i++) { SelectRange(window, HistoryUsageRange.ThirtyDays); SelectRange(window, HistoryUsageRange.SevenDays); }
            check(ReferenceEquals(viewTimer, Field<object>(window, "_timer")) &&
                workerNames.Select((name, index) => ReferenceEquals(workers[index], Field<object>(monitoring, name))).All(value => value) &&
                captures == counters.Captures && monitoring.GetSnapshot().Session!.Id == sessionId &&
                recordCount == monitoring.History.GetRecentSessions().Count && eventCount == monitoring.History.GetRecentEvents().Count &&
                typeof(MainWindow).GetFields(PrivateInstance).Count(field => field.FieldType == typeof(System.Windows.Threading.DispatcherTimer)) == 1 &&
                !typeof(HistoryUsageChart).GetFields(PrivateInstance).Any(field => typeof(System.Threading.Timer).IsAssignableFrom(field.FieldType)),
                "Repeated range selection reuses monitoring/view workers without extra timers, samples, sessions or events");
            var cached = chart.Data;
            NavigateAway(window);
            clock.Advance(TimeSpan.FromSeconds(1));
            counters.Advance(100_000, 50_000);
            monitoring.SampleNow();
            window.RefreshDashboard();
            HistoryOverviewChecks.Pump();
            check(ReferenceEquals(cached, chart.Data), "Active usage continues while hidden History avoids chart data and rendering work");
            HistoryOverviewChecks.SelectHistory(window);
            check(chart.Data.Points[^1].DownloadBytes == 4_200_000,
                "Entering History immediately renders current active daily bytes without waiting for another monitoring cycle");
            ShowUsageSection(window);
            Hover(chart, 6);
            SelectRange(window, HistoryUsageRange.ThirtyDays);
            check(chart.SelectedPoint is null && chart.ToolTip is null,
                "Changing range clears the former daily hover target instead of showing a mismatched tooltip");
            Hover(chart, 29);
            clock.Advance(TimeSpan.FromHours(11));
            counters.Advance(200_000, 100_000);
            monitoring.SampleNow();
            window.RefreshDashboard();
            HistoryOverviewChecks.Pump();
            check(chart.Data.Points[^1].TotalBytes == 0 && chart.Data.Points[^2].DownloadBytes == 4_400_000 &&
                chart.Data.Points[^2].UploadBytes == 2_700_000 && Text(window, "HistoryTodayDownloadText") == ByteFormatter.Format(0) &&
                Text(window, "HistoryTodayUploadText") == ByteFormatter.Format(0),
                "After local midnight active bytes remain on their start day and Today/chart today's usage both stay zero");
            check(chart.SelectedPoint is null && chart.ToolTip is null,
                "Local midnight clears the old hovered column when dates shift across the chart range");
            window.WindowState = WindowState.Minimized;
            var minimized = chart.Data;
            clock.Advance(TimeSpan.FromSeconds(1));
            counters.Advance(10_000, 10_000);
            monitoring.SampleNow();
            window.RefreshDashboard();
            HistoryOverviewChecks.Pump();
            check(ReferenceEquals(minimized, chart.Data) && chart.GetPointAt(PointInDay(chart, 29)) is null,
                "Minimized History suppresses chart refresh and daily tooltip hit testing while monitoring continues");
            window.WindowState = WindowState.Normal;
            HistoryOverviewChecks.StopViewTimer(window);
            Layout(window);
            check(chart.Data.Points[^2].DownloadBytes == 4_410_000,
                "Restoring History refreshes the active start-day contribution immediately");
        }
        finally { window.Close(); }
    }

    private static void CheckStackedDrawing(HistoryUsageChart chart, Action<bool, string> check)
    {
        chart.InvalidateVisual();
        chart.UpdateLayout();
        HistoryOverviewChecks.Pump();
        var rectangles = Drawings(VisualTreeHelper.GetDrawing(chart)).OfType<GeometryDrawing>()
            .Where(drawing => drawing.Geometry is RectangleGeometry &&
                (ReferenceEquals(drawing.Brush, HistoryUsageChart.DownloadBrush) || ReferenceEquals(drawing.Brush, HistoryUsageChart.UploadBrush)))
            .ToArray();
        check(rectangles.Any(drawing => ReferenceEquals(drawing.Brush, HistoryUsageChart.DownloadBrush)) &&
            rectangles.Any(drawing => ReferenceEquals(drawing.Brush, HistoryUsageChart.UploadBrush)) &&
            rectangles.All(drawing => chart.PlotBounds.Contains(drawing.Geometry.Bounds)),
            "Native stacked daily rectangles use both measurement series and stay inside the autoscaled plot");
    }

    private static void CheckTooltip(HistoryUsageChart chart, int index, Action<bool, string> check, string state, string path)
    {
        var point = chart.Data.Points[index];
        var tip = Hover(chart, index);
        var text = (TextBlock)tip.Content;
        check(chart.GetPointAt(PointInDay(chart, index))?.LocalDate == point.LocalDate && chart.SelectedPoint?.LocalDate == point.LocalDate &&
            text.Text == point.ToolTipText && text.Text.Contains(point.LocalDate.ToString("MMM d, yyyy", CultureInfo.InvariantCulture), StringComparison.Ordinal) &&
            (point.TotalBytes == 0 ? text.Text.Contains("No recorded data usage", StringComparison.Ordinal) :
                text.Text.Contains("Download: " + ByteFormatter.Format(point.DownloadBytes), StringComparison.Ordinal) &&
                text.Text.Contains("Upload: " + ByteFormatter.Format(point.UploadBytes), StringComparison.Ordinal) &&
                text.Text.Contains("Total: " + ByteFormatter.Format(point.TotalBytes), StringComparison.Ordinal)) &&
            text.TextWrapping == TextWrapping.Wrap && double.IsFinite(text.MaxWidth) && text.MaxWidth > 0 && double.IsFinite(tip.MaxWidth),
            $"The {state} tooltip exposes its exact local date and bytes with finite wrapping");
        tip.PlacementTarget = chart;
        tip.IsOpen = true;
        HistoryOverviewChecks.Pump();
        check(tip.IsVisible && text.ActualWidth <= text.MaxWidth + 0.5,
            $"The {state} details open as a compact native WPF tooltip");
        SaveTooltip(tip, path);
        tip.IsOpen = false;
    }

    internal static ToolTip Hover(HistoryUsageChart chart, int index)
    {
        typeof(HistoryUsageChart).GetMethod("UpdateHover", PrivateInstance)!.Invoke(chart, new object[] { PointInDay(chart, index) });
        return chart.ToolTip as ToolTip ?? throw new InvalidOperationException("Daily chart hover did not create a tooltip.");
    }
    private static Point PointInDay(HistoryUsageChart chart, int index) => new(
        chart.PlotBounds.Left + chart.PlotBounds.Width * (index + 0.5) / chart.Data.Points.Count,
        chart.PlotBounds.Top + chart.PlotBounds.Height / 2);
    internal static void ShowUsageSection(MainWindow window)
    {
        Layout(window);
        var page = Page(window);
        var section = Control<StackPanel>(window, "HistoryUsageSection");
        var top = section.TransformToAncestor(page).Transform(new Point()).Y + page.VerticalOffset;
        page.ScrollToVerticalOffset(top);
        Layout(window);
    }
    internal static void SaveTooltip(ToolTip tip, string path)
    {
        tip.UpdateLayout();
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(tip.ActualWidth), (int)Math.Ceiling(tip.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(tip);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = System.IO.File.Create(path);
        encoder.Save(output);
    }
    private static void CheckLayout(MainWindow window, Action<bool, string> check, string state)
    {
        var page = Page(window);
        var chart = Control<HistoryUsageChart>(window, "HistoryUsageChart");
        var bounds = chart.TransformToAncestor(page).TransformBounds(new Rect(0, 0, chart.ActualWidth, chart.ActualHeight));
        check(page.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled && page.ScrollableWidth < 0.01 &&
            bounds.Left >= 0 && bounds.Right <= page.ActualWidth + 0.5 && bounds.Top >= -0.5 && bounds.Bottom <= page.ActualHeight + 0.5 &&
            page.VerticalScrollBarVisibility == ScrollBarVisibility.Auto,
            $"The {state} chart is fully reachable at 760x600 with vertical scrolling and no horizontal overflow");
    }
    private static bool ButtonInViewport(MainWindow window)
    {
        var button = Control<Button>(window, "ViewHistoryButton");
        var bounds = button.TransformToAncestor(Page(window)).TransformBounds(new Rect(0, 0, button.ActualWidth, button.ActualHeight));
        return bounds.Top >= -0.5 && bounds.Bottom <= Page(window).ActualHeight + 0.5;
    }
    private static bool OrderedDates(HistoryUsageChartData data, TimeProvider clock)
    {
        var today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
        return data.Points.Select(point => point.LocalDate).SequenceEqual(Enumerable.Range(1 - data.Points.Count, data.Points.Count).Select(today.AddDays));
    }
    private static Color RangeIndicator(RadioButton button) => ((SolidColorBrush)((Border)button.Template.FindName("RangeIndicator", button)).Background).Color;
    private static string ChartGlyphs(HistoryUsageChart chart) => string.Concat(Drawings(VisualTreeHelper.GetDrawing(chart)).OfType<GlyphRunDrawing>()
        .Select(drawing => drawing.GlyphRun.Characters is { } characters ? new string(characters.ToArray()) : ""));
    private static IEnumerable<Drawing> Drawings(Drawing? root)
    {
        if (root is null) yield break;
        yield return root;
        if (root is DrawingGroup group)
            foreach (var child in group.Children)
                foreach (var drawing in Drawings(child)) yield return drawing;
    }
    internal static void SelectRange(MainWindow window, HistoryUsageRange range)
    {
        Control<RadioButton>(window, range == HistoryUsageRange.SevenDays ? "HistoryUsage7DButton" : "HistoryUsage30DButton").IsChecked = true;
        Layout(window);
    }
    private static void RefreshUsage(MainWindow window) => typeof(MainWindow).GetMethod("RefreshHistoryUsage", PrivateInstance)!.Invoke(window, new object[] { true });
    private static void NavigateAway(MainWindow window)
    {
        Control<TabControl>(window, "MainNavigation").SelectedItem = Control<TabItem>(window, "SettingsTab");
        HistoryOverviewChecks.Pump();
    }
    private static DateTimeOffset LocalDay(TimeProvider clock, int days) => new(clock.GetLocalNow().Date.AddDays(days).AddHours(10),
        TimeZoneInfo.Local.GetUtcOffset(clock.GetLocalNow().Date.AddDays(days).AddHours(10)));
    private static void AddCompleted(ConnectionHistoryStore history, DateTimeOffset start, long? profileId, long download, long upload)
    {
        var session = history.Transition(null, start, null, 0, 0,
            new NetworkIdentity("WiFi", "Owned daily network", "Owned daily network " + new string('W', 70), HistoryOverviewChecks.AdapterId),
            profileId, start, null, null)!;
        history.Transition(session.Id, start.AddMinutes(10), "ApplicationExit", download, upload, null, null, start.AddMinutes(10), null, null);
    }
    private static void RenameOwnedTable(string path, string from, string to)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"ALTER TABLE {from} RENAME TO {to};";
        command.ExecuteNonQuery();
    }
    private static T Control<T>(Window window, string name) where T : class => HistoryOverviewChecks.Control<T>(window, name);
    private static string Text(Window window, string name) => Control<TextBlock>(window, name).Text;
    private static ScrollViewer Page(MainWindow window) => (ScrollViewer)Control<TabItem>(window, "HistoryTab").Content;
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, PrivateInstance)!.GetValue(instance)!;
    private static void StopSampler(MonitoringService monitoring) => Field<System.Threading.Timer>(monitoring, "_timer").Change(Timeout.Infinite, Timeout.Infinite);
    private static void Layout(Window window) { window.UpdateLayout(); HistoryOverviewChecks.Pump(); }
    private static void Wait(Func<bool> ready)
    {
        var watch = Stopwatch.StartNew();
        while (!ready())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Owned daily usage fixture was not ready.");
            HistoryOverviewChecks.Pump();
        }
        HistoryOverviewChecks.Pump();
    }
    private sealed class OwnedClock : TimeProvider
    {
        private long _ticks = new DateTimeOffset(new DateTime(2026, 10, 12, 14, 0, 0),
            TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 12))).UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        public void Advance(TimeSpan duration) => Interlocked.Add(ref _ticks, duration.Ticks);
    }
}
