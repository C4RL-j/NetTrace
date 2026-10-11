using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NetTrace;
using NetTrace.Controls;
using NetTrace.Models;
using NetTrace.Presentation;
using NetTrace.Services;

internal static class WifiRadioChecks
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    internal const string WifiId = "11111111-1111-1111-1111-111111111111";
    private const string SecondWifiId = "22222222-2222-2222-2222-222222222222";
    private const string EthernetId = "33333333-3333-3333-3333-333333333333";
    private static readonly string[] ValueNames =
    [
        "WifiSignalQualityText", "WifiRssiText", "WifiChannelText", "WifiBandFrequencyText",
        "WifiBssidText", "WifiPhyText", "WifiReceiveLinkRateText", "WifiTransmitLinkRateText"
    ];
    private static readonly Dictionary<string, string> HelpNames = new(StringComparer.Ordinal)
    {
        ["WifiSignalQualityHelp"] = "Signal quality", ["WifiRssiHelp"] = "RSSI", ["WifiChannelHelp"] = "Channel",
        ["WifiBandFrequencyHelp"] = "Band / Frequency", ["WifiBssidHelp"] = "BSSID", ["WifiPhyHelp"] = "PHY / radio type",
        ["WifiReceiveLinkRateHelp"] = "Receive link rate", ["WifiTransmitLinkRateHelp"] = "Transmit link rate",
        ["Ipv4Help"] = "IPv4", ["Ipv6Help"] = "IPv6", ["DefaultGatewayHelp"] = "Default gateway",
        ["DnsHelp"] = "DNS", ["VpnTransportHelp"] = "VPN transport", ["LinkRateHelp"] = "Link rate"
    };

    public static void RunColorDiagnostic(string root, Action<bool, string> check)
    {
        using var fixture = new Fixture(System.IO.Path.Combine(root, "wifi-radio-color-owned"));
        var window = fixture.Window;
        fixture.Set(Complete(WifiId));
        var tab = Control<TabItem>(window, "ConnectionsTab");
        var navigation = Control<TabControl>(window, "MainNavigation");
        Console.WriteLine($"Window foreground: {DescribeBrush(window.Foreground)}");
        Console.WriteLine($"Navigation foreground: {DescribeBrush(navigation.Foreground)}; window identity: {ReferenceEquals(navigation.Foreground, window.Foreground)}");
        Console.WriteLine($"Connections tab foreground: {DescribeBrush(tab.Foreground)}; window identity: {ReferenceEquals(tab.Foreground, window.Foreground)}");
        foreach (var name in ValueNames.Concat(new[] { "Ipv4Text", "Ipv6Text", "DefaultGatewayText", "DnsText", "LinkSpeedText" }))
        {
            var value = Control<TextBlock>(window, name);
            var source = DependencyPropertyHelper.GetValueSource(value, TextBlock.ForegroundProperty);
            Console.WriteLine($"{name}: {DescribeBrush(value.Foreground)}; source: {source.BaseValueSource}; " +
                $"window identity: {ReferenceEquals(value.Foreground, window.Foreground)}; " +
                $"tab identity: {ReferenceEquals(value.Foreground, tab.Foreground)}");
        }
        foreach (var name in new[] { "AdapterStatusText", "ConnectionsVpnStatusText", "VpnTransportText", "IPv4RouteText", "IPv6RouteText" })
            Console.WriteLine($"Semantic {name}: {DescribeBrush(Control<TextBlock>(window, name).Foreground)}");
        check(HasOrdinaryTechnicalColors(window),
            "Radio measurements retain ordinary readable text without signal judgments or status coloring");
    }

    private static string DescribeBrush(Brush brush) => brush is SolidColorBrush solid
        ? $"ARGB {solid.Color} ({solid.Color.A},{solid.Color.R},{solid.Color.G},{solid.Color.B})"
        : brush.GetType().Name;

    private static bool HasOrdinaryTechnicalColors(MainWindow window)
    {
        var expected = Color.FromArgb(255, 248, 250, 252);
        var palette = new[] { "StatusPositiveBrush", "StatusCautionBrush", "StatusNegativeBrush", "StatusInfoBrush", "StatusNeutralBrush" }
            .Select(name => ((SolidColorBrush)window.FindResource(name)).Color).ToArray();
        var rawNames = ValueNames.Concat(new[] { "Ipv4Text", "Ipv6Text", "DefaultGatewayText", "DnsText", "LinkSpeedText" });
        // These controls inherit through the selected TabItem. Equal ARGB and
        // opacity establish the displayed color; separately allocated theme
        // brushes need not have the Window's object identity.
        return rawNames.All(name => Control<TextBlock>(window, name).Foreground is SolidColorBrush brush &&
            brush.Color == expected && brush.Opacity == 1 && !palette.Contains(brush.Color)) &&
            Control<TabItem>(window, "ConnectionsTab").Foreground is SolidColorBrush tabBrush && tabBrush.Color == expected &&
            Contrast(expected, ((SolidColorBrush)window.FindResource("CardBrush")).Color) >= 4.5 &&
            Control<TextBlock>(window, "AdapterStatusText").Foreground is SolidColorBrush status &&
            status.Color == ((SolidColorBrush)window.FindResource("StatusPositiveBrush")).Color && status.Color != expected;
    }

    private static double Contrast(Color foreground, Color background)
    {
        static double Linear(byte component)
        {
            var value = component / 255d;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        static double Luminance(Color color) => 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
        var first = Luminance(foreground);
        var second = Luminance(background);
        return (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
    }

    public static void Run(string root, Action<bool, string> check)
    {
        using var fixture = new Fixture(System.IO.Path.Combine(root, "wifi-radio-owned"));
        var window = fixture.Window;
        var complete = Complete(WifiId);
        fixture.Set(complete);
        check(Values(window).SequenceEqual(new[] { "82%", "-58 dBm", "44", "5 GHz • 5220 MHz",
            "AA:BB:CC:DD:EE:FF", "802.11ac", "433 Mbps", "390 Mbps" }) &&
            Control<StackPanel>(window, "WifiRadioSection").IsVisible,
            "Connected Wi-Fi renders all eight native radio values with independent receive and transmit rates");
        check(Control<StackPanel>(window, "ConnectionsContent").Children.OfType<Border>().Count() == 2 &&
            Control<Grid>(window, "WifiRadioGrid").RowDefinitions.Count == 8 &&
            ValueNames.Select(name => Grid.GetRow(Control<TextBlock>(window, name))).SequenceEqual(Enumerable.Range(0, 8)),
            "Wi-Fi radio is a compact subsection inside the existing monitored-adapter card and preserves both Connections cards");
        check(Control<TextBlock>(window, "LinkSpeedText").Visibility == Visibility.Collapsed &&
            Control<TextBlock>(window, "LinkRateLabel").Visibility == Visibility.Collapsed &&
            Control<NetworkHelpButton>(window, "LinkRateHelp").Visibility == Visibility.Collapsed &&
            Control<StackPanel>(window, "LinkRateLabelPanel").Visibility == Visibility.Collapsed,
            "Two valid negotiated rates cleanly supersede the general adapter link-rate row without duplicate information");
        check(HasOrdinaryTechnicalColors(window),
            "Radio measurements retain ordinary readable text without signal judgments or status coloring");
        SavePreview(window, root, "connected-complete");

        foreach (var signal in new[] { 0, 100, 67 })
        {
            fixture.Set(complete with { SignalQualityPercent = signal });
            check(Text(window, "WifiSignalQualityText") == signal + "%",
                $"Signal quality {signal} percent is shown without a good or bad judgment");
        }
        fixture.Set(complete with { RssiDbm = null });
        check(Text(window, "WifiRssiText") == "Unavailable" && Text(window, "WifiSignalQualityText") == "82%",
            "Missing actual RSSI remains Unavailable rather than being converted from signal quality");
        SavePreview(window, root, "missing-rssi");

        foreach (var band in new[] { (2412, 1, "2.4 GHz • 2412 MHz"), (5180, 36, "5 GHz • 5180 MHz"),
            (5955, 1, "6 GHz • 5955 MHz") })
        {
            fixture.Set(complete with { FrequencyMHz = band.Item1, Channel = band.Item2 });
            check(Text(window, "WifiBandFrequencyText") == band.Item3 && Text(window, "WifiChannelText") == band.Item2.ToString(),
                $"The selected Wi-Fi interface displays the expected channel and {band.Item3} band/frequency");
        }
        fixture.Set(complete with { FrequencyMHz = 2412, Channel = 1 });
        check(Text(window, "WifiPhyText") == "802.11ac" && Text(window, "WifiBandFrequencyText") == "2.4 GHz • 2412 MHz" &&
            Text(window, "WifiChannelText") == "1" &&
            ((TextBlock)Control<NetworkHelpButton>(window, "WifiPhyHelp").HelpToolTip.Content).Text.Contains(
                "Windows reports for the current connection", StringComparison.Ordinal) &&
            NetworkHelpText.Get("PHY / radio type").Contains("does not perfectly match the current band or frequency", StringComparison.Ordinal),
            "Current-association PHY remains Windows-reported 802.11ac when native 2.4 GHz metadata differs, with an accurate driver explanation");
        fixture.Set(complete with { Channel = null, FrequencyMHz = null, Bssid = null, PhyType = null });
        check(new[] { "WifiChannelText", "WifiBandFrequencyText", "WifiBssidText", "WifiPhyText" }
                .All(name => Text(window, name) == "Unavailable") && Control<StackPanel>(window, "WifiRadioSection").IsVisible,
            "Unavailable channel, frequency, BSSID and PHY retain compact rows while other useful Wi-Fi data remains");
        foreach (var phy in new[] { (7u, "802.11n"), (8u, "802.11ac"), (10u, "802.11ax") })
        {
            fixture.Set(complete with { PhyType = phy.Item1 });
            check(Text(window, "WifiPhyText") == phy.Item2, $"The UI displays friendly {phy.Item2} rather than a raw native enum name");
        }
        fixture.Set(complete with { PhyType = uint.MaxValue });
        check(Text(window, "WifiPhyText") == "Unavailable", "An unsupported PHY does not expose a numeric native value");

        foreach (var rates in new[] { ((long?)433_000_000L, (long?)null), ((long?)null, (long?)390_000_000L),
            ((long?)null, (long?)null) })
        {
            fixture.Set(complete with { ReceiveLinkBitsPerSecond = rates.Item1, TransmitLinkBitsPerSecond = rates.Item2 });
            check(Text(window, "WifiReceiveLinkRateText") == (rates.Item1.HasValue ? "433 Mbps" : "Unavailable") &&
                Text(window, "WifiTransmitLinkRateText") == (rates.Item2.HasValue ? "390 Mbps" : "Unavailable") &&
                Control<TextBlock>(window, "LinkSpeedText").IsVisible && Text(window, "LinkSpeedText") == "86 Mbps",
                "Missing separate Wi-Fi rates preserve the working general link-rate fallback without fabricating values");
        }
        fixture.Set(complete with { ReceiveLinkBitsPerSecond = 1_200_000_000, TransmitLinkBitsPerSecond = 867_000_000 });
        check(Text(window, "WifiReceiveLinkRateText") == "1.2 Gbps" && Text(window, "WifiTransmitLinkRateText") == "867 Mbps",
            "Different negotiated rates use readable Gbps and Mbps units");

        foreach (var definition in HelpNames)
        {
            var help = Control<NetworkHelpButton>(window, definition.Key);
            var text = (TextBlock)help.HelpToolTip.Content;
            check(help.Term == definition.Value && !string.IsNullOrWhiteSpace(text.Text) &&
                text.Text == NetworkHelpText.Get(definition.Value) && help.ToolTip == help.HelpToolTip &&
                help.Content?.ToString() == "ⓘ" && help.Focusable &&
                AutomationProperties.GetName(help) == "Help for " + definition.Value,
                $"The {definition.Value} label has its correct centralized explanation, visible information cue and keyboard-accessible name");
            check(text.TextWrapping == TextWrapping.Wrap && text.MaxWidth is >= 260 and <= 340 &&
                double.IsFinite(text.MaxWidth) && double.IsFinite(help.HelpToolTip.MaxWidth),
                $"The {definition.Value} help text wraps within a finite compact maximum width");
        }
        // Keep the general link-rate fallback visible so all fourteen label
        // cues are measured rather than silently skipping its collapsed row.
        fixture.Set(complete with { TransmitLinkBitsPerSecond = null });
        foreach (var definition in HelpNames)
            check(LabelCueIsAdjacent(window, definition.Key, definition.Value),
                $"The {definition.Value} information cue follows its own label compactly without overlapping the value column");
        check(NetworkHelpText.Get("BSSID").Contains("different from the Wi-Fi network name (SSID)", StringComparison.Ordinal) &&
            NetworkHelpText.Get("Receive link rate").Contains("not the same as your actual internet download speed", StringComparison.Ordinal) &&
            NetworkHelpText.Get("Transmit link rate").Contains("not the same as your actual internet upload speed", StringComparison.Ordinal),
            "Beginner help distinguishes BSSID from SSID and local link rates from actual internet speeds");

        fixture.Set(complete);
        fixture.Counters.LongIpv6 = true;
        fixture.RefreshAdapterDetails();
        check(Text(window, "Ipv6Text") == "2001:db8:abcd:1234:5678:90ab:cdef:1234" &&
            Control<TextBlock>(window, "Ipv6Text").TextTrimming == TextTrimming.CharacterEllipsis &&
            Control<StackPanel>(window, "WifiRadioSection").IsVisible &&
            new[] { "ConnectionsVpnStatusText", "VpnTransportText", "IPv4RouteText", "IPv6RouteText", "PhysicalAdapterText" }
                .All(name => Control<TextBlock>(window, name).ActualHeight >= 14),
            "Long IPv6 and the Wi-Fi subsection preserve every existing VPN transport and family-route row");
        var tooltipStyle = (Style)Control<StackPanel>(window, "ConnectionsContent").FindResource(typeof(ToolTip));
        var stringTemplate = (DataTemplate)tooltipStyle.Setters.OfType<Setter>()
            .Single(setter => setter.Property == ContentControl.ContentTemplateProperty).Value;
        var wrappedString = (TextBlock)stringTemplate.LoadContent();
        check(Control<TextBlock>(window, "Ipv6Text").ToolTip is string &&
            Control<TextBlock>(window, "LinkSpeedText").ToolTip is string &&
            wrappedString.TextWrapping == TextWrapping.Wrap && wrappedString.MaxWidth == 300 &&
            tooltipStyle.Setters.OfType<Setter>().Any(setter => setter.Property == FrameworkElement.MaxWidthProperty &&
                setter.Value is double width && width == 322) &&
            Control<NetworkHelpButton>(window, "WifiRssiHelp").HelpToolTip.ReadLocalValue(ContentControl.ContentTemplateProperty) is null,
            "Existing IPv6 and link-rate tooltip strings retain their content and use the same finite wrapped presentation as beginner help");
        SavePreview(window, root, "long-ipv6-vpn-rows");
        CheckHelpPreview(window, root, "WifiRssiHelp", "hover-rssi", check);
        CheckHelpPreview(window, root, "WifiBssidHelp", "hover-bssid", check);

        var keyboardHelp = Control<NetworkHelpButton>(window, "WifiBssidHelp");
        Keyboard.Focus(keyboardHelp);
        Pump();
        check(keyboardHelp.HelpToolTip.IsOpen, "Keyboard focus opens the same BSSID explanation without requiring hover");
        keyboardHelp.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)
            ?? throw new InvalidOperationException("The owned Wi-Fi window has no presentation source."), 0, Key.Escape)
            { RoutedEvent = Keyboard.KeyDownEvent });
        check(!keyboardHelp.HelpToolTip.IsOpen, "Escape dismisses the focused help tooltip");
        // Invoking OnClick exercises the control behavior rather than merely a routed notification.
        typeof(NetworkHelpButton).GetMethod("OnClick", PrivateInstance)!.Invoke(keyboardHelp, null);
        check(keyboardHelp.HelpToolTip.IsOpen, "Click opens the same compact help text without a separate documentation window");
        Keyboard.Focus(Control<ComboBox>(window, "AdapterComboBox"));
        Pump();
        check(!keyboardHelp.HelpToolTip.IsOpen, "Moving keyboard focus away dismisses clicked help");

        fixture.Select(SecondWifiId, Complete(SecondWifiId) with { Bssid = "02:23:45:67:89:AB", Channel = 6,
            FrequencyMHz = 2437, SignalQualityPercent = 55 });
        check(Text(window, "WifiBssidText") == "02:23:45:67:89:AB" && Text(window, "WifiChannelText") == "6" &&
            Text(window, "WifiSignalQualityText") == "55%" && fixture.Radio.Current?.AdapterId == SecondWifiId,
            "Selecting a second Wi-Fi adapter displays only the selected interface's radio data");
        fixture.Select(WifiId, complete);
        check(Text(window, "WifiBssidText") == "AA:BB:CC:DD:EE:FF" && Text(window, "WifiChannelText") == "44",
            "Returning to the first Wi-Fi adapter restores its own current BSSID and channel");

        fixture.Counters.WifiStatus = OperationalStatus.Down;
        fixture.RefreshAdapterDetails();
        check(Text(window, "AdapterStatusText") == "Disconnected" &&
            Control<StackPanel>(window, "WifiRadioSection").Visibility == Visibility.Collapsed &&
            Values(window).All(value => value == "Unavailable") && fixture.Radio.Current is null,
            "Disconnected Wi-Fi hides radio details and clears every previous BSSID, channel and signal value");
        SavePreview(window, root, "disconnected-wifi");
        fixture.Counters.WifiStatus = OperationalStatus.Up;
        fixture.RefreshAdapterDetails();
        fixture.Set(complete);
        Control<ComboBox>(window, "AdapterComboBox").SelectedItem = fixture.Monitoring.GetSnapshot().Adapters.Single(a => a.Id == EthernetId);
        window.RefreshDashboard();
        check(Text(window, "AdapterTypeText") == "Ethernet" && Text(window, "Ipv4Text") == "198.51.100.20" &&
            Control<StackPanel>(window, "WifiRadioSection").Visibility == Visibility.Collapsed &&
            Values(window).All(value => value == "Unavailable") && Control<TextBlock>(window, "LinkSpeedText").IsVisible,
            "Ethernet preserves its existing adapter fields without an empty Wi-Fi block or stale wireless values");
        SavePreview(window, root, "ethernet-no-radio");
        fixture.Select(WifiId, complete);

        window.Width = 760;
        window.Height = 600;
        window.UpdateLayout();
        var scroll = (ScrollViewer)Control<TabItem>(window, "ConnectionsTab").Content;
        scroll.ScrollToBottom();
        window.UpdateLayout();
        Pump();
        check(scroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled && scroll.ScrollableWidth < 0.01 &&
            ValueNames.All(name => Control<TextBlock>(window, name).ActualHeight >= 14) &&
            Control<TextBlock>(window, "WifiBssidText").ActualWidth > 200,
            "At 760 by 600, the radio subsection remains readable in the existing vertical scroll without horizontal expansion");
        check(HelpNames.Where(definition => Control<NetworkHelpButton>(window, definition.Key).IsVisible)
                .All(definition => LabelCueIsAdjacent(window, definition.Key, definition.Value)) &&
            new[] { "ConnectionRouteGrid", "AdapterDetailsGrid", "WifiRadioGrid" }.All(name =>
            {
                var grid = Control<Grid>(window, name);
                return double.IsFinite(grid.ActualWidth) && grid.ActualWidth <= scroll.ViewportWidth + 0.5;
            }) && scroll.ScrollableWidth < 0.01,
            "At minimum window size, every visible help cue stays beside its label inside the fixed left column and introduces no horizontal overflow");
        SavePreview(window, root, "760x600-complete");
        CheckHelpPreview(window, root, "WifiRssiHelp", "760x600-hover-rssi", check);
        CheckHelpPreview(window, root, "WifiBssidHelp", "760x600-hover-bssid", check);

        var sourceReads = fixture.Source.Reads;
        SelectPage(window, "Dashboard");
        fixture.Clock.Advance();
        window.RefreshDashboard();
        check(fixture.Source.Reads == sourceReads && fixture.Radio.Current is null,
            "Leaving Connections invalidates its presentation-only cache and performs no hidden-page WLAN query");
        SelectPage(window, "Connections");
        fixture.WaitFor(complete);
        var viewTimer = Field<DispatcherTimer>(window, "_timer");
        sourceReads = fixture.Source.Reads;
        window.Hide();
        fixture.Clock.Advance();
        window.RefreshDashboard();
        check(!viewTimer.IsEnabled && fixture.Source.Reads == sourceReads && fixture.Radio.Current is null,
            "A hidden window stops the existing presentation timer and invalidates Wi-Fi data without background WLAN polling");
        window.Show();
        viewTimer.Stop();
        fixture.WaitFor(complete);
        window.WindowState = WindowState.Minimized;
        sourceReads = fixture.Source.Reads;
        fixture.Clock.Advance();
        window.RefreshDashboard();
        check(!viewTimer.IsEnabled && fixture.Source.Reads == sourceReads && fixture.Radio.Current is null,
            "Minimizing clears Wi-Fi radio data and avoids additional WLAN queries while tray monitoring continues");
        window.WindowState = WindowState.Normal;
        viewTimer.Stop();
        fixture.WaitFor(complete);
        fixture.Monitoring.Suspend();
        check(fixture.Radio.Current is null,
            "Suspend invalidates cached radio data synchronously before pending dispatcher presentation can run");
        window.RefreshDashboard();
        check(fixture.Radio.Current is null && Control<StackPanel>(window, "WifiRadioSection").Visibility == Visibility.Collapsed &&
            Values(window).All(value => value == "Unavailable"),
            "A suspended monitoring snapshot clears radio measurements and invalidates the previous association cache");
        var resumed = complete with { SignalQualityPercent = 73, Bssid = "0A:12:34:56:78:9A" };
        fixture.Source.Set(resumed);
        fixture.Clock.Advance();
        fixture.Monitoring.Resume();
        Field<System.Threading.Timer>(fixture.Monitoring, "_timer").Change(Timeout.Infinite, Timeout.Infinite);
        fixture.WaitFor(resumed);
        check(Text(window, "WifiSignalQualityText") == "73%" && Text(window, "WifiBssidText") == "0A:12:34:56:78:9A" &&
            Control<StackPanel>(window, "WifiRadioSection").IsVisible,
            "Resume obtains fresh radio data through the existing view cadence without restoring the pre-sleep BSSID or signal");
        check(fixture.Source.ReadThreads.All(id => id != Environment.CurrentManagedThreadId),
            "Structured WLAN discovery runs off the UI thread and leaves MainWindow responsible only for presentation");
    }

    internal static WifiRadioSnapshot Complete(string id) => new(id, true)
    {
        SignalQualityPercent = 82, RssiDbm = -58, Channel = 44, FrequencyMHz = 5220,
        Bssid = "AA:BB:CC:DD:EE:FF", PhyType = 8, ReceiveLinkBitsPerSecond = 433_000_000,
        TransmitLinkBitsPerSecond = 390_000_000
    };

    private static bool LabelCueIsAdjacent(MainWindow window, string name, string term)
    {
        var help = Control<NetworkHelpButton>(window, name);
        if (help.Parent is not StackPanel panel || panel.Parent is not Grid grid ||
            panel.Orientation != Orientation.Horizontal || panel.Children.Count != 2 ||
            panel.Children[0] is not TextBlock label || !ReferenceEquals(panel.Children[1], help) ||
            !help.IsVisible || Grid.GetColumn(panel) != 0) return false;
        var labelText = term == "Link rate" ? "Wi-Fi link rate" : term;
        var valueName = name == "LinkRateHelp" ? "LinkSpeedText" : name.Replace("Help", "Text", StringComparison.Ordinal);
        var value = Control<TextBlock>(window, valueName);
        if (label.Text != labelText || Grid.GetColumn(value) != 1 || Grid.GetRow(panel) != Grid.GetRow(value)) return false;
        var labelBounds = label.TransformToAncestor(grid).TransformBounds(new Rect(0, 0, label.ActualWidth, label.ActualHeight));
        var helpBounds = help.TransformToAncestor(grid).TransformBounds(new Rect(0, 0, help.ActualWidth, help.ActualHeight));
        var valueBounds = value.TransformToAncestor(grid).TransformBounds(new Rect(0, 0, value.ActualWidth, value.ActualHeight));
        var gap = helpBounds.Left - labelBounds.Right;
        return label.ActualWidth > 0 && help.ActualWidth >= 16 && value.ActualWidth > 0 &&
            gap is >= 2 and <= 12 && helpBounds.Right <= grid.ColumnDefinitions[0].ActualWidth + 0.5 &&
            helpBounds.Right <= valueBounds.Left + 0.5 && panel.ActualWidth <= grid.ColumnDefinitions[0].ActualWidth + 0.5;
    }
    private static void CheckHelpPreview(MainWindow window, string root, string name, string preview, Action<bool, string> check)
    {
        var help = Control<NetworkHelpButton>(window, name);
        var scroll = (ScrollViewer)Control<TabItem>(window, "ConnectionsTab").Content;
        help.BringIntoView();
        window.UpdateLayout();
        Pump();
        var width = scroll.ExtentWidth;
        help.HelpToolTip.IsOpen = true;
        Pump();
        help.HelpToolTip.UpdateLayout();
        var text = (TextBlock)help.HelpToolTip.Content;
        check(help.HelpToolTip.IsOpen && text.ActualWidth <= 300.1 && text.ActualHeight > 30 &&
            help.HelpToolTip.ActualWidth <= 322.1 && Math.Abs(scroll.ExtentWidth - width) < 0.01,
            $"The {help.Term} hover tooltip wraps compactly without widening the Connections page at {window.Width} by {window.Height}");
        SaveElement(help.HelpToolTip, root, preview + "-tooltip");
        SavePreview(window, root, preview + "-page");
        help.HelpToolTip.IsOpen = false;
    }
    internal static string SavePreview(MainWindow window, string root, string name)
    {
        window.UpdateLayout();
        var scroll = (ScrollViewer)Control<TabItem>(window, "ConnectionsTab").Content;
        var original = scroll.VerticalOffset;
        SaveElement(window, root, name + "-page");
        scroll.ScrollToBottom();
        window.UpdateLayout();
        Pump();
        var path = SaveElement(window, root, name + "-radio");
        scroll.ScrollToVerticalOffset(original);
        window.UpdateLayout();
        return path;
    }
    private static string SaveElement(FrameworkElement element, string root, string name)
    {
        element.UpdateLayout();
        var directory = System.IO.Path.Combine(root, "wifi-radio-previews");
        System.IO.Directory.CreateDirectory(directory);
        var path = System.IO.Path.Combine(directory, name + ".png");
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(element.ActualWidth)),
            Math.Max(1, (int)Math.Ceiling(element.ActualHeight)), 96, 96, PixelFormats.Pbgra32);
        if (element is MainWindow window)
        {
            var content = (FrameworkElement)window.Content;
            var margin = content.Margin;
            var drawing = new DrawingVisual();
            using (var context = drawing.RenderOpen())
            {
                context.DrawRectangle(window.Background, null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
                context.DrawRectangle(new VisualBrush(content), null,
                    new Rect(margin.Left, margin.Top, content.ActualWidth, content.ActualHeight));
            }
            bitmap.Render(drawing);
        }
        else bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = System.IO.File.Create(path);
        encoder.Save(output);
        return path;
    }
    private static string[] Values(MainWindow window) => ValueNames.Select(name => Text(window, name)).ToArray();
    private static string Text(MainWindow window, string name) => Control<TextBlock>(window, name).Text;
    private static T Control<T>(Window window, string name) where T : class => window.FindName(name) as T
        ?? throw new InvalidOperationException($"Missing Wi-Fi radio check control {name}.");
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, PrivateInstance)!.GetValue(owner)!;
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
    private static void SelectPage(MainWindow window, string name)
    {
        Control<TabControl>(window, "MainNavigation").SelectedItem = Control<TabItem>(window, name + "Tab");
        window.UpdateLayout();
        window.RefreshDashboard();
        Pump();
    }
    private static void Wait(Func<bool> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Owned Wi-Fi radio state did not arrive.");
            Pump();
        }
        Pump();
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
    internal sealed class OwnedRadio : IWifiRadioSource
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, WifiRadioSnapshot> _values = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<int> _threads = [];
        public int Reads { get { lock (_gate) return _threads.Count; } }
        public IReadOnlyList<int> ReadThreads { get { lock (_gate) return _threads.ToArray(); } }
        public void Set(WifiRadioSnapshot radio) { lock (_gate) _values[radio.AdapterId] = radio; }
        public WifiRadioSnapshot Read(string adapterId)
        {
            lock (_gate)
            {
                _threads.Add(Environment.CurrentManagedThreadId);
                return _values.GetValueOrDefault(adapterId) ?? new(adapterId, false);
            }
        }
    }
    private sealed class Fixture : IDisposable
    {
        public ControlledClock Clock { get; } = new();
        public OwnedRadio Source { get; } = new();
        public OwnedCounters Counters { get; } = new();
        public WifiRadioService Radio { get; }
        public MonitoringService Monitoring { get; }
        public MainWindow Window { get; }
        public Fixture(string root)
        {
            Source.Set(Complete(WifiId));
            Radio = new(Source, Clock);
            Monitoring = new(new UsageStore(root), new OwnedIdentity(),
                vpnVerifier: new(new VpnDetectionService(new NoTunnel()), new NoRoutes()), networkMonitor: Counters,
                icmpMonitor: new(new OwnedPing()), reachabilityMonitor: TestReachability.Create());
            Monitoring.Start();
            Field<System.Threading.Timer>(Monitoring, "_timer").Change(Timeout.Infinite, Timeout.Infinite);
            Wait(() => Monitoring.GetSnapshot().SelectedAdapterDetails is not null);
            Window = new(Monitoring, new ReadOnlyStartup(), Radio) { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
            Window.Show();
            Field<DispatcherTimer>(Window, "_timer").Stop();
            SelectPage(Window, "Connections");
            WaitFor(Complete(WifiId));
        }
        public void Set(WifiRadioSnapshot radio)
        {
            Source.Set(radio);
            Clock.Advance();
            Window.RefreshDashboard();
            WaitFor(radio);
        }
        public void WaitFor(WifiRadioSnapshot radio)
        {
            Wait(() => { Window.RefreshDashboard(); return Radio.Current == radio; });
            Window.RefreshDashboard();
            Window.UpdateLayout();
            Pump();
        }
        public void Select(string id, WifiRadioSnapshot radio)
        {
            Source.Set(radio);
            Clock.Advance();
            Control<ComboBox>(Window, "AdapterComboBox").SelectedItem = Monitoring.GetSnapshot().Adapters.Single(a => a.Id == id);
            Window.RefreshDashboard();
            WaitFor(radio);
        }
        public void RefreshAdapterDetails()
        {
            var previous = Field<long>(Monitoring, "_lastHandledObservationSequence");
            Monitoring.RefreshRecognition();
            Wait(() => { _ = Monitoring.GetSnapshot(); return Field<long>(Monitoring, "_lastHandledObservationSequence") > previous; });
            Window.RefreshDashboard();
            Window.UpdateLayout();
            Pump();
        }
        public void Dispose() { Window.Close(); Monitoring.Dispose(); }
    }
    private sealed class ControlledClock : TimeProvider
    {
        private long _ticks = new DateTimeOffset(2026, 10, 11, 8, 0, 0, TimeSpan.Zero).Ticks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        public void Advance() => Interlocked.Add(ref _ticks, TimeSpan.TicksPerSecond * 6);
    }
    private sealed class OwnedCounters : INetworkMonitor
    {
        public OperationalStatus WifiStatus { get; set; } = OperationalStatus.Up;
        public bool LongIpv6 { get; set; }
        public IReadOnlyList<AdapterInfo> GetActiveAdapters() =>
        [
            new(WifiId, "Owned Wi-Fi", "Owned first wireless adapter", "Wireless80211", 86_000_000, "192.0.2.10", true),
            new(SecondWifiId, "Owned second Wi-Fi", "Owned second wireless adapter", "Wireless80211", 100_000_000, "192.0.2.20", false),
            new(EthernetId, "Owned Ethernet", "Owned physical ethernet adapter", "Ethernet", 1_000_000_000, "198.51.100.20", false)
        ];
        public NetworkSnapshot? Capture(string adapterId) => new(1000, 500, DateTimeOffset.UtcNow);
        public NetworkAdapterDetails? GetAdapterDetails(string id) => new(id,
            id == WifiId ? WifiStatus : OperationalStatus.Up,
            [IPAddress.Parse(id == EthernetId ? "198.51.100.20" : "192.0.2.10"),
                IPAddress.Parse(LongIpv6 ? "2001:db8:abcd:1234:5678:90ab:cdef:1234" : "2001:db8:1::10")],
            [IPAddress.Parse("192.0.2.1")], [IPAddress.Parse("1.1.1.1")], id == EthernetId ? 1_000_000_000 : 86_000_000);
    }
    private sealed class OwnedIdentity : INetworkIdentityService
    {
        public event EventHandler? NetworkChanged { add { } remove { } }
        public NetworkIdentityResult GetCurrentNetwork() => new(NetworkIdentityAvailability.Available,
            new NetworkIdentity("WiFi", "OWNED_WIFI_RADIO", "OPPO A3x", WifiId), null);
        public void Dispose() { }
    }
    private sealed class NoTunnel : IVpnInterfaceSource { public VpnInterfaceInventory Read() => new([], []); }
    private sealed class NoRoutes : IVpnRouteSource
    {
        public VpnRouteInventory Read(AddressFamily family) => new(family, [], Availability: IpFamilyAvailability.Unavailable);
    }
    private sealed class OwnedPing : IPingSource
    {
        public Task<IcmpPingReply> SendAsync(IPAddress target, TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(new IcmpPingReply(IPStatus.TimedOut));
    }
    private sealed class ReadOnlyStartup : IWindowsStartupRegistration
    {
        public bool IsEnabled() => false;
        public void Enable() => throw new InvalidOperationException("Wi-Fi checks do not modify startup registration.");
        public void Disable() => throw new InvalidOperationException("Wi-Fi checks do not modify startup registration.");
    }
}
