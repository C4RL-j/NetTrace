using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NetTrace;
using NetTrace.Controls;
using NetTrace.Models;
using NetTrace.Services;

internal static class ScrollingChecks
{
    public static void Run(string root, Action<bool, string> check)
    {
        using var monitoring = new MonitoringService(new UsageStore(System.IO.Path.Combine(root, "scrolling")),
            new OwnedIdentity(), networkMonitor: new OwnedCounters(),
            vpnVerifier: new(new VpnDetectionService(new NoTunnel()), new NoRoutes()),
            icmpMonitor: new(new OwnedPing()), reachabilityMonitor: TestReachability.Create());
        using var radio = new WifiRadioService(new NoRadio());
        var shell = new MainWindow(monitoring, new OwnedStartup(), radio, new AppUsageTodayChecks.UnavailableClient(),
            appTrackingSetupService: new AppUsageSetupChecks.RunningSetupService())
            { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
        Window? host = null;
        try
        {
            shell.Show();
            StopViewTimer(shell);
            check(shell.Width == 760 && shell.MinWidth == 760 && shell.MinHeight == 600 &&
                shell.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip,
                "NetTrace opens at 760 DIP wide and retains its minimum size and normal resizing");
            shell.Height = 600;
            var style = (Style)shell.FindResource("PageScrollViewerStyle");
            var pages = new[] { "Dashboard", "Health", "Connections", "History", "AppUsage", "Settings" };
            var pagesValid = true;
            foreach (var name in pages)
            {
                var tab = (TabItem)shell.FindName(name + "Tab");
                ((TabControl)shell.FindName("MainNavigation")).SelectedItem = tab;
                shell.UpdateLayout();
                Pump();
                var page = (ScrollViewer)tab.Content;
                pagesValid &= ReferenceEquals(page.Style, style) && SmoothScroll.GetIsEnabled(page) &&
                    page.VerticalScrollBarVisibility == ScrollBarVisibility.Auto &&
                    page.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled && page.ScrollableWidth < 0.01;
            }
            check(pagesValid, "All six pages use the compact scrolling style without horizontal overflow at 760x600");

            var content = new StackPanel();
            for (var i = 0; i < 100; i++)
                content.Children.Add(new TextBlock { Text = $"Owned scroll row {i + 1}", Height = 40,
                    Foreground = shell.Foreground, Margin = new Thickness(18, 0, 18, 0), TextWrapping = TextWrapping.Wrap });
            var scroll = new ScrollViewer { Style = style, Content = content, Background = shell.Background };
            host = new Window { Content = scroll, Width = 760, Height = 600, Background = shell.Background,
                ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
            host.Show();
            host.UpdateLayout();
            Pump();
            var bar = (ScrollBar)scroll.Template.FindName("PART_VerticalScrollBar", scroll);
            var track = (Track)bar.Template.FindName("PART_Track", bar);
            var surface = (Border)track.Thumb.Template.FindName("ThumbSurface", track.Thumb);
            check(scroll.Template.FindName("PART_ScrollContentPresenter", scroll) is ScrollContentPresenter &&
                bar.IsVisible && bar.ActualWidth <= 12 && bar.Orientation == Orientation.Vertical &&
                track.Thumb.ActualHeight >= 18 && track.Thumb.ActualWidth <= bar.ActualWidth + 0.1 &&
                surface.CornerRadius == new CornerRadius(0) &&
                surface.Background is SolidColorBrush && surface.Opacity is >= 0.4 and <= 0.7 &&
                bar.Background is SolidColorBrush { Color.R: < 40, Color.G: < 40, Color.B: < 60 } &&
                Close(bar.Maximum, scroll.ScrollableHeight) && Close(bar.ViewportSize, scroll.ViewportHeight),
                "The compact dark vertical scrollbar has a working native track and square visible thumb");
            SavePreview(scroll, root);

            Console.WriteLine($"Owned scrolling settings: wheel lines={SystemParameters.WheelScrollLines}; client-area animation={SystemParameters.ClientAreaAnimation}.");
            var samples = new List<double>();
            scroll.ScrollChanged += (_, args) => { if (ReferenceEquals(args.OriginalSource, scroll)) samples.Add(args.VerticalOffset); };
            Wheel(scroll, -120);
            scroll.UpdateLayout();
            var immediate = scroll.VerticalOffset;
            PumpFor(350);
            var step = scroll.VerticalOffset;
            check(SystemParameters.WheelScrollLines == 0 ? Close(step, 0) : step > 0 &&
                (SystemParameters.ClientAreaAnimation ? samples.Any(offset => offset > 0.1 && offset < step - 0.1) : Close(immediate, step)),
                "Wheel scrolling renders intermediate offsets when animation is enabled and honors the Windows motion setting");
            PumpFor(80);
            check(Close(scroll.VerticalOffset, step), "A completed wheel movement settles without continuing to drift");

            Reset(scroll, 0);
            Wheel(scroll, -120);
            Wheel(scroll, -120);
            Wheel(scroll, -120);
            PumpFor(350);
            check(Close(scroll.VerticalOffset, Math.Min(step * 3, scroll.ScrollableHeight)),
                "Rapid wheel ticks accumulate their full movement instead of losing earlier ticks");

            Reset(scroll, 200);
            Wheel(scroll, -120);
            AwaitMovement(scroll, 200, step);
            var beforeReverse = scroll.VerticalOffset;
            Wheel(scroll, 120);
            PumpFor(350);
            check(Close(scroll.VerticalOffset, Math.Max(0, beforeReverse - step)),
                "Reversing the wheel changes direction from the currently visible offset");

            Reset(scroll, 0);
            Wheel(scroll, 120 * 1000);
            PumpFor(250);
            var top = scroll.VerticalOffset;
            Reset(scroll, scroll.ScrollableHeight);
            Wheel(scroll, -120 * 1000);
            PumpFor(250);
            check(Close(top, 0) && Close(scroll.VerticalOffset, scroll.ScrollableHeight),
                "Wheel movement clamps at both content boundaries");

            Reset(scroll, 200);
            Wheel(scroll, -120);
            AwaitMovement(scroll, 200, step);
            scroll.ScrollToVerticalOffset(600);
            scroll.UpdateLayout();
            PumpFor(350);
            check(Close(scroll.VerticalOffset, 600), "Direct ScrollTo calls take control and cancel pending wheel movement");

            Reset(scroll, 200);
            Wheel(scroll, -120);
            AwaitMovement(scroll, 200, step);
            var beforeKey = scroll.VerticalOffset;
            SendKey(scroll, host, Key.PageDown);
            scroll.UpdateLayout();
            Pump();
            var afterKey = scroll.VerticalOffset;
            PumpFor(350);
            check(afterKey > beforeKey && Close(scroll.VerticalOffset, afterKey),
                "Page Down remains native and cancels an unfinished wheel animation");

            Reset(scroll, 200);
            Wheel(scroll, -120);
            AwaitMovement(scroll, 200, step);
            track.Thumb.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = Mouse.PreviewMouseDownEvent });
            var beforeThumb = scroll.VerticalOffset;
            track.Thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
            track.Thumb.RaiseEvent(new DragDeltaEventArgs(0, 30) { RoutedEvent = Thumb.DragDeltaEvent });
            track.Thumb.RaiseEvent(new DragCompletedEventArgs(0, 30, false) { RoutedEvent = Thumb.DragCompletedEvent });
            scroll.UpdateLayout();
            Pump();
            var afterThumb = scroll.VerticalOffset;
            PumpFor(350);
            check(afterThumb > beforeThumb && Close(scroll.VerticalOffset, afterThumb),
                "Native thumb tracking remains usable and takes control from wheel animation");

            Reset(scroll, 0);
            var nested = new ScrollViewer { Height = 90, Content = new Border { Height = 600 },
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            content.Children.Insert(0, nested);
            host.UpdateLayout();
            Pump();
            Console.WriteLine($"Owned nested setup: outer={scroll.VerticalOffset:0.###}; inner={nested.VerticalOffset:0.###}; maximum={nested.ScrollableHeight:0.###}; size={nested.ActualWidth:0.###}x{nested.ActualHeight:0.###}.");
            Wheel(nested, -120);
            nested.UpdateLayout();
            PumpFor(350);
            Console.WriteLine($"Owned nested scrolling diagnostic: outer={scroll.VerticalOffset:0.###}; inner={nested.VerticalOffset:0.###}; inner maximum={nested.ScrollableHeight:0.###}; loaded={nested.IsLoaded}; visible={nested.IsVisible}; lines={SystemParameters.WheelScrollLines}; animation={SystemParameters.ClientAreaAnimation}; modifiers={Keyboard.Modifiers}.");
            check(Close(scroll.VerticalOffset, 0) && (SystemParameters.WheelScrollLines == 0 || nested.VerticalOffset > 0),
                "A nested scrolling control retains its wheel input without moving the outer page");
            content.Children.Remove(nested);
            host.UpdateLayout();
            Pump();

            Reset(scroll, 200);
            Wheel(scroll, -120);
            AwaitMovement(scroll, 200, step);
            SmoothScroll.SetIsEnabled(scroll, false);
            var stopped = scroll.VerticalOffset;
            PumpFor(350);
            var didStop = Close(scroll.VerticalOffset, stopped);
            Wheel(scroll, -120);
            scroll.UpdateLayout();
            Pump();
            var nativeOffset = scroll.VerticalOffset;
            PumpFor(350);
            check(didStop && (SystemParameters.WheelScrollLines == 0 || nativeOffset > stopped) && Close(scroll.VerticalOffset, nativeOffset),
                "Disabling smooth scrolling cancels it and restores immediate native wheel scrolling");
            SmoothScroll.SetIsEnabled(scroll, true);

            scroll.Content = new Border { Height = 30 };
            host.UpdateLayout();
            Pump();
            Wheel(scroll, -120);
            PumpFor(250);
            check(Close(scroll.VerticalOffset, 0) && scroll.ScrollableHeight == 0 && !bar.IsVisible,
                "A page without overflow stays still and hides its scrollbar");
            scroll.Content = content;
            host.UpdateLayout();
            Pump();

            Reset(scroll, 200);
            Wheel(scroll, -120);
            AwaitMovement(scroll, 200, step);
            host.Hide();
            Pump();
            var hiddenOffset = scroll.VerticalOffset;
            PumpFor(350);
            check(Close(scroll.VerticalOffset, hiddenOffset), "Hiding the page stops pending wheel rendering");
            host.Show();
            host.UpdateLayout();
            Pump();
            Reset(scroll, 200);
            Wheel(scroll, -120);
            AwaitMovement(scroll, 200, step);
            host.Content = null;
            Pump();
            var unloadedOffset = scroll.VerticalOffset;
            PumpFor(350);
            check(!scroll.IsLoaded && Close(scroll.VerticalOffset, unloadedOffset),
                "Unloading the page stops pending wheel rendering without retaining active animation work");
        }
        finally { host?.Close(); shell.Close(); }
    }

    private static bool Close(double value, double expected) => Math.Abs(value - expected) < 0.5;
    private static void Reset(ScrollViewer scroll, double offset) { scroll.ScrollToVerticalOffset(offset); scroll.UpdateLayout(); Pump(); }
    private static void Wheel(UIElement target, int delta)
    {
        var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
        target.RaiseEvent(args);
        if (!args.Handled) { args.RoutedEvent = Mouse.MouseWheelEvent; target.RaiseEvent(args); }
    }
    private static void SendKey(UIElement target, Visual host, Key key)
    {
        var source = PresentationSource.FromVisual(host) ?? throw new InvalidOperationException("Owned scroll host has no presentation source.");
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        target.RaiseEvent(args);
        if (!args.Handled) { args.RoutedEvent = Keyboard.KeyDownEvent; target.RaiseEvent(args); }
    }
    private static void AwaitMovement(ScrollViewer scroll, double start, double step)
    {
        if (step == 0 || !SystemParameters.ClientAreaAnimation) { scroll.UpdateLayout(); Pump(); return; }
        var elapsed = Stopwatch.StartNew();
        while (scroll.VerticalOffset <= start + 0.1 && elapsed.Elapsed < TimeSpan.FromSeconds(2)) Pump();
    }
    private static void PumpFor(int milliseconds)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.ElapsedMilliseconds < milliseconds) Pump();
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
    private static void StopViewTimer(MainWindow window) => ((DispatcherTimer)typeof(MainWindow)
        .GetField("_timer", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!).Stop();
    private static void SavePreview(FrameworkElement content, string root)
    {
        var directory = System.IO.Path.Combine(root, "scrolling-previews");
        System.IO.Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = System.IO.File.Create(System.IO.Path.Combine(directory, "760x600-owned-scrollbar.png"));
        encoder.Save(output);
    }
    private sealed class OwnedIdentity : INetworkIdentityService
    {
        public event EventHandler? NetworkChanged { add { } remove { } }
        public NetworkIdentityResult GetCurrentNetwork() => new(NetworkIdentityAvailability.Disconnected, null, null);
        public void Dispose() { }
    }
    private sealed class OwnedCounters : INetworkMonitor
    {
        public IReadOnlyList<AdapterInfo> GetActiveAdapters() => [new("owned-scroll-adapter", "Owned Ethernet", "Owned scroll fixture", "Ethernet", 1_000_000_000, "192.0.2.10", true)];
        public NetworkSnapshot? Capture(string adapterId) => new(0, 0, DateTimeOffset.UtcNow);
        public NetworkAdapterDetails? GetAdapterDetails(string adapterId) => new(adapterId, OperationalStatus.Up, [IPAddress.Parse("192.0.2.10")], [], [], 1_000_000_000);
    }
    private sealed class NoTunnel : IVpnInterfaceSource { public VpnInterfaceInventory Read() => new([], []); }
    private sealed class NoRoutes : IVpnRouteSource { public VpnRouteInventory Read(AddressFamily family) => new(family, [], Availability: IpFamilyAvailability.Unavailable); }
    private sealed class NoRadio : IWifiRadioSource { public WifiRadioSnapshot Read(string adapterId) => new(adapterId, false); }
    private sealed class OwnedPing : IPingSource { public Task<IcmpPingReply> SendAsync(IPAddress target, TimeSpan timeout, CancellationToken cancellationToken) => Task.FromResult(new IcmpPingReply(IPStatus.TimedOut)); }
    private sealed class OwnedStartup : IWindowsStartupRegistration
    {
        public bool IsEnabled() => false;
        public void Enable() => throw new InvalidOperationException("Scrolling checks do not change startup registration.");
        public void Disable() => throw new InvalidOperationException("Scrolling checks do not change startup registration.");
    }
}
