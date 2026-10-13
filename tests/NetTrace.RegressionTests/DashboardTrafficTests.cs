using System.Globalization;
using System.Diagnostics;
using System.Reflection;
using NetTrace.Models;
using NetTrace.Presentation;
using NetTrace.Services;
using NetTrace.Utils;

internal static class DashboardTrafficTests
{
    private static readonly DateTimeOffset End = new(2026, 10, 13, 5, 30, 0, TimeSpan.Zero);

    public static (string Name, Action Run)[] Cases =>
    [
        ("Empty Dashboard traffic uses a readable positive byte-rate scale", EmptyScale),
        ("The live traffic chart retains exactly the last sixty seconds including endpoints", WindowBounds),
        ("Unordered traffic samples become chronological without mutating their input", ChronologicalSamples),
        ("Download and upload plot their independent actual bytes-per-second values", IndependentDirections),
        ("The live graph scale follows the highest individual direction rather than their sum", IndividualScale),
        ("Traffic scale promotes through readable one two and five byte-rate steps", NiceScale),
        ("Extreme finite traffic retains a finite covering graph scale", ExtremeScale),
        ("Traffic scale labels use the same adaptive formatter as the live cards", AxisFormatterParity),
        ("Zero-speed periods remain known plot points at the baseline", ZeroPeriods),
        ("Unavailable traffic breaks both series without drawing a false zero", UnavailableGaps),
        ("An unavailable download does not discard an independently known upload", IndependentGap),
        ("Explicit measurement boundary markers break each known traffic series", ExplicitBreak),
        ("Unobserved intervals longer than the refresh cadence break graph lines", MissingTimeGap),
        ("Ordinary consecutive live samples stay connected", ConsecutiveSamples),
        ("Hover values expose local time and matching download upload total rates", TooltipData),
        ("Zero-speed hover values remain explicit known zero byte rates", ZeroTooltip),
        ("Unavailable hover values cannot claim measured zero traffic", GapTooltip),
        ("French hover and axis quantities match the live card display culture", DisplayCulture),
        ("Chart snapshots stay immutable when caller input changes", ImmutableSnapshots),
        ("The live timeline floors sampling time to one UTC second", SecondBuckets),
        ("Duplicate same-second traffic refreshes reuse the existing visual snapshot", RedundantRefresh),
        ("A changed same-second measurement replaces its point without accumulating speeds", SameSecondReplacement),
        ("Advancing one second moves the graph even while both directions are idle", AdvancingIdle),
        ("The rolling timeline remains bounded to sixty-one second positions", BoundedTimeline),
        ("Backdated traffic cannot replace current graph measurements", BackdatedSample),
        ("Timeline measurements normalize invalid rates exactly like the live cards", NormalizedRates),
        ("Available and unavailable states can replace the latest same-second point", AvailabilityReplacement),
        ("Collecting without graph projection preserves a continuous recent timeline", ContinuousCollection),
        ("Genuine missing measurement intervals remain gaps without fabricated samples", LongMeasurementGap),
        ("Changing monitored adapters clears the old adapter's traffic timeline", AdapterReset),
        ("Repeated observations of the same adapter preserve earlier live points", SameAdapter),
        ("Returned old traffic snapshots cannot change as the timeline advances", ImmutableTimeline),
        ("A rolling old peak expires and allows the graph scale to decrease", ExpiringPeak),
        ("Raw timeline snapshots are cached and remain immutable as background samples advance", ImmutableRawTimeline),
        ("Raw background collection builds no chart geometry until presentation reads it", LazyProjection),
        ("Headless monitoring feeds the timeline from the accepted raw rates without another capture", ServiceRatesAndAccounting),
        ("An unseen headless timeline continuously retains the bounded last sixty seconds", ServiceRecentHistory),
        ("Reading monitoring snapshots reuses raw history without sampling or adding a timer", SnapshotReadsArePassive),
        ("A genuine failed network capture creates a gap while subsequent measurements reconnect", ServiceUnavailableCapture),
        ("Operating system sleep remains a genuine unobserved interval in the traffic timeline", ServiceSleepGap),
        ("Stopped monitoring cannot capture or append further traffic samples", ServiceStopped),
        ("A new application monitoring owner starts fresh traffic without replaying persisted usage", ServiceRecreation),
    ];

    private static DashboardTrafficSample Sample(int seconds, double? download, double? upload,
        bool breakBefore = false) => new(End.AddSeconds(seconds), download, upload, breakBefore);

    private static DashboardTrafficChartData Chart(params DashboardTrafficSample[] samples) =>
        DashboardTrafficChartData.Create(samples, End);

    private static DashboardTrafficChartData Observe(DashboardTrafficTimeline timeline,
        DateTimeOffset timestampUtc, double download, double upload, bool available, string? adapterId)
    {
        timeline.Record(timestampUtc, download, upload, available, adapterId);
        return timeline.Data;
    }

    private static void EmptyScale() => English(() =>
    {
        var value = Chart();
        Equal(0, value.Samples.Count, "no manufactured live measurements");
        Equal(0, value.DownloadSegments.Count, "empty download geometry");
        Equal(0, value.UploadSegments.Count, "empty upload geometry");
        Check(double.IsFinite(value.MaximumBytesPerSecond) && value.MaximumBytesPerSecond > 0,
            "an empty chart still needs a finite positive Y axis");
        Equal(ByteRateFormatter.Format(value.MaximumBytesPerSecond), value.MaximumText, "empty scale quantity");
        Equal(60, DashboardTrafficChartData.WindowSeconds, "rolling window length");
    });

    private static void WindowBounds()
    {
        var value = Chart(Sample(-61, 99, 1), Sample(-60, 10, 2), Sample(-1, 20, 3),
            Sample(0, 30, 4), Sample(1, 999, 5));
        Equal(3, value.Samples.Count, "only samples inside the inclusive visible window");
        Equal(End.AddSeconds(-60), value.WindowStartUtc, "visible window start");
        Equal(End, value.WindowEndUtc, "visible window end");
        Equal(10d, value.Samples[0].DownloadBytesPerSecond!.Value, "oldest endpoint is included");
        Equal(30d, value.Samples[^1].DownloadBytesPerSecond!.Value, "current endpoint is included");
        var points = value.DownloadSegments.SelectMany(segment => segment).ToArray();
        Near(0, points[0].Position, "oldest endpoint horizontal position");
        Near(1, points[^1].Position, "current endpoint horizontal position");
    }

    private static void ChronologicalSamples()
    {
        var source = new[] { Sample(0, 300, 3), Sample(-2, 100, 1), Sample(-1, 200, 2) };
        var original = source.ToArray();
        var value = DashboardTrafficChartData.Create(source, End);
        Check(value.Samples.Select(sample => sample.TimestampUtc).SequenceEqual(
            new[] { End.AddSeconds(-2), End.AddSeconds(-1), End }), "chart points are chronological");
        Check(source.SequenceEqual(original), "sorting does not change caller input");
    }

    private static void IndependentDirections()
    {
        var value = Chart(Sample(-1, 842, 1250), Sample(0, 4_920_000, 28_800));
        Check(value.DownloadSegments.Single().Select(point => point.BytesPerSecond)
            .SequenceEqual(new[] { 842d, 4_920_000d }), "download geometry preserves the raw measured direction");
        Check(value.UploadSegments.Single().Select(point => point.BytesPerSecond)
            .SequenceEqual(new[] { 1250d, 28_800d }), "upload geometry preserves the raw measured direction");
    }

    private static void IndividualScale() => Equal(2000d,
        Chart(Sample(0, 1250, 1250)).MaximumBytesPerSecond, "1250 B/s in each direction needs a 2000 B/s axis");

    private static void NiceScale()
    {
        foreach (var (measurement, expected) in new[]
        {
            (1d, 1d), (1.01d, 2d), (2.01d, 5d), (5.01d, 10d),
            (1000d, 1000d), (1250d, 2000d), (4999d, 5000d),
            (95000d, 100000d), (1_001_000d, 2_000_000d), (5_000_000d, 5_000_000d)
        }) Equal(expected, Chart(Sample(0, measurement, 0)).MaximumBytesPerSecond, "nice scale for " + measurement);
    }

    private static void ExtremeScale()
    {
        foreach (var measurement in new[] { double.MaxValue / 2, double.MaxValue })
        {
            var value = Chart(Sample(0, measurement, measurement));
            Check(double.IsFinite(value.MaximumBytesPerSecond) && value.MaximumBytesPerSecond >= measurement,
                "a finite measurement cannot overflow the axis or lie above it");
            Check(!value.MaximumText.Contains("Infinity", StringComparison.Ordinal), "axis text remains finite");
        }
    }

    private static void AxisFormatterParity() => English(() =>
    {
        foreach (var measurement in new[] { 842d, 1250d, 28_800d, 4_920_000d, 1_100_000_000d })
        {
            var value = Chart(Sample(0, measurement, 0));
            Equal(ByteRateFormatter.Format(value.MaximumBytesPerSecond), value.MaximumText, "top byte-rate label");
            Equal(ByteRateFormatter.Format(value.MaximumBytesPerSecond / 2), value.MiddleText, "middle byte-rate label");
            Check(value.MaximumText.EndsWith("/s", StringComparison.Ordinal), "speed axis is a rate quantity");
        }
    });

    private static void ZeroPeriods()
    {
        var value = Chart(Sample(-2, 0, 0), Sample(-1, 100, 40), Sample(0, 0, 0));
        Equal(3, value.DownloadSegments.Single().Count, "zero download periods remain connected measured samples");
        Equal(3, value.UploadSegments.Single().Count, "zero upload periods remain connected measured samples");
        Equal(0d, value.DownloadSegments.Single()[0].BytesPerSecond, "download baseline is a known zero");
        Equal(0d, value.UploadSegments.Single()[^1].BytesPerSecond, "upload baseline is a known zero");
    }

    private static void UnavailableGaps()
    {
        var value = Chart(Sample(-2, 100, 20), Sample(-1, null, null), Sample(0, 200, 40));
        Equal(3, value.Samples.Count, "the unavailable time remains available for hover");
        Equal(2, value.DownloadSegments.Count, "download line breaks around missing data");
        Equal(2, value.UploadSegments.Count, "upload line breaks around missing data");
        Equal(2, value.DownloadSegments.Sum(segment => segment.Count), "download gap creates no false zero vertex");
        Equal(2, value.UploadSegments.Sum(segment => segment.Count), "upload gap creates no false zero vertex");
    }

    private static void IndependentGap()
    {
        var value = Chart(Sample(-2, 100, 20), Sample(-1, null, 30), Sample(0, 200, 40));
        Equal(2, value.DownloadSegments.Count, "missing download splits its own series");
        Equal(3, value.UploadSegments.Single().Count, "known upload remains independently visible");
    }

    private static void ExplicitBreak()
    {
        var value = Chart(Sample(-1, 100, 20), Sample(0, 200, 40, true));
        Equal(2, value.DownloadSegments.Count, "pause splits download into separate observations");
        Equal(2, value.UploadSegments.Count, "pause splits upload into separate observations");
    }

    private static void MissingTimeGap()
    {
        var value = Chart(Sample(-5, 100, 20), Sample(-4, 120, 25), Sample(0, 200, 40));
        Equal(2, value.DownloadSegments.Count, "a four-second missing interval is not joined");
        Equal(2, value.UploadSegments.Count, "both directions break across the missing interval");
        Equal(2, value.DownloadSegments[0].Count, "normal samples before the gap stay joined");
    }

    private static void ConsecutiveSamples()
    {
        var value = Chart(Sample(-2, 100, 20), Sample(-1, 120, 25), Sample(0, 200, 40));
        Equal(1, value.DownloadSegments.Count, "one connected download segment");
        Equal(1, value.UploadSegments.Count, "one connected upload segment");
    }

    private static void TooltipData() => English(() =>
    {
        var sample = Sample(0, 4_920_000, 28_800);
        var text = sample.ToolTipText;
        Check(text.Contains(sample.TimestampUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture),
            StringComparison.Ordinal), "tooltip exposes the local observation time");
        TooltipRate(text, "Download", LiveSpeedPresentation.Create(4_920_000).ByteRateText);
        TooltipRate(text, "Upload", LiveSpeedPresentation.Create(28_800).ByteRateText);
        TooltipRate(text, "Total", ByteRateFormatter.Format(4_948_800));
    });

    private static void ZeroTooltip() => English(() =>
    {
        var text = Sample(0, 0, 0).ToolTipText;
        foreach (var direction in new[] { "Download", "Upload", "Total" }) TooltipRate(text, direction, "0 B/s");
    });

    private static void GapTooltip()
    {
        var text = Sample(0, null, null).ToolTipText;
        Check(!text.Contains("0 B/s", StringComparison.Ordinal), "unknown directions cannot be shown as measured zero");
        Check(text.Contains("Download", StringComparison.Ordinal) && text.Contains("Upload", StringComparison.Ordinal),
            "unavailable tooltip retains direction context");
    }

    private static void DisplayCulture() => WithCulture("fr-FR", () =>
    {
        var sample = Sample(0, 1250, 4_920_000);
        TooltipRate(sample.ToolTipText, "Download", LiveSpeedPresentation.Create(1250).ByteRateText);
        TooltipRate(sample.ToolTipText, "Upload", LiveSpeedPresentation.Create(4_920_000).ByteRateText);
        Check(sample.ToolTipText.Contains("1,25 KB/s", StringComparison.Ordinal), "French decimal separator appears in hover quantities");
        var value = Chart(sample);
        Equal(ByteRateFormatter.Format(value.MaximumBytesPerSecond), value.MaximumText, "French top axis quantity");
        Equal(ByteRateFormatter.Format(value.MaximumBytesPerSecond / 2), value.MiddleText, "French middle axis quantity");
    });

    private static void ImmutableSnapshots()
    {
        var source = new List<DashboardTrafficSample> { Sample(-1, 100, 20), Sample(0, 200, 40) };
        var value = DashboardTrafficChartData.Create(source, End);
        source[0] = Sample(-1, 9999, 9999);
        source.Clear();
        Equal(2, value.Samples.Count, "clearing the input cannot change the chart snapshot");
        Equal(100d, value.Samples[0].DownloadBytesPerSecond!.Value, "input replacement cannot change old measurements");
        Equal(100d, value.DownloadSegments.Single()[0].BytesPerSecond, "geometry remains consistent with immutable input");
        if (value.Samples is IList<DashboardTrafficSample> list)
            Check(list.IsReadOnly, "exposed sample collection cannot be edited");
    }

    private static void SecondBuckets()
    {
        var value = Observe(new DashboardTrafficTimeline(), End.AddMilliseconds(987), 100, 20, true, "adapter-a");
        Equal(End, value.WindowEndUtc, "window end is the observation's UTC second");
        Equal(End, value.Samples.Single().TimestampUtc, "sample is placed in one stable second position");
    }

    private static void RedundantRefresh()
    {
        var timeline = new DashboardTrafficTimeline();
        var first = Observe(timeline, End.AddMilliseconds(100), 100, 20, true, "adapter-a");
        var repeated = Observe(timeline, End.AddMilliseconds(900), 100, 20, true, "adapter-a");
        Check(ReferenceEquals(first, repeated), "same-second unchanged reads avoid new geometry and rendering");
        Equal(1, repeated.Samples.Count, "repeated refresh does not add an extra sample");
    }

    private static void SameSecondReplacement()
    {
        var timeline = new DashboardTrafficTimeline();
        var first = Observe(timeline, End.AddMilliseconds(100), 100, 20, true, "adapter-a");
        var changed = Observe(timeline, End.AddMilliseconds(900), 200, 40, true, "adapter-a");
        Equal(1, changed.Samples.Count, "one point remains in the second bucket");
        Equal(200d, changed.Samples.Single().DownloadBytesPerSecond!.Value, "latest measured download replaces rather than adds");
        Equal(40d, changed.Samples.Single().UploadBytesPerSecond!.Value, "latest measured upload replaces rather than adds");
        Equal(100d, first.Samples.Single().DownloadBytesPerSecond!.Value, "older visual snapshot stays intact");
    }

    private static void AdvancingIdle()
    {
        var timeline = new DashboardTrafficTimeline();
        var first = Observe(timeline, End, 0, 0, true, "adapter-a");
        var next = Observe(timeline, End.AddSeconds(1), 0, 0, true, "adapter-a");
        Check(!ReferenceEquals(first, next), "the visible time window advances during known idle periods");
        Equal(2, next.Samples.Count, "a second known idle observation remains visible");
        Equal(End.AddSeconds(1), next.WindowEndUtc, "moving idle window end");
    }

    private static void BoundedTimeline()
    {
        var timeline = new DashboardTrafficTimeline();
        for (var index = 0; index < 180; index++)
            timeline.Record(End.AddSeconds(index), index, index / 2d, true, "adapter-a");
        var value = timeline.Data;
        Equal(61, value.Samples.Count, "sixty seconds have sixty-one inclusive point positions");
        Equal(End.AddSeconds(119), value.Samples[0].TimestampUtc, "older observations expire");
        Equal(End.AddSeconds(179), value.Samples[^1].TimestampUtc, "latest observation is retained");
    }

    private static void BackdatedSample()
    {
        var timeline = new DashboardTrafficTimeline();
        var current = Observe(timeline, End, 100, 20, true, "adapter-a");
        var stale = Observe(timeline, End.AddSeconds(-1), 9999, 9999, true, "adapter-a");
        Check(ReferenceEquals(current, stale), "late older samples leave the current visual snapshot alone");
        Equal(100d, stale.Samples.Single().DownloadBytesPerSecond!.Value, "late old speed cannot appear as current");
    }

    private static void NormalizedRates() => English(() =>
    {
        var timeline = new DashboardTrafficTimeline();
        var index = 0;
        foreach (var raw in new[] { -1d, double.NaN, double.PositiveInfinity, double.NegativeInfinity, -0d, 0d, 0.001d, 1250d })
        {
            var value = Observe(timeline, End.AddSeconds(index++), raw, raw, true, "adapter-a");
            var sample = value.Samples[^1];
            var expected = double.IsFinite(raw) && raw > 0 ? raw : 0;
            Equal(expected, sample.DownloadBytesPerSecond!.Value, "download uses the same card normalization");
            Equal(expected, sample.UploadBytesPerSecond!.Value, "upload uses the same card normalization");
            TooltipRate(sample.ToolTipText, "Download", LiveSpeedPresentation.Create(raw).ByteRateText);
        }
    });

    private static void AvailabilityReplacement()
    {
        var timeline = new DashboardTrafficTimeline();
        timeline.Record(End, 100, 20, true, "adapter-a");
        var missing = Observe(timeline, End.AddMilliseconds(250), 100, 20, false, "adapter-a");
        Check(missing.Samples.Single().DownloadBytesPerSecond is null && missing.Samples.Single().UploadBytesPerSecond is null,
            "an unavailable observation does not retain stale speed");
        Equal(0, missing.DownloadSegments.Count, "missing speed has no plotted geometry");
        var known = Observe(timeline, End.AddMilliseconds(500), 200, 40, true, "adapter-a");
        Equal(1, known.Samples.Count, "availability changes retain one second position");
        Equal(200d, known.Samples.Single().DownloadBytesPerSecond!.Value, "fresh available speed replaces the gap");
    }

    private static void ContinuousCollection()
    {
        var timeline = new DashboardTrafficTimeline();
        for (var index = 0; index <= 20; index++)
            timeline.Record(End.AddSeconds(index), 100 + index, 20 + index, true, "adapter-a");
        var resumed = timeline.Data;
        Equal(21, resumed.Samples.Count, "all observations remain available without a visible graph");
        Check(resumed.Samples.All(sample => !sample.BreakBefore), "background collection adds no artificial visibility boundary");
        Equal(21, resumed.DownloadSegments.Single().Count, "download history remains joined on returning");
        Equal(21, resumed.UploadSegments.Single().Count, "upload history remains joined on returning");
        Equal(120d, resumed.Samples[^1].DownloadBytesPerSecond!.Value, "returning presents the recent download rate");
    }

    private static void LongMeasurementGap()
    {
        var timeline = new DashboardTrafficTimeline();
        timeline.Record(End, 100, 20, true, "adapter-a");
        var resumed = Observe(timeline, End.AddSeconds(20), 200, 40, true, "adapter-a");
        Equal(2, resumed.Samples.Count, "twenty unobserved seconds create no fabricated observations");
        Equal(2, resumed.DownloadSegments.Count, "a genuinely unobserved interval remains a download gap");
        Equal(2, resumed.UploadSegments.Count, "a genuinely unobserved interval remains an upload gap");
    }

    private static void AdapterReset()
    {
        var timeline = new DashboardTrafficTimeline();
        var previous = Observe(timeline, End, 100, 20, true, "adapter-a");
        var current = Observe(timeline, End.AddSeconds(1), 200, 40, true, "adapter-b");
        Equal(1, current.Samples.Count, "changed adapter starts a fresh timeline");
        Equal(200d, current.Samples.Single().DownloadBytesPerSecond!.Value, "only new adapter speed remains");
        Equal(100d, previous.Samples.Single().DownloadBytesPerSecond!.Value, "old immutable adapter snapshot remains intact");
    }

    private static void SameAdapter()
    {
        var timeline = new DashboardTrafficTimeline();
        timeline.Record(End, 100, 20, true, "adapter-a");
        var current = Observe(timeline, End.AddSeconds(1), 200, 40, true, "adapter-a");
        Equal(2, current.Samples.Count, "normal observations retain the same adapter's rolling history");
        Equal(1, current.DownloadSegments.Count, "normal same-adapter samples stay connected");
    }

    private static void ImmutableTimeline()
    {
        var timeline = new DashboardTrafficTimeline();
        var first = Observe(timeline, End, 100, 20, true, "adapter-a");
        for (var index = 1; index <= 70; index++) timeline.Record(End.AddSeconds(index), index, 0, true, "adapter-a");
        Equal(1, first.Samples.Count, "old view does not gain later timeline points");
        Equal(End, first.WindowEndUtc, "old view keeps its original time window");
        Equal(100d, first.DownloadSegments.Single().Single().BytesPerSecond, "old view keeps its original geometry");
    }

    private static void ExpiringPeak()
    {
        var timeline = new DashboardTrafficTimeline();
        var peak = Observe(timeline, End, 4_920_000, 0, true, "adapter-a");
        for (var index = 1; index <= 61; index++) timeline.Record(End.AddSeconds(index), 100, 0, true, "adapter-a");
        Check(timeline.Data.MaximumBytesPerSecond < peak.MaximumBytesPerSecond, "expired historical peak no longer controls current scale");
        Equal(100d, timeline.Data.MaximumBytesPerSecond, "small stable speed uses its own current scale");
    }

    private static void ImmutableRawTimeline()
    {
        var timeline = new DashboardTrafficTimeline();
        timeline.Record(End, 100, 20, true, "adapter-a");
        var first = timeline.Samples;
        Check(ReferenceEquals(first, timeline.Samples), "unchanged raw reads reuse the cached snapshot");
        if (first is IList<DashboardTrafficSample> list)
            Check(list.IsReadOnly, "the raw snapshot cannot mutate the collector");
        timeline.Record(End.AddSeconds(1), 200, 40, true, "adapter-a");
        var next = timeline.Samples;
        Equal(1, first.Count, "old raw snapshot retains its original sample count");
        Equal(100d, first.Single().DownloadBytesPerSecond!.Value, "old raw snapshot retains its measured value");
        Equal(2, next.Count, "fresh raw snapshot receives the next measurement");
        Check(ReferenceEquals(next, timeline.Samples), "fresh raw snapshot is reused until a sample changes");
        timeline.Record(End.AddSeconds(1).AddMilliseconds(500), 200, 40, true, "adapter-a");
        Check(ReferenceEquals(next, timeline.Samples), "same-second redundant recording preserves raw cache identity");
    }

    private static void LazyProjection()
    {
        var timeline = new DashboardTrafficTimeline();
        var cache = typeof(DashboardTrafficTimeline).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(field => field.FieldType == typeof(DashboardTrafficChartData));
        var initial = cache.GetValue(timeline);
        for (var index = 0; index <= 60; index++)
        {
            timeline.Record(End.AddSeconds(index), 100 + index, 20 + index, true, "adapter-a");
            _ = timeline.Samples;
            Check(ReferenceEquals(initial, cache.GetValue(timeline)),
                "recording and reading raw samples must not build new chart segments");
        }
        var visible = timeline.Data;
        Equal(61, visible.DownloadSegments.Single().Count, "the visible read builds all recent geometry at once");
        Check(ReferenceEquals(visible, timeline.Data), "unchanged visible reads reuse the chart projection");
        timeline.Record(End.AddSeconds(61), 161, 81, true, "adapter-a");
        var afterRecord = cache.GetValue(timeline);
        Check(afterRecord is null || ReferenceEquals(visible, afterRecord),
            "background recording can invalidate a projection but must not regenerate it");
        Equal(61, timeline.Samples.Count, "raw storage stays bounded without projection");
        Check(!ReferenceEquals(visible, timeline.Data), "the next visible read presents the updated recent window");
    }

    private static void ServiceRatesAndAccounting()
    {
        using var test = ControlledMonitoring();
        var captures = test.Network.Captures;
        test.AdvanceCounters(2048, 128);
        var snapshot = test.Service.GetSnapshot();
        var sample = snapshot.TrafficSamples[^1];
        Equal(captures + 1, test.Network.Captures, "one accepted sample performs only the existing counter capture");
        Equal(test.Clock.GetUtcNow(), sample.TimestampUtc, "graph time uses the accepted counter measurement");
        Equal(snapshot.DownloadBytesPerSecond, sample.DownloadBytesPerSecond!.Value, "download shares the card's raw rate");
        Equal(snapshot.UploadBytesPerSecond, sample.UploadBytesPerSecond!.Value, "upload shares the card's raw rate");
        Equal(2048d, sample.DownloadBytesPerSecond!.Value, "accepted download rate");
        Equal(128d, sample.UploadBytesPerSecond!.Value, "accepted upload rate");
        Equal(2048L, snapshot.SessionDownloaded, "download accounting receives the delta once");
        Equal(128L, snapshot.SessionUploaded, "upload accounting receives the delta once");
        Equal(2048L, snapshot.PendingDownloaded, "download persistence retains the existing batch");
        Equal(128L, snapshot.PendingUploaded, "upload persistence retains the existing batch");
        Equal(2048L, snapshot.Session!.DownloadBytes, "physical session download receives the same delta");
        Equal(128L, snapshot.Session!.UploadBytes, "physical session upload receives the same delta");
        Equal((0L, 0L), test.Usage.GetTotals(test.Day, test.Day), "collecting graph history does not force persistence");
        var totals = test.Service.GetUsageTotals();
        foreach (var value in new[] { totals.Today, totals.Week, totals.Month })
            Equal((2048L, 128L), value, "the existing summary ranges include pending bytes exactly once");
        test.Service.Flush();
        Equal((2048L, 128L), test.Usage.GetTotals(test.Day, test.Day), "the existing flush persists only accepted bytes");
        Equal((2048L, 128L), test.Service.GetUsageTotals().Today, "flush leaves the displayed summary quantities unchanged");
    }

    private static void ServiceRecentHistory()
    {
        using var test = ControlledMonitoring();
        var start = test.Clock.GetUtcNow();
        var first = test.Service.GetSnapshot().TrafficSamples;
        for (var index = 1; index <= 95; index++) test.AdvanceCounters(1000, 40);
        // No Dashboard/window or graph projection was constructed during collection.
        var snapshot = test.Service.GetSnapshot();
        Equal(61, snapshot.TrafficSamples.Count, "continuous background collection retains sixty-one endpoint positions");
        Equal(start.AddSeconds(35), snapshot.TrafficSamples[0].TimestampUtc, "old background observations expire");
        Equal(start.AddSeconds(95), snapshot.TrafficSamples[^1].TimestampUtc, "the latest background observation is retained");
        for (var index = 1; index < snapshot.TrafficSamples.Count; index++)
            Equal(TimeSpan.FromSeconds(1), snapshot.TrafficSamples[index].TimestampUtc - snapshot.TrafficSamples[index - 1].TimestampUtc,
                "background measurements retain every recent second");
        Check(snapshot.TrafficSamples.All(sample => !sample.BreakBefore), "lack of a Dashboard does not create visibility markers");
        var chart = DashboardTrafficChartData.Create(snapshot.TrafficSamples, test.Clock.GetUtcNow());
        Equal(61, chart.DownloadSegments.Single().Count, "returning immediately has complete recent download geometry");
        Equal(61, chart.UploadSegments.Single().Count, "returning immediately has complete recent upload geometry");
        Equal(1, first.Count, "an older monitoring snapshot remains immutable as the service advances");
        Equal((95_000L, 3800L), test.Service.GetUsageTotals().Today, "background timeline storage does not alter cumulative usage");
    }

    private static void SnapshotReadsArePassive()
    {
        using var test = ControlledMonitoring();
        test.AdvanceCounters(200, 30);
        var snapshot = test.Service.GetSnapshot();
        var captures = test.Network.Captures;
        for (var index = 0; index < 30; index++)
            Check(ReferenceEquals(snapshot.TrafficSamples, test.Service.GetSnapshot().TrafficSamples),
                "unchanged monitoring reads reuse immutable raw history");
        Equal(captures, test.Network.Captures, "graph/snapshot consumers do not query the network again");
        Equal(1, typeof(MonitoringService).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Count(field => field.FieldType == typeof(Timer)), "monitoring retains its one existing sampling timer");
        Check(typeof(DashboardTrafficTimeline).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .All(field => !typeof(Timer).IsAssignableFrom(field.FieldType)), "timeline storage owns no timer");
        if (snapshot.TrafficSamples is IList<DashboardTrafficSample> samples)
            Check(samples.IsReadOnly, "monitoring raw traffic cannot be edited by a caller");
    }

    private static void ServiceUnavailableCapture()
    {
        using var test = ControlledMonitoring();
        test.AdvanceCounters(100, 20);
        test.Clock.Advance(TimeSpan.FromSeconds(1));
        var missingTime = test.Clock.GetUtcNow();
        test.Network.SetSnapshot(test.Adapter.Id, null);
        test.Service.SampleNow();
        var missing = test.Service.GetSnapshot().TrafficSamples.Single(sample => sample.TimestampUtc == missingTime);
        Check(missing.DownloadBytesPerSecond is null && missing.UploadBytesPerSecond is null,
            "a genuinely unavailable capture is not a measured idle zero");
        test.AdvanceCounters(200, 40); // Existing recovery baseline does not count unknown interval bytes.
        test.AdvanceCounters(300, 60);
        var snapshot = test.Service.GetSnapshot();
        var chart = DashboardTrafficChartData.Create(snapshot.TrafficSamples, test.Clock.GetUtcNow());
        Equal(2, chart.DownloadSegments.Count, "download remains separated across the failed capture");
        Equal(2, chart.UploadSegments.Count, "upload remains separated across the failed capture");
        Equal(300d, snapshot.TrafficSamples[^1].DownloadBytesPerSecond!.Value, "subsequent accepted download is visible");
        Equal(60d, snapshot.TrafficSamples[^1].UploadBytesPerSecond!.Value, "subsequent accepted upload is visible");
        Equal((400L, 80L), test.Service.GetUsageTotals().Today, "graph recovery preserves the existing counter-reset accounting");
    }

    private static void ServiceSleepGap()
    {
        using var test = ControlledMonitoring();
        test.AdvanceCounters(100, 20);
        test.Service.Suspend();
        var captures = test.Network.Captures;
        var sleeping = test.Service.GetSnapshot().TrafficSamples;
        test.Clock.Advance(TimeSpan.FromSeconds(10));
        test.Service.SampleNow();
        Equal(captures, test.Network.Captures, "real operating system suspension still stops monitoring");
        Check(ReferenceEquals(sleeping, test.Service.GetSnapshot().TrafficSamples),
            "suspended monitoring does not fabricate traffic samples");
        test.Service.Resume();
        StopSampler(test.Service);
        WaitForSession(test.Service);
        test.AdvanceCounters(200, 40);
        test.AdvanceCounters(300, 60);
        var snapshot = test.Service.GetSnapshot();
        var chart = DashboardTrafficChartData.Create(snapshot.TrafficSamples, test.Clock.GetUtcNow());
        Equal(2, chart.DownloadSegments.Count, "genuine sleep remains a download time gap");
        Equal(2, chart.UploadSegments.Count, "genuine sleep remains an upload time gap");
        Check(!snapshot.IsSuspended, "the resumed source is active again");
    }

    private static void ServiceStopped()
    {
        using var test = ControlledMonitoring();
        test.AdvanceCounters(100, 20);
        test.Service.Stop();
        var stopped = test.Service.GetSnapshot();
        var captures = test.Network.Captures;
        test.AdvanceCounters(200, 40);
        var after = test.Service.GetSnapshot();
        Equal(captures, test.Network.Captures, "stopped monitoring ignores manual sampling requests");
        Check(ReferenceEquals(stopped.TrafficSamples, after.TrafficSamples), "stopped monitoring appends no timeline points");
        Check(!after.IsRunning, "the sampler lifetime still follows application monitoring");
    }

    private static void ServiceRecreation()
    {
        using var test = ControlledMonitoring();
        test.AdvanceCounters(2000, 200);
        var old = test.Service.GetSnapshot().TrafficSamples;
        test.Service.Stop();
        var identity = new FakeNetworkIdentityService
        {
            Network = new NetworkIdentity("WiFi", "Headless hotspot", "Headless hotspot", test.Adapter.Id)
        };
        using var restarted = MonitoringService.CreateForDataRoot(test.Root, identity,
            new VpnRouteVerifier(new VpnDetectionService(new FakeVpnInterfaceSource()), new FakeVpnRouteSource()),
            test.Network, test.Clock, new IcmpMonitor(new NoInternetPingSource(), timeProvider: test.Clock),
            new InternetReachabilityMonitor(new NoInternetReachabilitySource(), timeProvider: test.Clock));
        Equal(0, restarted.GetSnapshot().TrafficSamples.Count, "in-memory traffic begins empty for a new application owner");
        restarted.Start();
        StopSampler(restarted);
        WaitForSession(restarted);
        restarted.SampleNow();
        Equal(1, restarted.GetSnapshot().TrafficSamples.Count, "only the new owner's accepted baseline is collected");
        Equal((2000L, 200L), restarted.GetUsageTotals().Today, "persisted usage is preserved without becoming live graph samples");
        test.AdvanceCounters(100, 10, sampleNow: false);
        restarted.SampleNow();
        Equal(2, restarted.GetSnapshot().TrafficSamples.Count, "the new owner collects its own new observations");
        Equal(2, old.Count, "the old owner's returned raw history remains immutable");
        Equal(2000d, old[^1].DownloadBytesPerSecond!.Value, "the old owner's accepted rates remain unchanged");
        Equal((2100L, 210L), restarted.GetUsageTotals().Today, "restarting the graph does not replay persisted accounting");
    }

    private static HeadlessMonitoringContext ControlledMonitoring()
    {
        var test = new HeadlessMonitoringContext();
        StopSampler(test.Service);
        return test;
    }

    private static void StopSampler(MonitoringService service)
    {
        var timer = (Timer)typeof(MonitoringService).GetField("_timer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(service)!;
        timer.Change(Timeout.Infinite, Timeout.Infinite);
    }

    private static void WaitForSession(MonitoringService service)
    {
        var deadline = Stopwatch.StartNew();
        while (service.GetSnapshot().Session is null)
        {
            if (deadline.Elapsed >= TimeSpan.FromSeconds(5))
                throw new TimeoutException("Headless traffic fixture did not establish its physical connection.");
            Thread.Sleep(20);
        }
    }

    private static void TooltipRate(string text, string direction, string expected) =>
        Check(text.Contains(direction + ": " + expected, StringComparison.Ordinal),
            direction + " tooltip must show " + expected + ". Got: " + text);

    private static void English(Action action) => WithCulture("en-US", action);

    private static void WithCulture(string culture, Action action)
    {
        var previous = CultureInfo.CurrentCulture;
        try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture); action(); }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    private static void Equal<T>(T expected, T actual, string description)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{description}: expected {expected}, got {actual}.");
    }

    private static void Near(double expected, double actual, string description) =>
        Check(Math.Abs(expected - actual) < 1e-9, $"{description}: expected {expected}, got {actual}.");

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
    }
}
