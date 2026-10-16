using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Path = System.Windows.Shapes.Path;
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
        CheckLargeValues(root, previews, check);
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
                "Dashboard Data Usage opens with Today selected in the pill selector");
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
                CheckDirectionColors(window, index, expected[index], check, "recorded GB totals");
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
                CheckDirectionColors(window, index, (0, 0), check, "zero totals");
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

    private static void CheckLargeValues(string root, string previews, Action<bool, string> check)
    {
        var store = new UsageStore(System.IO.Path.Combine(root, "dashboard-usage-large-values"));
        var clock = new OwnedClock();
        var today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
        store.AddUsage(today, 3_000_000_000_000_000_000, 1_000_000_000_000_000_000);
        store.AddUsage(today.AddDays(-1), 1_000_000_000_000_000_000, 1_000_000_000_000_000_000);
        store.AddUsage(today.AddDays(-29), 200_000_000_000_000_000, 200_000_000_000_000_000);
        var expected = new (long Download, long Upload)[]
        {
            (3_000_000_000_000_000_000, 1_000_000_000_000_000_000),
            (4_000_000_000_000_000_000, 2_000_000_000_000_000_000),
            (4_200_000_000_000_000_000, 2_200_000_000_000_000_000)
        };
        using var monitoring = HistoryOverviewChecks.CreateMonitoring(store, clock);
        var window = CreateWindow(monitoring);
        try
        {
            window.Show();
            HistoryOverviewChecks.StopViewTimer(window);
            foreach (var size in new[] { (Width: 760, Height: 600), (Width: 920, Height: 740) })
            {
                window.Width = size.Width;
                window.Height = size.Height;
                for (var index = 0; index < RangeTitles.Length; index++)
                {
                    Click(window, index);
                    CheckValues(window, index, expected[index], check,
                        "large persisted byte totals remain exact in the narrower summary");
                    CheckDirectionColors(window, index, expected[index], check, $"{size.Width}x{size.Height} large TB totals");
                    CheckLayout(window, check, $"{size.Width}x{size.Height} {RangeTitles[index]} large TB values");
                    Save(window, previews, $"{size.Width}x{size.Height}-{index}-large-TB.png");
                }
            }
        }
        finally { window.Close(); }
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
            "The pill Dashboard range selector accepts keyboard focus at the minimum window size");
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
        check(section.ColumnDefinitions.Count == 3 && section.ColumnDefinitions[0].Width.IsAbsolute &&
            section.ColumnDefinitions[2].Width.IsStar && Grid.GetColumn(summary) == 0 && Grid.GetRow(summary) == 1,
            "Data Usage places one compact summary in a narrower left column and gives the graph the remaining width");
        var traffic = Control<DashboardTrafficChart>(window, "DashboardTrafficChart");
        var speedTest = Control<Border>(window, "SpeedTestCard");
        check(section.Children.Count == 4 && ReferenceEquals(traffic.Parent, section) &&
                Grid.GetColumn(traffic) == 2 && Grid.GetRow(traffic) == 1 &&
                section.Children.OfType<FrameworkElement>().Where(child => Grid.GetRow(child) == 1 &&
                    Grid.GetColumn(child) == 2).SequenceEqual(new FrameworkElement[] { speedTest, traffic }) &&
                ReferenceEquals(speedTest.Parent, section) && speedTest.Height == 52 && traffic.Margin.Top == 54,
            "The right Data Usage column retains the native live graph below a fixed Speed Test in its existing gap");
        check(summary.Children.Count == 2 && ReferenceEquals(selector.Parent, summary) &&
            ReferenceEquals(card.Parent, summary) && Grid.GetRow(selector) == 0 && Grid.GetRow(card) == 1 &&
            selector.HorizontalAlignment == HorizontalAlignment.Stretch,
            "One pill selector remains directly above the left summary card");
        check(ReferenceEquals(selector.Style, window.FindResource("DashboardUsageSelectorStyle")) &&
            Control<Path>(window, "DashboardUsageSelectorIcon").Data is not null &&
            Control<Path>(window, "DashboardUsageSelectorChevron").Data is not null &&
            Text(window, "DashboardUsageRangeText") == "TODAY",
            "The pill selector exposes its selected range, calendar icon and chevron through the shared NetTrace style");
        check(new[] { "DashboardUsageTabs", "DashboardUsageTodayButton", "DashboardUsage7DButton", "DashboardUsage30DButton",
                "TodayTotalText", "TodayBreakdownText", "WeekTotalText", "WeekBreakdownText", "MonthTotalText", "MonthBreakdownText" }
                .All(name => window.FindName(name) is null) &&
            summary.Children.OfType<Border>().Count() == 1,
            "The old separate range tabs and duplicate usage cards are removed");
        check(card.CornerRadius.TopLeft >= 16 && card.CornerRadius.TopRight == card.CornerRadius.TopLeft &&
            card.CornerRadius.BottomLeft == card.CornerRadius.TopLeft && card.CornerRadius.BottomRight == card.CornerRadius.TopLeft &&
            card.Padding.Left >= 18 && card.Padding.Top >= 18 &&
            Control<TextBlock>(window, "DashboardUsageTotalText").FontSize >= 30 &&
            Control<TextBlock>(window, "DashboardUsageTotalText").FontSize >=
                Control<TextBlock>(window, "DashboardUsageTitleText").FontSize * 2 &&
            Control<TextBlock>(window, "DashboardUsageTotalText").FontWeight.ToOpenTypeWeight() >= 600,
            "The refreshed summary has soft rounded corners, generous inset spacing and a larger prominent total");
        check(card.Background is GradientBrush gradient && gradient.GradientStops.Count >= 2 &&
            gradient.GradientStops.All(stop => stop.Color.A == 255 &&
                Math.Max(stop.Color.R, Math.Max(stop.Color.G, stop.Color.B)) < 80),
            "The refreshed usage card keeps a subtle opaque dark surface consistent with NetTrace");
    }

    private static void CheckAccent(MainWindow window, int selected, Action<bool, string> check)
    {
        var accent = (Brush)window.FindResource("AccentBrush");
        var button = Control<Button>(window, "DashboardUsageSelectorButton");
        var borders = HistoryOverviewChecks.Descendants(button).OfType<Border>().ToArray();
        check(SameBrush(button.Foreground, accent) || borders.Any(border =>
                SameBrush(border.Background, accent) || SameBrush(border.BorderBrush, accent)),
            $"The selected {RangeTitles[selected]} selector uses the existing cyan accent");
        check(borders.Any(border => border.CornerRadius.TopLeft >= button.ActualHeight / 2 - 1 &&
                border.CornerRadius.TopRight == border.CornerRadius.TopLeft &&
                border.CornerRadius.BottomLeft == border.CornerRadius.TopLeft &&
                border.CornerRadius.BottomRight == border.CornerRadius.TopLeft),
            $"The {RangeTitles[selected]} selector uses a fully rounded pill above the summary");
    }

    private static void CheckLayout(MainWindow window, Action<bool, string> check, string state)
    {
        Layout(window);
        var page = Page(window);
        var section = Control<Grid>(window, "DashboardUsageSection");
        var summary = Control<Grid>(window, "DashboardUsageSummary");
        var selector = Control<Button>(window, "DashboardUsageSelectorButton");
        var card = Control<Border>(window, "DashboardUsageCard");
        var traffic = Control<DashboardTrafficChart>(window, "DashboardTrafficChart");
        var selectorBounds = Bounds(selector, summary);
        var cardBounds = Bounds(card, summary);
        var previousEqualWidth = (section.ActualWidth - section.ColumnDefinitions[1].ActualWidth) / 2;
        check(window.MinWidth == 760 && window.MinHeight == 600 &&
            page.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled && page.ScrollableWidth < 0.01 &&
            section.ActualWidth <= page.ViewportWidth + 0.5 && summary.ActualWidth < previousEqualWidth - 60 &&
            traffic.ActualWidth > previousEqualWidth + 60 &&
            Math.Abs(summary.ActualWidth + traffic.ActualWidth + section.ColumnDefinitions[1].ActualWidth - section.ActualWidth) < 0.5,
            $"{state} keeps the summary narrower and gives its freed width to the graph without horizontal overflow");
        check(cardBounds.Top - selectorBounds.Bottom is >= 8 and <= 12 &&
            Math.Abs(selectorBounds.Left - cardBounds.Left) < 0.5 &&
            Math.Abs(selectorBounds.Right - cardBounds.Right) < 0.5 && selectorBounds.Height is >= 42 and <= 48,
            $"{state} keeps the larger pill aligned to the summary width with clean spacing above the card");
        var textDiagnostics = new List<string>();
        var valuesFit = new[] { "DashboardUsageTitleText", "DashboardUsageTotalText", "DashboardUsageBreakdownText" }
            .Select(name =>
            {
                var text = Control<TextBlock>(window, name);
                var bounds = Bounds(text, card);
                var measureWidth = text.TextWrapping == TextWrapping.NoWrap ? double.PositiveInfinity
                    : text.ActualWidth + text.Margin.Left + text.Margin.Right;
                text.Measure(new Size(measureWidth,
                    double.PositiveInfinity));
                // DesiredSize includes FrameworkElement margins; ActualWidth/Height
                // and transformed text bounds describe only the element itself.
                var desiredWidth = text.DesiredSize.Width - text.Margin.Left - text.Margin.Right;
                var desiredHeight = text.DesiredSize.Height - text.Margin.Top - text.Margin.Bottom;
                textDiagnostics.Add(FormattableString.Invariant($"{name}: actual {text.ActualWidth:0.###}x{text.ActualHeight:0.###}, desired {text.DesiredSize.Width:0.###}x{text.DesiredSize.Height:0.###}, content desired {desiredWidth:0.###}x{desiredHeight:0.###}, measure width {measureWidth:0.###}, bounds {bounds}, margin {text.Margin}, wrap {text.TextWrapping}."));
                return bounds.Left >= -0.5 && bounds.Right <= card.ActualWidth + 0.5 &&
                    bounds.Top >= -0.5 && bounds.Bottom <= card.ActualHeight + 0.5 &&
                    text.TextTrimming == TextTrimming.None && desiredWidth <= text.ActualWidth + 0.5 &&
                    desiredHeight <= text.ActualHeight + 0.5;
            }).ToArray().All(value => value);
        var content = HistoryOverviewChecks.Descendants(selector).OfType<ContentPresenter>().Single();
        content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var icon = Control<Path>(window, "DashboardUsageSelectorIcon");
        var chevron = Control<Path>(window, "DashboardUsageSelectorChevron");
        var label = Control<TextBlock>(window, "DashboardUsageRangeText");
        var iconBounds = Bounds(icon, selector);
        var labelBounds = Bounds(label, selector);
        var chevronBounds = Bounds(chevron, selector);
        var readable = valuesFit && content.DesiredSize.Width <= content.ActualWidth + 0.5 && selectorBounds.Left >= -0.5 &&
            selectorBounds.Right <= summary.ActualWidth + 0.5 && selector.ActualHeight >= 42 &&
            iconBounds.Width >= 14 && iconBounds.Height >= 14 && iconBounds.Left >= 10 &&
            iconBounds.Right <= labelBounds.Left - 4 && labelBounds.Right <= chevronBounds.Left - 4 &&
            chevronBounds.Right <= selector.ActualWidth - 10 &&
            iconBounds.Top >= -0.5 && iconBounds.Bottom <= selector.ActualHeight + 0.5 &&
            chevronBounds.Top >= -0.5 && chevronBounds.Bottom <= selector.ActualHeight + 0.5;
        if (!readable)
        {
            Console.WriteLine(FormattableString.Invariant($"Dashboard summary layout failure {state}: card {card.ActualWidth:0.###}x{card.ActualHeight:0.###}, selector bounds {selectorBounds}, summary width {summary.ActualWidth:0.###}, selector content actual {content.ActualWidth:0.###}x{content.ActualHeight:0.###}, desired {content.DesiredSize.Width:0.###}x{content.DesiredSize.Height:0.###}."));
            foreach (var diagnostic in textDiagnostics) Console.WriteLine(diagnostic);
        }
        check(readable,
            $"{state} shows the complete selector icon, label, chevron, title, total, download and upload values without clipping");
        Layout(window);
    }

    private static void CheckDirectionColors(MainWindow window, int range, (long Download, long Upload) expected,
        Action<bool, string> check, string state)
    {
        Layout(window);
        var text = Control<TextBlock>(window, "DashboardUsageBreakdownText");
        var download = Control<Run>(window, "DashboardUsageDownloadRun");
        var upload = Control<Run>(window, "DashboardUsageUploadRun");
        var drawings = Drawings(VisualTreeHelper.GetDrawing(text)).OfType<GlyphRunDrawing>().ToArray();
        var green = Color.FromRgb(0x22, 0xC5, 0x5E);
        var blue = Color.FromRgb(0x38, 0xBD, 0xF8);
        string Characters(Color color) => string.Concat(drawings.Where(drawing =>
            drawing.ForegroundBrush is SolidColorBrush brush && brush.Color == color)
            .Select(drawing => drawing.GlyphRun.Characters is { } characters ? new string(characters.ToArray()) : ""));
        // Text wrapping may omit whitespace glyphs at line breaks; the Run text
        // separately preserves exact formatting, while drawing confirms its ink.
        string WithoutWhitespace(string value) => string.Concat(value.Where(character => !char.IsWhiteSpace(character)));
        check(IsSelected(window, range) && download.Text == "↓ " + ByteFormatter.Format(expected.Download) &&
            upload.Text == "↑ " + ByteFormatter.Format(expected.Upload) &&
            WithoutWhitespace(Characters(green)) == WithoutWhitespace(download.Text) &&
            WithoutWhitespace(Characters(blue)) == WithoutWhitespace(upload.Text),
            $"{RangeTitles[range]} {state} renders exact download in green and upload in blue, including their direction arrows");
    }

    private static IEnumerable<Drawing> Drawings(Drawing? drawing)
    {
        if (drawing is null) yield break;
        yield return drawing;
        if (drawing is DrawingGroup group)
            foreach (var child in group.Children)
                foreach (var item in Drawings(child)) yield return item;
    }

    private static void CheckValues(MainWindow window, int range, (long Download, long Upload) expected,
        Action<bool, string> check, string reason)
    {
        var title = Text(window, "DashboardUsageTitleText");
        var total = Text(window, "DashboardUsageTotalText");
        var breakdown = Text(window, "DashboardUsageBreakdownText");
        var expectedTotal = ByteFormatter.Format(expected.Download + expected.Upload);
        var expectedBreakdown = $"↓ {ByteFormatter.Format(expected.Download)}   ↑ {ByteFormatter.Format(expected.Upload)}";
        var correct = IsSelected(window, range) && title == RangeTitles[range] &&
            total == expectedTotal && breakdown == expectedBreakdown;
        if (!correct)
        {
            Console.WriteLine($"Dashboard usage value mismatch ({reason}): selected '{Text(window, "DashboardUsageRangeText")}', title '{title}', total '{total}' (expected '{expectedTotal}'), breakdown '{breakdown}' (expected '{expectedBreakdown}').");
            Console.WriteLine("Actual breakdown code points: " + string.Join(" ", breakdown.Select(character => $"U+{(int)character:X4}")));
            Console.WriteLine($"Directional Runs: download '{Control<Run>(window, "DashboardUsageDownloadRun").Text}', upload '{Control<Run>(window, "DashboardUsageUploadRun").Text}'.");
            var preview = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "NetTrace-Polish005-summary-failure-" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                Save(window, System.IO.Path.GetDirectoryName(preview)!, System.IO.Path.GetFileName(preview));
                Console.WriteLine("Dashboard usage failure preview: " + preview);
            }
            catch (Exception exception) { Console.WriteLine("Could not save the failure preview: " + exception.Message); }
        }
        check(correct, RangeTitles[range] + ": " + reason);
    }

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
    private static string Text(Window window, string name)
    {
        var text = Control<TextBlock>(window, name);
        return new TextRange(text.ContentStart, text.ContentEnd).Text;
    }
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
