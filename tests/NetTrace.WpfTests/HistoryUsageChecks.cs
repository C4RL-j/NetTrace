using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
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
        CheckSingleSeries(previews, check);
        CheckRealPointerHover(previews, check);
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
            CheckHoverDaySelection(chart, check);
            CheckHoverOutline(chart, 6, check, "mixed-usage 7D", System.IO.Path.Combine(previews, "hover-outline-7D.png"));
            CheckHoverOutline(chart, 4, check, "smaller mixed-usage 7D");
            CheckHoverTransitions(chart, 6, 4, 3, check);
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
            CheckHoverDaySelection(chart, check);
            CheckTooltip(chart, 0, check, "varied-usage 30D", System.IO.Path.Combine(previews, "tooltip-varied-30D.png"));
            CheckHoverOutline(chart, 0, check, "varied-usage 30D", System.IO.Path.Combine(previews, "hover-outline-30D.png"));
            CheckHoverTransitions(chart, 29, 0, 24, check);
            AddCompleted(history, LocalDay(clock, -1), null, 20_000_000_000, 5_000_000_000);
            RefreshUsage(window);
            check(chart.Data.MaximumTotalBytes == 25_000_000_000 && chart.Data.MaximumText == ByteFormatter.Format(25_000_000_000) &&
                chart.Data.MiddleText == ByteFormatter.Format(12_500_000_000),
                "A large GB day automatically rescales the chart with the application's existing byte formatter");
            ShowUsageSection(window);
            CheckStackedDrawing(chart, check);
            CheckHoverOutline(chart, 28, check, "large-GB 30D");
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
            CheckTooltip(chart, 0, check, "empty day among populated 30D", System.IO.Path.Combine(previews, "tooltip-empty-day-30D.png"));
            CheckTooltip(chart, 27, check, "populated day among mostly-zero 30D", System.IO.Path.Combine(previews, "tooltip-mostly-zero-30D.png"));
            CheckHoverOutline(chart, 27, check, "mostly-zero 30D");
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
            check(tip.IsOpen, "Hovering live daily usage opens its tooltip immediately without a delayed tooltip service");
            HistoryOverviewChecks.Pump();
            var selectedDate = chart.SelectedPoint!.LocalDate;
            clock.Advance(TimeSpan.FromSeconds(1));
            counters.Advance(100_000, 50_000);
            monitoring.SampleNow();
            window.RefreshDashboard();
            HistoryOverviewChecks.Pump();
            check(ReferenceEquals(tip, chart.ToolTip) && tip.IsOpen && chart.SelectedPoint?.LocalDate == selectedDate &&
                TooltipHasValues(tip, chart.Data.Points[^1]) &&
                chart.Data.Points[^1].DownloadBytes == 4_100_000,
                "A stationary daily tooltip stays open and updates its exact active byte values on the existing view cadence");
            SaveTooltip(tip, System.IO.Path.Combine(previews, "tooltip-active-updated.png"));
            Leave(chart);
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
        var grid = tip.Content as Grid;
        var texts = grid?.Children.OfType<TextBlock>().ToArray() ?? [];
        var header = texts.SingleOrDefault(text => Grid.GetRow(text) == 0);
        check(tip.IsOpen && chart.GetPointAt(PointInDay(chart, index))?.LocalDate == point.LocalDate && chart.SelectedPoint?.LocalDate == point.LocalDate &&
            header?.Text == point.LocalDate.ToString("ddd, MMM d, yyyy", CultureInfo.InvariantCulture),
            $"The {state} hover selects the exact daily column and immediately opens its day/date tooltip");
        check(TooltipHasValues(tip, point) && point.TotalBytes == point.DownloadBytes + point.UploadBytes,
            $"The {state} tooltip formats exact Download, Upload and their summed Total with the existing byte formatter");
        var download = TooltipLabel(tip, "Download");
        var upload = TooltipLabel(tip, "Upload");
        var total = TooltipLabel(tip, "Total");
        check(ReferenceEquals(download?.Foreground, HistoryUsageChart.DownloadBrush) &&
            ReferenceEquals(upload?.Foreground, HistoryUsageChart.UploadBrush) &&
            texts.Where(text => !ReferenceEquals(text, download) && !ReferenceEquals(text, upload))
                .All(text => text.Foreground is SolidColorBrush brush && brush.Color.R >= 240 && brush.Color.G >= 240 && brush.Color.B >= 240) &&
            total is not null,
            $"The {state} tooltip colors only Download/Upload labels with the unchanged chart brushes and keeps Total and values neutral");
        check(grid is not null && grid.RowDefinitions.Count == 4 && grid.ColumnDefinitions.Count == 2 && texts.Length == 7 &&
            header is not null && Grid.GetColumnSpan(header) == 2 && double.IsFinite(grid.MaxWidth) && grid.MaxWidth is > 0 and <= 250 &&
            double.IsFinite(tip.MaxWidth) && tip.MaxWidth <= 274 &&
            tip.Background is SolidColorBrush background && background.Color.R < 40 && background.Color.G < 40 && background.Color.B < 70 &&
            tip.BorderThickness == new Thickness(1),
            $"The {state} tooltip uses a compact aligned grid and native NetTrace dark styling");
        check(ReferenceEquals(tip.PlacementTarget, chart) && tip.Placement == PlacementMode.MousePoint && !tip.IsHitTestVisible &&
            !ToolTipService.GetIsEnabled(chart),
            $"The {state} chart owns mouse-point tooltip placement and avoids delayed service or popup hover interception");
        HistoryOverviewChecks.Pump();
        check(tip.IsVisible && grid is not null && grid.ActualWidth <= grid.MaxWidth + 0.5 && tip.ActualWidth <= tip.MaxWidth + 0.5,
            $"The {state} details open as a compact native WPF tooltip");
        if (point.TotalBytes == 0)
            check(WhiteOutlines(chart).Length == 0 && TooltipHasValues(tip, point),
                $"The {state} hover shows three zero amounts without drawing a phantom outlined bar");
        SaveTooltip(tip, path);
        Leave(chart);
        check(chart.SelectedPoint is null && chart.ToolTip is null && !tip.IsOpen,
            $"Leaving the {state} day immediately clears selection and closes the tooltip");
    }

    private static bool TooltipHasValues(ToolTip tip, DailyUsagePoint point) =>
        TooltipValue(tip, "Download")?.Text == ByteFormatter.Format(point.DownloadBytes) &&
        TooltipValue(tip, "Upload")?.Text == ByteFormatter.Format(point.UploadBytes) &&
        TooltipValue(tip, "Total")?.Text == ByteFormatter.Format(point.TotalBytes);

    private static TextBlock? TooltipLabel(ToolTip tip, string label) => (tip.Content as Grid)?.Children
        .OfType<TextBlock>().SingleOrDefault(text => text.Text == label && Grid.GetColumn(text) == 0);

    private static TextBlock? TooltipValue(ToolTip tip, string label)
    {
        var rowLabel = TooltipLabel(tip, label);
        return rowLabel is null ? null : ((Grid)tip.Content).Children.OfType<TextBlock>()
            .SingleOrDefault(text => Grid.GetRow(text) == Grid.GetRow(rowLabel) && Grid.GetColumn(text) == 1);
    }

    private static void CheckHoverDaySelection(HistoryUsageChart chart, Action<bool, string> check)
    {
        var data = chart.Data;
        var plot = chart.PlotBounds;
        var maximum = data.MaximumTotalBytes;
        var values = data.Points.Select(point => (point.LocalDate, point.DownloadBytes, point.UploadBytes)).ToArray();
        var correct = Enumerable.Range(0, data.Points.Count).All(index =>
        {
            var tip = Hover(chart, index);
            return tip.IsOpen && chart.SelectedPoint?.LocalDate == data.Points[index].LocalDate && TooltipHasValues(tip, data.Points[index]);
        });
        check(correct && ReferenceEquals(data, chart.Data) && chart.PlotBounds == plot && chart.Data.MaximumTotalBytes == maximum &&
            chart.Data.Points.Select(point => (point.LocalDate, point.DownloadBytes, point.UploadBytes)).SequenceEqual(values),
            $"Hover visits each of the {data.Points.Count} chronological days with its own values while data and scale remain unchanged");
        Leave(chart);
    }

    private static void CheckHoverTransitions(HistoryUsageChart chart, int first, int second, int zero, Action<bool, string> check)
    {
        Leave(chart);
        var before = RenderChart(chart);
        var data = chart.Data;
        var plot = chart.PlotBounds;
        var tip = Hover(chart, first);
        var openedImmediately = tip.IsOpen;
        HistoryOverviewChecks.Pump();
        var firstOutline = WhiteOutlines(chart).SingleOrDefault()?.Geometry.Bounds;
        var next = Hover(chart, second);
        RenderChart(chart);
        var nextOutlines = WhiteOutlines(chart);
        check(openedImmediately && ReferenceEquals(tip, next) && tip.IsOpen && TooltipHasValues(tip, data.Points[second]) &&
            chart.SelectedPoint?.LocalDate == data.Points[second].LocalDate && nextOutlines.Length == 1 &&
            nextOutlines[0].Geometry.Bounds != firstOutline,
            $"Moving between populated {data.Points.Count}D columns updates the open tooltip and keeps only the new day's white outline");
        var empty = Hover(chart, zero);
        var zeroRendered = RenderChart(chart);
        check(data.Points[zero].TotalBytes == 0 && ReferenceEquals(tip, empty) && tip.IsOpen &&
            chart.SelectedPoint?.LocalDate == data.Points[zero].LocalDate && TooltipHasValues(tip, data.Points[zero]) &&
            WhiteOutlines(chart).Length == 0 && BitmapPixels(before).SequenceEqual(BitmapPixels(zeroRendered)),
            $"Moving from a populated {data.Points.Count}D column to a zero day shows zero values and removes all outline pixels");
        typeof(HistoryUsageChart).GetMethod("UpdateHover", PrivateInstance)!
            .Invoke(chart, new object[] { new Point(plot.Left - 1, plot.Top + plot.Height / 2) });
        check(chart.SelectedPoint is null && chart.ToolTip is null && !tip.IsOpen && ReferenceEquals(data, chart.Data) && chart.PlotBounds == plot &&
            BitmapPixels(before).SequenceEqual(BitmapPixels(RenderChart(chart))),
            $"Moving into the {data.Points.Count}D axis margin closes the tooltip and leaves chart bytes, scale and rendering unchanged");
    }

    private static void CheckSingleSeries(string previews, Action<bool, string> check)
    {
        var now = new DateTimeOffset(2026, 10, 12, 14, 0, 0, TimeSpan.Zero);
        ConnectionSession[] sessions =
        [
            new(1, null, "WiFi", "Owned", "Owned", "Owned", now.AddDays(-1), now.AddDays(-1).AddMinutes(10),
                now.AddDays(-1).AddMinutes(10), "ApplicationExit", 10_000_000, 0, null),
            new(2, null, "WiFi", "Owned", "Owned", "Owned", now.AddMinutes(-10), now, now,
                "ApplicationExit", 0, 20_000_000, null),
        ];
        var chart = new HistoryUsageChart { Width = 680, Height = 180 };
        var window = new Window { Content = chart, SizeToContent = SizeToContent.WidthAndHeight,
            ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
        try
        {
            window.Show();
            Layout(window);
            foreach (var range in new[] { HistoryUsageRange.SevenDays, HistoryUsageRange.ThirtyDays })
            {
                chart.UpdateData(HistoryUsageChartData.Create(sessions, null, now, TimeZoneInfo.Utc, range));
                RenderChart(chart);
                var count = chart.Data.Points.Count;
                check(chart.Data.Points[count - 2] is { DownloadBytes: 10_000_000, UploadBytes: 0 } &&
                    chart.Data.Points[count - 1] is { DownloadBytes: 0, UploadBytes: 20_000_000 },
                    $"The {count}D single-series fixture includes a real download-only and upload-only day");
                CheckHoverOutline(chart, count - 2, check, $"download-only {count}D");
                CheckHoverOutline(chart, count - 1, check, $"upload-only {count}D");
                CheckTooltip(chart, count - 2, check, $"download-only {count}D", System.IO.Path.Combine(previews, $"tooltip-download-only-{count}D.png"));
                CheckTooltip(chart, count - 1, check, $"upload-only {count}D", System.IO.Path.Combine(previews, $"tooltip-upload-only-{count}D.png"));
            }
        }
        finally { window.Close(); }
    }

    private static void CheckRealPointerHover(string previews, Action<bool, string> check)
    {
        if (!GetCursorPos(out var originalPointer))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Unable to save the pointer for native History hover verification.");

        var now = new DateTimeOffset(2026, 10, 12, 14, 0, 0, TimeSpan.Zero);
        ConnectionSession[] sessions =
        [
            new(1, null, "WiFi", "Owned", "Owned", "Owned", now.AddDays(-1), now.AddDays(-1).AddMinutes(10),
                now.AddDays(-1).AddMinutes(10), "ApplicationExit", 10_000_000, 2_000_000, null),
            new(2, null, "WiFi", "Owned", "Owned", "Owned", now.AddMinutes(-10), now, now,
                "ApplicationExit", 24_300_000_000, 2_100_000_000, null),
        ];
        var chart = new HistoryUsageChart { Width = 680, Height = 180 };
        var window = new Window
        {
            Content = chart, SizeToContent = SizeToContent.WidthAndHeight,
            ShowActivated = false, ShowInTaskbar = false, Topmost = true, Opacity = 1,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            Left = 80, Top = 80, Background = new SolidColorBrush(Color.FromRgb(15, 23, 42)),
        };
        try
        {
            window.Show();
            Layout(window);
            foreach (var range in new[] { HistoryUsageRange.SevenDays, HistoryUsageRange.ThirtyDays })
            {
                chart.UpdateData(HistoryUsageChartData.Create(sessions, null, now, TimeZoneInfo.Utc, range));
                RenderChart(chart);
                var data = chart.Data;
                var count = data.Points.Count;
                var plot = chart.PlotBounds;
                var margin = new Point(plot.Left - 8, plot.Top + plot.Height / 2);
                MovePointer(chart, margin);
                Wait(() => chart.IsMouseOver && chart.SelectedPoint is null && chart.ToolTip is null);
                check(chart.IsMouseOver && chart.SelectedPoint is null && chart.ToolTip is null,
                    $"Native pointer entry into the {count}D chart margin has no day tooltip before crossing into the plot");

                // Stay inside the same chart element when entering a day column:
                // delayed ToolTipService discovery previously missed this move.
                var firstIndex = count - 2;
                MovePointer(chart, new Point(PointInDay(chart, firstIndex).X, plot.Top + 8));
                Wait(() => chart.IsMouseOver && chart.SelectedPoint?.LocalDate == data.Points[firstIndex].LocalDate &&
                    chart.ToolTip is ToolTip { IsOpen: true, IsVisible: true });
                var firstTip = (ToolTip)chart.ToolTip!;
                check(VisibleTooltipHasValues(firstTip, data.Points[firstIndex]) && WhiteOutlines(chart).Length == 1,
                    $"Real {count}D mouse movement from margin to a short day's column opens visible Download/Upload/Total rows and its outline");
                SaveTooltip(firstTip, System.IO.Path.Combine(previews, $"tooltip-native-short-{count}D.png"));

                MovePointer(chart, PointInDay(chart, count - 1));
                Wait(() => chart.IsMouseOver && chart.SelectedPoint?.LocalDate == data.Points[^1].LocalDate &&
                    chart.ToolTip is ToolTip { IsOpen: true, IsVisible: true });
                var nextTip = (ToolTip)chart.ToolTip!;
                check(ReferenceEquals(firstTip, nextTip) && VisibleTooltipHasValues(nextTip, data.Points[^1]) &&
                    WhiteOutlines(chart).Length == 1,
                    $"Real {count}D mouse movement between days keeps the popup visible and shows the next day's exact Download/Upload/Total");
                SaveTooltip(nextTip, System.IO.Path.Combine(previews, $"tooltip-native-GB-{count}D.png"));

                MovePointer(chart, PointInDay(chart, 0));
                Wait(() => chart.IsMouseOver && chart.SelectedPoint?.LocalDate == data.Points[0].LocalDate &&
                    chart.ToolTip is ToolTip { IsOpen: true, IsVisible: true });
                var zeroTip = (ToolTip)chart.ToolTip!;
                check(VisibleTooltipHasValues(zeroTip, data.Points[0]) && data.Points[0].TotalBytes == 0 && WhiteOutlines(chart).Length == 0,
                    $"Real {count}D mouse movement to a zero day shows visible zero amounts and removes the previous outline");

                MovePointer(chart, margin);
                Wait(() => chart.IsMouseOver && chart.SelectedPoint is null && chart.ToolTip is null && !zeroTip.IsOpen);
                check(chart.IsMouseOver && chart.SelectedPoint is null && chart.ToolTip is null && !zeroTip.IsOpen && WhiteOutlines(chart).Length == 0,
                    $"Real {count}D mouse movement back into the axis margin closes the popup and clears its outline");

                MovePointer(chart, PointInDay(chart, count - 1));
                Wait(() => chart.IsMouseOver && chart.SelectedPoint?.LocalDate == data.Points[^1].LocalDate &&
                    chart.ToolTip is ToolTip { IsOpen: true, IsVisible: true });
                var reopened = (ToolTip)chart.ToolTip!;
                check(VisibleTooltipHasValues(reopened, data.Points[^1]) && ReferenceEquals(data, chart.Data) && chart.PlotBounds == plot,
                    $"Real {count}D pointer re-entry reopens all usage rows without changing chart data or scale");

                MovePointer(chart, new Point(chart.ActualWidth + 12, chart.ActualHeight / 2));
                Wait(() => !chart.IsMouseOver && chart.SelectedPoint is null && chart.ToolTip is null && !reopened.IsOpen);
                check(!chart.IsMouseOver && chart.SelectedPoint is null && chart.ToolTip is null && !reopened.IsOpen && WhiteOutlines(chart).Length == 0,
                    $"Real pointer leave from the {count}D chart immediately closes the popup and removes its white outline");
            }

            MovePointer(chart, PointInDay(chart, chart.Data.Points.Count - 1));
            Wait(() => chart.ToolTip is ToolTip { IsOpen: true, IsVisible: true });
            var visibleTip = (ToolTip)chart.ToolTip!;
            window.WindowState = WindowState.Minimized;
            HistoryOverviewChecks.Pump();
            check(chart.SelectedPoint is null && chart.ToolTip is null && !visibleTip.IsOpen,
                "Minimizing the actual native-pointer History fixture closes its visible popup and clears hover state");
            window.WindowState = WindowState.Normal;
            Layout(window);
            check(SeriesRectangles(chart).Length == 4,
                "Restoring the native History fixture redraws the unchanged stacked bars before new pointer input");
            MovePointer(chart, new Point(chart.PlotBounds.Left - 8, chart.PlotBounds.Top + chart.PlotBounds.Height / 2));
            MovePointer(chart, PointInDay(chart, chart.Data.Points.Count - 1));
            Wait(() => chart.IsMouseOver && chart.ToolTip is ToolTip { IsOpen: true, IsVisible: true });
            var restoredTip = (ToolTip)chart.ToolTip!;
            check(VisibleTooltipHasValues(restoredTip, chart.Data.Points[^1]),
                "Real pointer movement after restoring the History fixture reopens its visible usage tooltip");
            window.Content = null;
            HistoryOverviewChecks.Pump();
            check(chart.SelectedPoint is null && chart.ToolTip is null && !restoredTip.IsOpen,
                "Unloading the actual native-pointer chart closes its popup and clears hover state");
            window.Content = chart;
            Layout(window);
            MovePointer(chart, new Point(chart.PlotBounds.Left - 8, chart.PlotBounds.Top + chart.PlotBounds.Height / 2));
            MovePointer(chart, PointInDay(chart, chart.Data.Points.Count - 1));
            Wait(() => chart.IsMouseOver && chart.ToolTip is ToolTip { IsOpen: true, IsVisible: true });
            visibleTip = (ToolTip)chart.ToolTip!;
            check(VisibleTooltipHasValues(visibleTip, chart.Data.Points[^1]),
                "Real pointer movement after reloading the History chart reopens all visible usage rows");
            window.Hide();
            HistoryOverviewChecks.Pump();
            check(chart.SelectedPoint is null && chart.ToolTip is null && !visibleTip.IsOpen,
                "Hiding the actual native-pointer History fixture closes its visible popup and clears hover state");
        }
        finally
        {
            window.Close();
            if (!SetCursorPos(originalPointer.X, originalPointer.Y))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Unable to restore the pointer after native History hover verification.");
            HistoryOverviewChecks.Pump();
        }
    }

    private static bool VisibleTooltipHasValues(ToolTip tip, DailyUsagePoint point) =>
        tip.IsOpen && tip.IsVisible && TooltipHasValues(tip, point) && tip.Content is Grid grid &&
        grid.Children.OfType<TextBlock>().All(text => text.IsVisible && text.ActualWidth > 0 && text.ActualHeight > 0);

    private static void MovePointer(HistoryUsageChart chart, Point position)
    {
        var screen = chart.PointToScreen(position);
        if (!SetCursorPos((int)Math.Round(screen.X), (int)Math.Round(screen.Y)))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Unable to move the pointer for native History hover verification.");
        HistoryOverviewChecks.Pump();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScreenPoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out ScreenPoint point);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    private static void CheckHoverOutline(HistoryUsageChart chart, int index, Action<bool, string> check, string state, string? path = null)
    {
        Leave(chart);
        var data = chart.Data;
        var plot = chart.PlotBounds;
        var before = RenderChart(chart);
        var fills = SeriesRectangles(chart);
        var center = PointInDay(chart, index).X;
        var selectedFills = fills.Where(drawing => center >= drawing.Geometry.Bounds.Left && center <= drawing.Geometry.Bounds.Right).ToArray();
        var stack = selectedFills.Aggregate(Rect.Empty, (bounds, drawing) => Rect.Union(bounds, drawing.Geometry.Bounds));
        var tip = Hover(chart, index);
        var openedImmediately = tip.IsOpen;
        var hovered = RenderChart(chart);
        var outlines = WhiteOutlines(chart);
        check(!stack.IsEmpty && outlines.Length == 1 && outlines[0].Brush is null && SameBounds(outlines[0].Geometry.Bounds, stack) &&
            outlines[0].Pen is { Thickness: 1 } && outlines[0].Pen.Brush is SolidColorBrush outlineBrush && outlineBrush.Color == Colors.White,
            $"Hovering {state} draws one thin white unfilled outline around the complete Download/Upload stack");
        check(SeriesRectangles(chart).Select(drawing => (drawing.Brush, drawing.Geometry.Bounds))
                .SequenceEqual(fills.Select(drawing => (drawing.Brush, drawing.Geometry.Bounds))) &&
            ReferenceEquals(data, chart.Data) && chart.PlotBounds == plot,
            $"The {state} outline preserves every existing series fill, bar geometry, data snapshot and scale");
        check(!stack.IsEmpty && PixelChangesTraceOutline(before, hovered, stack),
            $"The {state} rendered white outline reaches all four stack edges and changes only their pixels");
        if (path is not null) SaveBitmap(hovered, path);
        HistoryOverviewChecks.Pump();
        Leave(chart);
        check(openedImmediately && chart.SelectedPoint is null && chart.ToolTip is null && !tip.IsOpen,
            $"The {state} mouse-leave handler immediately removes hover state and closes its tooltip");
        var after = RenderChart(chart);
        check(WhiteOutlines(chart).Length == 0 && BitmapPixels(before).SequenceEqual(BitmapPixels(after)),
            $"The {state} white outline disappears on leave and the rendered chart exactly returns to its original fills");
    }

    private static GeometryDrawing[] SeriesRectangles(HistoryUsageChart chart) => Drawings(VisualTreeHelper.GetDrawing(chart))
        .OfType<GeometryDrawing>().Where(drawing => drawing.Geometry is RectangleGeometry &&
            (ReferenceEquals(drawing.Brush, HistoryUsageChart.DownloadBrush) || ReferenceEquals(drawing.Brush, HistoryUsageChart.UploadBrush))).ToArray();

    private static GeometryDrawing[] WhiteOutlines(HistoryUsageChart chart) => Drawings(VisualTreeHelper.GetDrawing(chart))
        .OfType<GeometryDrawing>().Where(drawing => drawing.Geometry is RectangleGeometry &&
            drawing.Pen?.Brush is SolidColorBrush brush && brush.Color == Colors.White).ToArray();

    private static bool SameBounds(Rect first, Rect second) =>
        Math.Abs(first.Left - second.Left) < 0.000001 && Math.Abs(first.Top - second.Top) < 0.000001 &&
        Math.Abs(first.Width - second.Width) < 0.000001 && Math.Abs(first.Height - second.Height) < 0.000001;

    private static bool PixelChangesTraceOutline(RenderTargetBitmap before, RenderTargetBitmap hovered, Rect stack)
    {
        var original = BitmapPixels(before);
        var selected = BitmapPixels(hovered);
        var edges = new bool[4];
        var changed = 0;
        var boundary = stack;
        boundary.Inflate(1.5, 1.5);
        for (var y = 0; y < before.PixelHeight; y++)
            for (var x = 0; x < before.PixelWidth; x++)
            {
                var offset = (y * before.PixelWidth + x) * 4;
                if (original.AsSpan(offset, 4).SequenceEqual(selected.AsSpan(offset, 4))) continue;
                changed++;
                var position = new Point(x + 0.5, y + 0.5);
                if (!boundary.Contains(position)) return false;
                var left = Math.Abs(position.X - stack.Left) <= 1.5;
                var right = Math.Abs(position.X - stack.Right) <= 1.5;
                var top = Math.Abs(position.Y - stack.Top) <= 1.5;
                var bottom = Math.Abs(position.Y - stack.Bottom) <= 1.5;
                if (!left && !right && !top && !bottom) return false;
                var verticalInterior = stack.Height <= 4 || position.Y > stack.Top + 1.5 && position.Y < stack.Bottom - 1.5;
                var horizontalInterior = stack.Width <= 4 || position.X > stack.Left + 1.5 && position.X < stack.Right - 1.5;
                edges[0] |= left && verticalInterior;
                edges[1] |= right && verticalInterior;
                edges[2] |= top && horizontalInterior;
                edges[3] |= bottom && horizontalInterior;
            }
        return changed > 0 && edges.All(edge => edge);
    }

    private static byte[] BitmapPixels(RenderTargetBitmap bitmap)
    {
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels;
    }

    private static RenderTargetBitmap RenderChart(HistoryUsageChart chart)
    {
        chart.UpdateLayout();
        HistoryOverviewChecks.Pump();
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(15, 23, 42)), null,
                new Rect(0, 0, chart.ActualWidth, chart.ActualHeight));
            context.DrawRectangle(new VisualBrush(chart) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                null, new Rect(0, 0, chart.ActualWidth, chart.ActualHeight));
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(chart.ActualWidth), (int)Math.Ceiling(chart.ActualHeight),
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        return bitmap;
    }

    private static void SaveBitmap(RenderTargetBitmap bitmap, string path)
    {
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = System.IO.File.Create(path);
        encoder.Save(output);
    }

    private static void Leave(HistoryUsageChart chart) => chart.RaiseEvent(
        new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseLeaveEvent });

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
