using System.Globalization;
using System.Windows;
using System.Windows.Media;
using NetTrace.Models;
using NetTrace.Presentation;
using NetTrace.Services;

namespace NetTrace.Controls;

// Native, immediate-mode WPF rendering: no chart worker, timer, animation or event subscription.
public sealed class IcmpStabilityChart : FrameworkElement
{
    private static readonly Typeface LabelTypeface = new("Segoe UI");
    private static readonly Brush LabelBrush = FrozenBrush("#94A3B8");
    private static readonly Pen GridPen = FrozenPen("#243244", 1);
    private static readonly Pen AxisPen = FrozenPen("#334155", 1);
    private static readonly Pen LatencyPen = FrozenPen("#38BDF8", 1.75);
    private static readonly Pen JitterPen = FrozenPen("#FBBF24", 1.75);

    public IcmpStabilityChartData Data { get; private set; } =
        IcmpStabilityChartData.Create(Array.Empty<IcmpProbeResult>());

    public void UpdateSamples(IReadOnlyList<IcmpProbeResult> samples,
        InternetReachabilityStatus reachability = InternetReachabilityStatus.Checking)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var count = Math.Min(samples.Count, IcmpMonitor.HistoryCapacity);
        var offset = samples.Count - count;
        var unchanged = Data.Count == count;
        for (var i = 0; unchanged && i < count; i++)
            unchanged = ReferenceEquals(Data.Samples[i], samples[offset + i]) &&
                Data.PacketLossPoints[i].Percent == IcmpPacketLossPresentation.Percent(samples[offset + i], reachability);
        if (unchanged) return;

        Data = IcmpStabilityChartData.Create(samples, reachability);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (!IsVisible || Window.GetWindow(this)?.WindowState == WindowState.Minimized
            || ActualWidth < 100 || ActualHeight < 60) return;

        var plot = new Rect(52, 12, Math.Max(1, ActualWidth - 66), Math.Max(1, ActualHeight - 42));
        for (var tick = 0; tick <= 2; tick++)
        {
            var value = Data.MaxMilliseconds * (tick / 2.0);
            var y = plot.Bottom - plot.Height * tick / 2;
            drawingContext.DrawLine(GridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var label = Text(FormatMilliseconds(value));
            drawingContext.DrawText(label, new Point(plot.Left - label.Width - 8, y - label.Height / 2));
        }
        drawingContext.DrawLine(AxisPen, plot.BottomLeft, plot.BottomRight);
        drawingContext.DrawText(Text("ms"), new Point(3, 0));
        DrawTimeLabels(drawingContext, plot);

        if (Data.Count == 0)
        {
            DrawMessage(drawingContext, plot, "Waiting for first measurement...");
            return;
        }
        if (Data.LatencySegments.Count == 0 && Data.JitterSegments.Count == 0)
        {
            DrawMessage(drawingContext, plot, "Latency and jitter unavailable for these samples.");
            return;
        }

        // Allow edge markers to remain visible while containing the plot within its padding.
        var clip = plot;
        clip.Inflate(3, 3);
        drawingContext.PushClip(new RectangleGeometry(clip));
        DrawSeries(drawingContext, plot, Data.LatencySegments, LatencyPen);
        DrawSeries(drawingContext, plot, Data.JitterSegments, JitterPen);
        drawingContext.Pop();
    }

    private void DrawSeries(DrawingContext drawingContext, Rect plot,
        IReadOnlyList<IReadOnlyList<IcmpStabilityChartPoint>> segments, Pen pen)
    {
        foreach (var segment in segments)
        {
            Point Project(IcmpStabilityChartPoint point) => new(
                plot.Left + plot.Width * point.Position,
                plot.Bottom - plot.Height * (point.Milliseconds / Data.MaxMilliseconds));
            if (segment.Count == 1)
            {
                drawingContext.DrawEllipse(pen.Brush, null, Project(segment[0]), 2.5, 2.5);
                continue;
            }

            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(Project(segment[0]), false, false);
                for (var i = 1; i < segment.Count; i++)
                    context.LineTo(Project(segment[i]), true, false);
            }
            geometry.Freeze();
            drawingContext.DrawGeometry(null, pen, geometry);
        }
    }

    private void DrawTimeLabels(DrawingContext drawingContext, Rect plot)
    {
        if (Data.Count == 0) return;
        var newest = Text(Data.Samples[^1].TimestampUtc.ToLocalTime().ToString("t", CultureInfo.CurrentCulture));
        var y = plot.Bottom + 8;
        drawingContext.DrawText(newest, new Point(Math.Max(plot.Left, plot.Right - newest.Width), y));
        if (Data.Count == 1) return;

        var oldest = Text(Data.Samples[0].TimestampUtc.ToLocalTime().ToString("t", CultureInfo.CurrentCulture));
        if (oldest.Width + newest.Width + 20 < plot.Width)
            drawingContext.DrawText(oldest, new Point(plot.Left, y));
    }

    private void DrawMessage(DrawingContext drawingContext, Rect plot, string message)
    {
        var text = Text(message);
        text.MaxTextWidth = plot.Width;
        drawingContext.DrawText(text, new Point(plot.Left + Math.Max(0, (plot.Width - text.Width) / 2),
            plot.Top + Math.Max(0, (plot.Height - text.Height) / 2)));
    }

    private FormattedText Text(string value) => new(value, CultureInfo.CurrentCulture,
        FlowDirection.LeftToRight, LabelTypeface, 11, LabelBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private static string FormatMilliseconds(double value) => value >= 10000
        ? value.ToString("0.#E+0", CultureInfo.CurrentCulture)
        : value.ToString("0.##", CultureInfo.CurrentCulture);

    private static Brush FrozenBrush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(string color, double thickness)
    {
        var pen = new Pen(FrozenBrush(color), thickness);
        pen.Freeze();
        return pen;
    }
}
