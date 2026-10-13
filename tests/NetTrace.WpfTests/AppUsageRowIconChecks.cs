using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using AppUsageIpc;
using NetTrace;
using NetTrace.Presentation;
using NetTrace.Services;
using NetTrace.Utils;

internal static class AppUsageRowIconChecks
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly AppUsageRange[] Ranges = [AppUsageRange.Today, AppUsageRange.SevenDays, AppUsageRange.ThirtyDays];

    public static void Run(string root, Action<bool, string> check)
    {
        CheckNamesAndRanges(check);
        CheckCache(root, check);
        CheckRows(check);
        CheckWindow(root, check);
    }

    private static void CheckNamesAndRanges(Action<bool, string> check)
    {
        var names = new (string Display, string? Executable, string Expected, bool Subtitle)[]
        {
            ("NetMod", "netmod.exe", "NetMod", true),
            ("netmod.exe", "netmod.exe", "netmod.exe", false),
            ("  NeTmOd.ExE  ", " netmod.exe ", "NeTmOd.ExE", false),
            ("Brave Browser", "brave.exe", "Brave Browser", true),
            ("Task Manager", null, "Task Manager", false),
            ("Task Manager", "  ", "Task Manager", false),
            ("", "cmd.exe", "cmd.exe", false),
            ("  ", " powershell.exe ", "powershell.exe", false),
        };
        foreach (var (display, executable, expected, subtitle) in names)
        {
            var input = Application("path:name", display, executable, null, 17, 3);
            var row = Presentation([input], AppUsageRange.Today).Applications.Single();
            check(row.DisplayName == expected, $"Primary application name chooses the friendly name or executable for '{expected}'");
            check(row.HasExecutableName == subtitle, $"Executable subtitle visibility follows distinct trimmed names for '{expected}'");
        }
        var special = Presentation([
            Application("system", "noncanonical System metadata", "system.exe", @"C:\Owned\system.exe", 40, 2, system: true),
            Application("unknown", "noncanonical Unknown metadata", "unknown.exe", @"C:\Owned\unknown.exe", 30, 4, unknown: true)
        ], AppUsageRange.Today).Applications;
        check(special[0].DisplayName == "System" && special[0].IconKind == AppUsageIconKind.System && !special[0].HasExecutableName,
            "System keeps its canonical name, dedicated icon kind and no executable subtitle");
        check(special[1].DisplayName == "Unknown / Unattributed" && special[1].IconKind == AppUsageIconKind.Unknown && !special[1].HasExecutableName,
            "Unknown keeps its canonical name, dedicated icon kind and no executable subtitle");
        var missing = Presentation([Application("path:missing", "", null, null, 1, 0)], AppUsageRange.Today).Applications.Single();
        check(!string.IsNullOrWhiteSpace(missing.DisplayName) && !missing.HasExecutableName,
            "Missing friendly and executable names retain a useful primary label without invented subtitle");

        foreach (var range in Ranges)
        {
            var inputs = Fixture();
            var result = Result(inputs, range);
            var presentation = AppUsageTodayPresentation.FromResult(new(IpcState.Connected, result), range);
            check(presentation.State == AppUsageTodayState.Available && presentation.TrackedAppsText == inputs.Count.ToString() &&
                presentation.DownloadText == ByteFormatter.Format(result.TotalDownloadBytes) &&
                presentation.UploadText == ByteFormatter.Format(result.TotalUploadBytes) &&
                presentation.LogicalTotalText == ByteFormatter.Format(result.LogicalTotalBytes),
                $"{range} icon presentation preserves IPC summary amounts and application count");
            check(presentation.Applications.Select(row => row.ApplicationIdentity).SequenceEqual(inputs.Select(row => row.ApplicationIdentity)),
                $"{range} icon presentation preserves service row order and identity");
            check(presentation.Applications.Zip(inputs).All(pair => pair.First.DownloadText == ByteFormatter.Format(pair.Second.DownloadBytes) &&
                pair.First.UploadText == ByteFormatter.Format(pair.Second.UploadBytes) && pair.First.TotalText == ByteFormatter.Format(pair.Second.TotalBytes)),
                $"{range} icons do not reconcile, reset or alter any application amount");
            check(presentation.Applications[0].ExecutablePath == inputs[0].ExecutablePath &&
                presentation.Applications[0].IconKind == AppUsageIconKind.Application,
                $"{range} executable metadata reaches icon extraction without entering display text");
            var empty = Presentation([], range);
            check(empty.State == AppUsageTodayState.Empty && empty.Applications.Count == 0 && empty.LogicalTotalText == ByteFormatter.Format(0),
                $"{range} empty state remains unchanged by row icons");
            var unavailable = AppUsageTodayPresentation.FromResult(new(IpcState.CollectorUnavailable, null), range);
            check(unavailable.State == AppUsageTodayState.Unavailable && unavailable.Applications.Count == 0,
                $"{range} unavailable state remains unchanged by row icons");
        }
    }

    private static void CheckCache(string root, Action<bool, string> check)
    {
        var directory = System.IO.Path.Combine(root, "row-icon-fixtures");
        System.IO.Directory.CreateDirectory(directory);
        var path = System.IO.Path.Combine(directory, "owned-icon-fixture.exe");
        System.IO.File.WriteAllBytes(path, [0]);
        var thread = Environment.CurrentManagedThreadId;
        var calls = 0;
        var extractorThread = 0;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var cache = new AppUsageIconCache(_ =>
        {
            Interlocked.Increment(ref calls);
            extractorThread = Environment.CurrentManagedThreadId;
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            return Icon(Colors.CornflowerBlue, freeze: false);
        });
        var fallback = cache.GetFallback(AppUsageIconKind.Application);
        check(fallback.IsFrozen && cache.GetFallback(AppUsageIconKind.System).IsFrozen && cache.GetFallback(AppUsageIconKind.Unknown).IsFrozen,
            "Application, System and Unknown fallbacks are frozen for safe binding");
        check(!ReferenceEquals(fallback, cache.GetFallback(AppUsageIconKind.System)) &&
            !ReferenceEquals(fallback, cache.GetFallback(AppUsageIconKind.Unknown)) &&
            !ReferenceEquals(cache.GetFallback(AppUsageIconKind.System), cache.GetFallback(AppUsageIconKind.Unknown)),
            "Application, System and Unknown use distinct cached fallback sources");
        var requests = Enumerable.Range(0, 24).Select(index => cache.GetIconAsync(index % 2 == 0 ? path : path.ToUpperInvariant())).ToArray();
        try
        {
            Wait(() => entered.IsSet);
            check(calls == 1 && requests.All(task => !task.IsCompleted),
                "Concurrent repeated range refreshes share one extraction for a normalized executable path");
            var dispatcherResponsive = false;
            Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => dispatcherResponsive = true), DispatcherPriority.Normal);
            Pump();
            check(dispatcherResponsive && requests.All(task => !task.IsCompleted),
                "A blocked icon extractor leaves the WPF dispatcher responsive");
            check(extractorThread != thread, "File metadata and icon extraction run away from the WPF thread");
        }
        finally { release.Set(); }
        var results = Complete(Task.WhenAll(requests));
        check(results.All(source => source.IsFrozen && ReferenceEquals(source, results[0])) && !ReferenceEquals(results[0], fallback),
            "Successful extraction freezes its source and shares it across pending rows");
        check(ReferenceEquals(Complete(cache.GetIconAsync(path)), results[0]) && calls == 1,
            "Completed icon extraction is cached across subsequent periodic refreshes");

        foreach (var invalid in new string?[] { null, "", "relative.exe", @"\\server\share\remote.exe", @"\\?\C:\device.exe",
            System.IO.Path.Combine(directory, "missing.exe") })
        {
            check(ReferenceEquals(Complete(cache.GetIconAsync(invalid)), fallback),
                "Missing, relative or remote executable metadata uses the safe local fallback");
        }
        check(calls == 1, "Unusable executable paths never invoke the extractor");
        check(ReferenceEquals(Complete(cache.GetIconAsync(path, AppUsageIconKind.System)), cache.GetFallback(AppUsageIconKind.System)) &&
            ReferenceEquals(Complete(cache.GetIconAsync(path, AppUsageIconKind.Unknown)), cache.GetFallback(AppUsageIconKind.Unknown)) && calls == 1,
            "System and Unknown avoid shell extraction even with unexpected path metadata");

        var failedCalls = 0;
        var failed = new AppUsageIconCache(_ => { Interlocked.Increment(ref failedCalls); throw new System.IO.IOException("owned icon read failure"); });
        var failures = Complete(Task.WhenAll(Enumerable.Range(0, 12).Select(_ => failed.GetIconAsync(path))));
        check(failures.All(source => source.IsFrozen && ReferenceEquals(source, failed.GetFallback(AppUsageIconKind.Application))) && failedCalls == 1,
            "Extraction failure returns one cached frozen fallback and cannot crash a refresh");
        check(ReferenceEquals(Complete(failed.GetIconAsync(path)), failed.GetFallback(AppUsageIconKind.Application)) && failedCalls == 1,
            "Failed extraction stays cached rather than retrying every four seconds");
        var emptyCalls = 0;
        var empty = new AppUsageIconCache(_ => { Interlocked.Increment(ref emptyCalls); return null; });
        check(ReferenceEquals(Complete(empty.GetIconAsync(path)), empty.GetFallback(AppUsageIconKind.Application)) &&
            ReferenceEquals(Complete(empty.GetIconAsync(path)), empty.GetFallback(AppUsageIconKind.Application)) && emptyCalls == 1,
            "Executables without an icon cache the application fallback");

        var concurrencyPaths = Enumerable.Range(0, 9).Select(index => System.IO.Path.Combine(directory, "owned-parallel-" + index + ".exe")).ToArray();
        foreach (var fixturePath in concurrencyPaths) System.IO.File.WriteAllBytes(fixturePath, [0]);
        using var slotsEntered = new CountdownEvent(3);
        using var slotsRelease = new ManualResetEventSlim();
        var current = 0;
        var maximum = 0;
        var concurrencyGate = new object();
        var bounded = new AppUsageIconCache(_ =>
        {
            lock (concurrencyGate) { current++; maximum = Math.Max(maximum, current); }
            if (slotsEntered.CurrentCount > 0) slotsEntered.Signal();
            slotsRelease.Wait(TimeSpan.FromSeconds(10));
            lock (concurrencyGate) current--;
            return Icon(Colors.SlateBlue);
        });
        var pending = concurrencyPaths.Select(fixturePath => bounded.GetIconAsync(fixturePath)).ToArray();
        try
        {
            Wait(() => slotsEntered.IsSet);
            check(current == 3 && pending.All(task => !task.IsCompleted),
                "Many distinct executable rows keep at most three extraction workers active");
        }
        finally { slotsRelease.Set(); }
        var boundedResults = Complete(Task.WhenAll(pending));
        check(maximum == 3 && boundedResults.All(source => source.IsFrozen),
            "Queued executable icons finish under the same three-worker bound");

        var notepad = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "notepad.exe");
        if (System.IO.File.Exists(notepad))
        {
            var native = new AppUsageIconCache();
            var nativeIcon = Complete(native.GetIconAsync(notepad));
            check(nativeIcon is System.Windows.Media.Imaging.BitmapSource && nativeIcon.IsFrozen &&
                !ReferenceEquals(nativeIcon, native.GetFallback(AppUsageIconKind.Application)),
                "An existing local Windows executable yields a frozen native icon without launch or elevation");
        }
    }

    private static void CheckRows(Action<bool, string> check)
    {
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try { CheckRowsOnDispatcher(check); }
        finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
    }

    private static void CheckRowsOnDispatcher(Action<bool, string> check)
    {
        var cache = new HeldCache();
        foreach (var kind in new[] { AppUsageIconKind.Application, AppUsageIconKind.System, AppUsageIconKind.Unknown })
        {
            var row = new AppUsageApplicationRow("owned-" + kind, "Owned application", "owned.exe", "1 KB", "2 KB", "3 KB")
            { IconKind = kind, ExecutablePath = @"C:\Owned\owned.exe" };
            var notifications = 0;
            var notificationThread = 0;
            ((INotifyPropertyChanged)row).PropertyChanged += (_, args) =>
            {
                if (args.PropertyName != nameof(row.Icon)) return;
                notifications++;
                notificationThread = Environment.CurrentManagedThreadId;
            };
            var loading = row.LoadIconAsync(cache);
            check(ReferenceEquals(row.Icon, cache.GetFallback(kind)) && !loading.IsCompleted,
                $"{kind} row binds a visible fallback immediately while icon work is pending");
            var icon = Icon(Colors.MediumSeaGreen);
            cache.Complete(icon);
            Complete(loading);
            check(ReferenceEquals(row.Icon, icon) && notifications > 0 && notificationThread == Environment.CurrentManagedThreadId,
                $"{kind} completed icon raises row binding notification on the owning dispatcher");
            check(row.ApplicationIdentity == "owned-" + kind && row.DisplayName == "Owned application" &&
                row.DownloadText == "1 KB" && row.UploadText == "2 KB" && row.TotalText == "3 KB",
                $"{kind} asynchronous icon completion preserves identity, names and all byte amounts");
            cache.Reset();
        }
    }

    private static void CheckWindow(string root, Action<bool, string> check)
    {
        using var monitoring = HistoryOverviewChecks.CreateMonitoring(new UsageStore(System.IO.Path.Combine(root, "row-icons-window")));
        var client = new FixtureClient();
        var window = new MainWindow(monitoring, new OwnedStartup(), appUsageClient: client,
            appTrackingSetupService: new AppUsageSetupChecks.RunningSetupService())
            { ShowActivated = false, ShowInTaskbar = false, Opacity = 0, Width = 760, Height = 600 };
        try
        {
            window.Show();
            HistoryOverviewChecks.StopViewTimer(window);
            Layout(window);
            check(client.Ranges.Count == 0, "Row icon support makes zero App Usage requests before navigation");
            Control<TabControl>(window, "MainNavigation").SelectedItem = Control<TabItem>(window, "AppUsageTab");
            var list = Control<ItemsControl>(window, "AppUsageApplications");
            Wait(() => list.Items.Count == 12);
            Layout(window);
            var rows = list.Items.Cast<AppUsageApplicationRow>().ToArray();
            var visuals = HistoryOverviewChecks.Descendants(list).ToArray();
            var images = visuals.OfType<Image>().ToArray();
            check(images.Length == rows.Length && images.All(image => image.Source is not null && image.Source.IsFrozen &&
                image.ActualWidth > 0 && image.ActualHeight > 0 && image.ActualWidth <= 32 && image.ActualHeight <= 32),
                "Every actual application row renders a compact frozen icon at 760x600");
            var system = rows.Single(row => row.IconKind == AppUsageIconKind.System);
            var unknown = rows.Single(row => row.IconKind == AppUsageIconKind.Unknown);
            check(system.Icon is not null && unknown.Icon is not null && !ReferenceEquals(system.Icon, unknown.Icon),
                "Actual System and Unknown rows render their distinct fallback images");
            var duplicate = rows.Single(row => row.ApplicationIdentity == "path:duplicate");
            var container = (FrameworkElement)list.ItemContainerGenerator.ContainerFromItem(duplicate);
            check(HistoryOverviewChecks.Descendants(container).OfType<TextBlock>().Count(text => text.IsVisible &&
                text.Text.Trim().Equals("netmod.exe", StringComparison.OrdinalIgnoreCase)) == 1,
                "A duplicated executable name appears once in the actual rendered row");
            var friendly = rows.Single(row => row.ApplicationIdentity == "path:friendly");
            var friendlyContainer = (FrameworkElement)list.ItemContainerGenerator.ContainerFromItem(friendly);
            var friendlyText = HistoryOverviewChecks.Descendants(friendlyContainer).OfType<TextBlock>().Where(text => text.IsVisible).Select(text => text.Text).ToArray();
            check(friendlyText.Contains("Brave Browser") && friendlyText.Contains("brave.exe"),
                "Distinct friendly and executable names retain two useful visible labels");
            var scroll = Control<ScrollViewer>(window, "AppUsageScrollViewer");
            check(scroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled && scroll.ScrollableWidth < 0.01 &&
                scroll.ExtentWidth <= scroll.ViewportWidth + 0.5 && list.ActualWidth <= scroll.ViewportWidth + 0.5,
                "Long application names and icons fit 760x600 without horizontal overflow");
            var longLabels = visuals.OfType<TextBlock>().Where(text => text.Text.StartsWith("Long owned", StringComparison.Ordinal)).ToArray();
            check(longLabels.Length == 6 && longLabels.All(text => text.TextTrimming == TextTrimming.CharacterEllipsis &&
                text.ActualWidth > 0 && text.ActualWidth <= list.ActualWidth),
                "Long primary and distinct executable labels remain measured and trimmed beside icons");
            HistoryOverviewChecks.SavePreview(window, System.IO.Path.Combine(root, "app-usage-row-icon-previews", "760x600-icons-and-subtitles.png"));
            var staleCache = new HeldCache();
            var staleLoading = rows[0].LoadIconAsync(staleCache);
            foreach (var range in Ranges.Skip(1))
            {
                Control<ToggleButton>(window, range == AppUsageRange.SevenDays ? "AppUsage7DButton" : "AppUsage30DButton")
                    .SetCurrentValue(ToggleButton.IsCheckedProperty, true);
                Wait(() => client.Ranges.Last() == range && !Controller(window).IsRefreshing);
                Layout(window);
                check(Controller(window).SelectedRange == range && list.Items.Count == 12 &&
                    Control<TextBlock>(window, "AppUsageLogicalTotalText").Text == ByteFormatter.Format(Fixture().Sum(row => row.TotalBytes)),
                    $"Actual {range} switching retains icons, rows and unchanged selected-range totals");
                if (range == AppUsageRange.SevenDays)
                {
                    var lateIcon = Icon(Colors.OrangeRed);
                    staleCache.Complete(lateIcon);
                    Complete(staleLoading);
                    var currentRow = (AppUsageApplicationRow)list.Items[0];
                    check(ReferenceEquals(rows[0].Icon, lateIcon) && !ReferenceEquals(currentRow, rows[0]) &&
                        !ReferenceEquals(currentRow.Icon, lateIcon) && Controller(window).SelectedRange == AppUsageRange.SevenDays,
                        "A late Today icon cannot overwrite the newly selected 7D row or range");
                }
            }
            Control<TabControl>(window, "MainNavigation").SelectedItem = Control<TabItem>(window, "SettingsTab");
            Layout(window);
            var hiddenCalls = client.Ranges.Count;
            for (int index = 0; index < 8; index++) window.RefreshDashboard();
            Pump();
            check(client.Ranges.Count == hiddenCalls, "Hidden icon rows send zero App Usage refresh queries");
        }
        finally
        {
            window.Close();
            Wait(() => client.Disposed);
        }
    }

    private static IReadOnlyList<AppUsageApplication> Fixture()
    {
        var rows = new List<AppUsageApplication>
        {
            Application("path:friendly", "Brave Browser", "brave.exe", @"C:\Owned\Brave\brave.exe", 12_700, 842),
            Application("path:duplicate", " netmod.exe ", " NETMOD.EXE ", @"C:\Owned\netmod.exe", 9_000, 800),
            Application("path:missing", "Missing executable", null, null, 7_000, 700),
            Application("system", "System", null, null, 6_000, 600, system: true),
            Application("unknown", "Unknown / Unattributed", null, null, 5_000, 500, unknown: true),
            Application("path:exe-name", "", "powershell.exe", @"C:\Owned\powershell.exe", 4_000, 400),
        };
        for (int index = 0; index < 3; index++) rows.Add(Application("path:long-" + index,
            "Long owned application " + index + " " + new string('W', 160),
            "Long owned executable " + index + " " + new string('x', 120) + ".exe", null, 3_000 - index, 300));
        for (int index = 0; index < 3; index++) rows.Add(Application("path:other-" + index,
            "Owned application " + index, "owned-" + index + ".exe", null, 2_000 - index, 200));
        return rows;
    }

    private static AppUsageApplication Application(string identity, string name, string? executable, string? path,
        long download, long upload, bool system = false, bool unknown = false) =>
        new(identity, name, executable, path, download, upload, download + upload, system, unknown);
    private static AppUsageQueryResult Result(IReadOnlyList<AppUsageApplication> rows, AppUsageRange range) =>
        AppUsageTodayChecks.Result(rows) with { Range = range };
    private static AppUsageTodayPresentation Presentation(IReadOnlyList<AppUsageApplication> rows, AppUsageRange range) =>
        AppUsageTodayPresentation.FromResult(new(IpcState.Connected, Result(rows, range)), range);
    private static DrawingImage Icon(Color color, bool freeze = true)
    {
        var source = new DrawingImage(new GeometryDrawing(new SolidColorBrush(color), null,
            new RectangleGeometry(new Rect(0, 0, 16, 16))));
        if (freeze) source.Freeze();
        return source;
    }
    private static T Complete<T>(Task<T> task) { Wait(() => task.IsCompleted); return task.GetAwaiter().GetResult(); }
    private static void Complete(Task task) { Wait(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static T Control<T>(MainWindow window, string name) where T : class => window.FindName(name) as T ??
        throw new InvalidOperationException("Missing row icon control " + name);
    private static AppUsageTodayController Controller(MainWindow window) => (AppUsageTodayController)typeof(MainWindow)
        .GetField("_appUsageToday", PrivateInstance)!.GetValue(window)!;
    private static void Layout(MainWindow window) { window.UpdateLayout(); Pump(); window.UpdateLayout(); }
    private static void Pump() => HistoryOverviewChecks.Pump();
    private static void Wait(Func<bool> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException("Owned App Usage icon condition was not ready.");
            Pump();
        }
        Pump();
    }

    private sealed class HeldCache : IAppUsageIconCache
    {
        private TaskCompletionSource<ImageSource> _pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ImageSource GetFallback(AppUsageIconKind kind) => AppUsageIconCache.Default.GetFallback(kind);
        public Task<ImageSource> GetIconAsync(string? path, AppUsageIconKind kind = AppUsageIconKind.Application) => _pending.Task;
        public void Complete(ImageSource source) => _pending.TrySetResult(source);
        public void Reset() => _pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class FixtureClient : IAppUsageTodayClient
    {
        public List<AppUsageRange> Ranges { get; } = [];
        public bool Disposed { get; private set; }
        public Task<IpcResult<AppUsageQueryResult>> GetAppUsageRangeAsync(AppUsageRange range, CancellationToken cancellationToken)
        {
            Ranges.Add(range);
            return Task.FromResult(new IpcResult<AppUsageQueryResult>(IpcState.Connected, Result(Fixture(), range)));
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
    private sealed class OwnedStartup : IWindowsStartupRegistration
    {
        public bool IsEnabled() => false;
        public void Enable() => throw new InvalidOperationException("Row icons must not change startup.");
        public void Disable() => throw new InvalidOperationException("Row icons must not change startup.");
    }
}
