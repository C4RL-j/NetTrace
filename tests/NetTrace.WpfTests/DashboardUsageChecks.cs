using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NetTrace;
using NetTrace.Controls;
using NetTrace.Models;
using NetTrace.Services;
using NetTrace.Utils;

internal static class DashboardUsageChecks
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly string[] RangeTitles = ["TODAY", "LAST 7 DAYS", "LAST 30 DAYS"];

    public static void Run(string root, Action<bool, string> check)
    {
        var previews = System.IO.Path.Combine(root, "dashboard-usage-previews");
        CheckRanges(root, previews, check);
        CheckEmpty(root, previews, check);
        Console.WriteLine("Dashboard summary range previews: " + previews);
    }

    private static void CheckRanges(string root, string previews, Action<bool, string> check)
    {
        var store = new UsageStore(System.IO.Path.Combine(root, "dashboard-usage-ranges"));
        var clock = new OwnedClock();
        var today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
        store.AddUsage(today, 24_320_000_000, 2_000_000_000);
        store.AddUsage(today.AddDays(-1), 10_000_000_000, 1_000_000_000);
        store.AddUsage(today.AddDays(-6), 6_000_000_000, 600_000_000);
        store.AddUsage(today.AddDays(-7), 7_000_000_000, 700_000_000);
        store.AddUsage(today.AddDays(-29), 29_000_000_000, 2_900_000_000);
        store.AddUsage(today.AddDays(-30), 30_000_000_000, 3_000_000_000);
        store.AddUsage(today.AddDays(1), 90_000_000_000, 9_000_000_000);
        using var monitoring = HistoryOverviewChecks.CreateMonitoring(store, clock);
        var window = CreateWindow(monitoring);
        try
        {
            window.Show();
            HistoryOverviewChecks.StopViewTimer(window);
            Layout(window);
            var card = Control<Border>(window, "DashboardUsageCard");
            var total = Control<TextBlock>(window, "DashboardUsageTotalText");
            var breakdown = Control<TextBlock>(window, "DashboardUsageBreakdownText");
            var expected = new (long Download, long Upload)[]
            {
                (24_320_000_000, 2_000_000_000),
                (40_320_000_000, 3_600_000_000),
                (76_320_000_000, 7_200_000_000)
            };

            check(Control<TabItem>(window, "DashboardTab").IsSelected && IsSelected(window, 0),
                "Dashboard Data Usage opens with Today selected in the compact selector");
            CheckValues(window, 0, expected[0], check, "default Today excludes prior and future date buckets");
            CheckStructure(window, check);
            foreach (var index in new[] { 1, 2, 0 })
            {
                Cycle(window);
                CheckValues(window, index, expected[index], check,
                    "one arrow click cycles Today to 7D to 30D to Today");
            }
            var beforeSwitching = monitoring.GetUsageTotals();
            for (var index = 0; index < RangeTitles.Length; index++)
            {
                Click(window, index);
                CheckValues(window, index, expected[index], check,
                    "selected range uses the existing inclusive stored date window");
                check(ReferenceEquals(card, Control<Border>(window, "DashboardUsageCard")) &&
                    ReferenceEquals(total, Control<TextBlock>(window, "DashboardUsageTotalText")) &&
                    ReferenceEquals(breakdown, Control<TextBlock>(window, "DashboardUsageBreakdownText")),
                    $"{RangeTitles[index]} updates the same summary card and directional text controls");
                CheckAccent(window, index, check);
                CheckLayout(window, check, RangeTitles[index] + " GB totals");
                Save(window, previews, "760x600-" + index + "-GB.png");
            }
            check(monitoring.GetUsageTotals() == beforeSwitching,
                "Selecting Dashboard ranges leaves all underlying usage totals unchanged");

            long addedDownload = 0, addedUpload = 0;
            for (var index = 0; index < RangeTitles.Length; index++)
            {
                Click(window, index);
                var download = 10_000_000L * (index + 1);
                var upload = 2_000_000L * (index + 1);
                store.AddUsage(today, download, upload);
                addedDownload += download;
                addedUpload += upload;
                window.RefreshDashboard();
                Layout(window);
                CheckValues(window, index, (expected[index].Download + addedDownload, expected[index].Upload + addedUpload),
                    check, "the existing Dashboard refresh updates the currently selected range");
            }
            for (var index = 0; index < expected.Length; index++)
                expected[index] = (expected[index].Download + addedDownload, expected[index].Upload + addedUpload);

            store.AddUsage(today.AddDays(-6), 500_000_000, 40_000_000);
            store.AddUsage(today.AddDays(-29), 900_000_000, 60_000_000);
            expected[1] = (expected[1].Download + 500_000_000, expected[1].Upload + 40_000_000);
            expected[2] = (expected[2].Download + 1_400_000_000, expected[2].Upload + 100_000_000);
            for (var index = 0; index < expected.Length; index++)
            {
                Click(window, index);
                CheckValues(window, index, expected[index], check,
                    "updated older buckets affect only the ranges containing their dates");
            }

            CheckKeyboard(window, check);
            CheckNavigation(window, store, today, expected, check);
            check(ReferenceEquals(card, Control<Border>(window, "DashboardUsageCard")) &&
                ReferenceEquals(total, Control<TextBlock>(window, "DashboardUsageTotalText")),
                "Returning through every navigation page preserves the original summary controls");
            window.Width = 920;
            window.Height = 740;
            Layout(window);
            CheckLayout(window, check, "resized 920x740");
            Save(window, previews, "920x740-30-days.png");
            window.Width = 760;
            window.Height = 600;
            Layout(window);
            CheckLayout(window, check, "returned minimum layout");
            var page = Page(window);
            page.ScrollToEnd();
            Layout(window);
            var profile = Control<TextBlock>(window, "DashboardProfileText");
            var bounds = Bounds(profile, page);
            check(page.VerticalScrollBarVisibility == ScrollBarVisibility.Auto && bounds.Top >= -0.5 &&
                bounds.Bottom <= page.ActualHeight + 0.5,
                "The minimum Dashboard keeps vertical access to the unchanged Current Connection summary");
            HistoryOverviewChecks.SavePreview(window, System.IO.Path.Combine(previews, "760x600-bottom.png"));
        }
        finally { window.Close(); }
    }

    private static void CheckEmpty(string root, string previews, Action<bool, string> check)
    {
        using var monitoring = HistoryOverviewChecks.CreateMonitoring(
            new UsageStore(System.IO.Path.Combine(root, "dashboard-usage-empty")), new OwnedClock());
        var window = CreateWindow(monitoring);
        try
        {
            window.Show();
            HistoryOverviewChecks.StopViewTimer(window);
            Layout(window);
            check(IsSelected(window, 0), "A separate fresh Dashboard session also defaults to Today");
            for (var index = 0; index < RangeTitles.Length; index++)
            {
                Click(window, index);
                CheckValues(window, index, (0, 0), check, "an empty range displays truthful zero total and directional values");
                CheckLayout(window, check, RangeTitles[index] + " zero totals");
                Save(window, previews, "760x600-" + index + "-zero.png");
            }
        }
        finally { window.Close(); }
    }

    private static void CheckNavigation(MainWindow window, UsageStore store, DateOnly today,
        (long Download, long Upload)[] expected, Action<bool, string> check)
    {
        var navigation = Control<TabControl>(window, "MainNavigation");
        var dashboard = Control<TabItem>(window, "DashboardTab");
        var pageNames = new[] { "HealthTab", "ConnectionsTab", "HistoryTab", "AppUsageTab", "SettingsTab" };
        for (var index = 1; index < RangeTitles.Length; index++)
        {
            Click(window, index);
            foreach (var name in pageNames)
            {
                navigation.SelectedItem = Control<TabItem>(window, name);
                Layout(window);
                store.AddUsage(today, 1000, 500);
                for (var range = 0; range < expected.Length; range++)
                    expected[range] = (expected[range].Download + 1000, expected[range].Upload + 500);
                navigation.SelectedItem = dashboard;
                Layout(window);
                CheckValues(window, index, expected[index], check,
                    "navigation back from " + name + " retains selection and refreshes usage accumulated while away");
            }
        }
    }

    private static void CheckKeyboard(MainWindow window, Action<bool, string> check)
    {
        Click(window, 0);
        var button = Control<Button>(window, "DashboardUsageSelectorButton");
        button.BringIntoView();
        Layout(window);
        Keyboard.Focus(button);
        HistoryOverviewChecks.Pump();
        check(button.Focusable && button.IsKeyboardFocused,
            "The compact Dashboard range selector accepts keyboard focus at the minimum window size");
        var source = PresentationSource.FromVisual(window)!;
        button.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Space)
            { RoutedEvent = Keyboard.KeyDownEvent, Source = button });
        button.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Space)
            { RoutedEvent = Keyboard.KeyUpEvent, Source = button });
        Layout(window);
        check(IsSelected(window, 1) && Text(window, "DashboardUsageTitleText") == RangeTitles[1],
            "Pressing Space on the focused selector cycles Today to Last 7 Days");
        Keyboard.Focus(Control<TabItem>(window, "DashboardTab"));
        HistoryOverviewChecks.Pump();
    }

    private static void CheckStructure(MainWindow window, Action<bool, string> check)
    {
        var section = Control<Grid>(window, "DashboardUsageSection");
        var summary = Control<Grid>(window, "DashboardUsageSummary");
        var selector = Control<Button>(window, "DashboardUsageSelectorButton");
        var card = Control<Border>(window, "DashboardUsageCard");
        check(section.ColumnDefinitions.Count == 3 && section.ColumnDefinitions[0].Width.IsStar &&
            section.ColumnDefinitions[2].Width.IsStar && Grid.GetColumn(summary) == 0 && Grid.GetRow(summary) == 1,
            "Data Usage places one summary in the left column and preserves the existing Dashboard spacing");
        var traffic = Control<DashboardTrafficChart>(window, "DashboardTrafficChart");
        check(section.Children.Count == 3 && ReferenceEquals(traffic.Parent, section) &&
                Grid.GetColumn(traffic) == 2 && Grid.GetRow(traffic) == 1 &&
                section.Children.OfType<FrameworkElement>().Where(child => Grid.GetRow(child) == 1 &&
                    Grid.GetColumn(child) == 2).Single() == traffic,
            "The right Data Usage column contains only the native live Download/Upload graph");
        check(summary.Children.Count == 2 && ReferenceEquals(selector.Parent, summary) &&
            ReferenceEquals(card.Parent, summary) && Grid.GetRow(selector) == 0 && Grid.GetRow(card) == 1 &&
            selector.HorizontalAlignment == HorizontalAlignment.Left,
            "One compact selector attaches directly above the left summary card");
        check(ReferenceEquals(selector.Style, window.FindResource("DashboardUsageSelectorStyle")) &&
            HistoryOverviewChecks.Descendants(selector).OfType<TextBlock>().Any(text => text.Text == "›") &&
            Text(window, "DashboardUsageRangeText") == "TODAY",
            "The compact selector exposes the selected range and next-range arrow through the shared NetTrace style");
        check(new[] { "DashboardUsageTabs", "DashboardUsageTodayButton", "DashboardUsage7DButton", "DashboardUsage30DButton",
                "TodayTotalText", "TodayBreakdownText", "WeekTotalText", "WeekBreakdownText", "MonthTotalText", "MonthBreakdownText" }
                .All(name => window.FindName(name) is null) &&
            summary.Children.OfType<Border>().Count() == 1,
            "The old separate range tabs and duplicate usage cards are removed");
        check(card.CornerRadius.TopLeft == 0 && card.CornerRadius.TopRight == 14 &&
            card.CornerRadius.BottomLeft == 14 && card.CornerRadius.BottomRight == 14,
            "The shared card joins the compact selector at its top left and keeps the remaining rounded corners");
    }

    private static void CheckAccent(MainWindow window, int selected, Action<bool, string> check)
    {
        var accent = (Brush)window.FindResource("AccentBrush");
        var button = Control<Button>(window, "DashboardUsageSelectorButton");
        var borders = HistoryOverviewChecks.Descendants(button).OfType<Border>().ToArray();
        check(SameBrush(button.Foreground, accent) || borders.Any(border =>
                SameBrush(border.Background, accent) || SameBrush(border.BorderBrush, accent)),
            $"The selected {RangeTitles[selected]} selector uses the existing cyan accent");
        check(borders.Any(border => border.CornerRadius.TopLeft > 0 && border.CornerRadius.TopRight > 0 &&
                border.CornerRadius.BottomLeft == 0 && border.CornerRadius.BottomRight == 0),
            $"The {RangeTitles[selected]} selector has rounded upper corners and attaches flush to the summary");
    }

    private static void CheckLayout(MainWindow window, Action<bool, string> check, string state)
    {
        Layout(window);
        var page = Page(window);
        var section = Control<Grid>(window, "DashboardUsageSection");
        var summary = Control<Grid>(window, "DashboardUsageSummary");
        var selector = Control<Button>(window, "DashboardUsageSelectorButton");
        var card = Control<Border>(window, "DashboardUsageCard");
        var selectorBounds = Bounds(selector, summary);
        var cardBounds = Bounds(card, summary);
        check(window.MinWidth == 760 && window.MinHeight == 600 &&
            page.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled && page.ScrollableWidth < 0.01 &&
            section.ActualWidth <= page.ViewportWidth + 0.5 && summary.ActualWidth <= section.ActualWidth / 2 + 0.5,
            $"{state} preserves the minimum Dashboard width with no horizontal overflow");
        check(Math.Abs(selectorBounds.Bottom - cardBounds.Top) < 0.5 && Math.Abs(selectorBounds.Left - cardBounds.Left) < 0.5 &&
            selectorBounds.Width < cardBounds.Width - 20 && selectorBounds.Width <= 180 && selectorBounds.Height <= 40,
            $"{state} keeps the selector compact and flush with the top-left summary edge");
        var valuesFit = new[] { "DashboardUsageTitleText", "DashboardUsageTotalText", "DashboardUsageBreakdownText" }
            .All(name =>
            {
                var text = Control<TextBlock>(window, name);
                var bounds = Bounds(text, card);
                text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                return bounds.Left >= -0.5 && bounds.Right <= card.ActualWidth + 0.5 &&
                    bounds.Top >= -0.5 && bounds.Bottom <= card.ActualHeight + 0.5 &&
                    text.DesiredSize.Width <= text.ActualWidth + 0.5;
            });
        var content = HistoryOverviewChecks.Descendants(selector).OfType<ContentPresenter>().Single();
        content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        check(valuesFit && content.DesiredSize.Width <= content.ActualWidth + 0.5 && selectorBounds.Left >= -0.5 &&
            selectorBounds.Right <= summary.ActualWidth + 0.5 && selector.ActualHeight >= 28,
            $"{state} shows the complete selector label, arrow, title, total, download and upload values without clipping");
        Layout(window);
    }

    private static void CheckValues(MainWindow window, int range, (long Download, long Upload) expected,
        Action<bool, string> check, string reason) => check(IsSelected(window, range) &&
            Text(window, "DashboardUsageTitleText") == RangeTitles[range] &&
            Text(window, "DashboardUsageTotalText") == ByteFormatter.Format(expected.Download + expected.Upload) &&
            Text(window, "DashboardUsageBreakdownText") == $"↓ {ByteFormatter.Format(expected.Download)}   ↑ {ByteFormatter.Format(expected.Upload)}",
            RangeTitles[range] + ": " + reason);

    private static bool IsSelected(MainWindow window, int range) =>
        Text(window, "DashboardUsageRangeText") == RangeTitles[range];

    private static void Click(MainWindow window, int range)
    {
        for (var clicks = 0; clicks < RangeTitles.Length && !IsSelected(window, range); clicks++) Cycle(window);
    }

    private static void Cycle(MainWindow window)
    {
        typeof(Button).GetMethod("OnClick", PrivateInstance)!.Invoke(Control<Button>(window, "DashboardUsageSelectorButton"), null);
        Layout(window);
    }

    private static bool SameBrush(Brush? left, Brush? right) => left is SolidColorBrush first &&
        right is SolidColorBrush second && first.Color == second.Color && Math.Abs(first.Opacity - second.Opacity) < 0.01;
    private static Rect Bounds(FrameworkElement element, Visual parent) =>
        element.TransformToAncestor(parent).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
    private static ScrollViewer Page(MainWindow window) => (ScrollViewer)Control<TabItem>(window, "DashboardTab").Content;
    private static T Control<T>(Window window, string name) where T : class => HistoryOverviewChecks.Control<T>(window, name);
    private static string Text(Window window, string name) => Control<TextBlock>(window, name).Text;
    private static void Layout(Window window) { window.UpdateLayout(); HistoryOverviewChecks.Pump(); }
    private static void Save(MainWindow window, string previews, string name)
    {
        Control<Grid>(window, "DashboardUsageSummary").BringIntoView();
        Layout(window);
        HistoryOverviewChecks.SavePreview(window, System.IO.Path.Combine(previews, name));
    }

    private static MainWindow CreateWindow(MonitoringService monitoring) => new(monitoring, new OwnedStartup(),
        new WifiRadioService(new NoRadio()), appUsageClient: new AppUsageTodayChecks.UnavailableClient(),
        appTrackingSetupService: new AppUsageSetupChecks.RunningSetupService())
        { ShowActivated = false, ShowInTaskbar = false, Opacity = 0, Width = 760, Height = 600 };
    private sealed class OwnedClock : TimeProvider
    {
        private readonly DateTimeOffset _now = new(new DateTime(2026, 10, 13, 12, 0, 0),
            TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 13, 12, 0, 0)));
        public override DateTimeOffset GetUtcNow() => _now.ToUniversalTime();
    }
    private sealed class NoRadio : IWifiRadioSource { public WifiRadioSnapshot Read(string adapterId) => new(adapterId, false); }
    private sealed class OwnedStartup : IWindowsStartupRegistration
    {
        public bool IsEnabled() => false;
        public void Enable() => throw new InvalidOperationException("Dashboard range verification cannot change startup settings.");
        public void Disable() => throw new InvalidOperationException("Dashboard range verification cannot change startup settings.");
    }
}
