using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NetTrace.Presentation;

namespace NetTrace.Controls;

// Draws the monitoring owner's existing speed samples only while visible.
// This control creates no sampler, query, worker or timer.
public sealed class DashboardTrafficChart : FrameworkElement
{
    private static readonly Typeface LabelTypeface = new("Segoe UI");
    private static readonly Brush LabelBrush = FrozenBrush("#94A3B8");
    private static readonly Pen GridPen = FrozenPen("#243244", 1);
    private static readonly Pen AxisPen = FrozenPen("#334155", 1);
    private static readonly Pen DownloadPen = FrozenPen("#22C55E", 2);
    private static readonly Pen UploadPen = FrozenPen("#38BDF8", 2);
    private ToolTip? _sampleToolTip;
    private TextBlock? _toolTipText;
    private Point? _hoverPosition;
    private Window? _ownerWindow;
    private IReadOnlyList<DashboardTrafficSample>? _sampleSnapshot;
    private IReadOnlyList<DashboardTrafficSample>? _pendingSamples;

    public static Brush DownloadBrush { get; } = DownloadPen.Brush;
    public static Brush UploadBrush { get; } = UploadPen.Brush;

    public DashboardTrafficChartData Data { get; private set; } = DashboardTrafficChartData.Empty;
    public DashboardTrafficSample? SelectedSample { get; private set; }

    public DashboardTrafficChart()
    {
        Loaded += (_, _) =>
        {
            ObserveWindow();
            if (_pendingSamples is not null) UpdateSamples(_pendingSamples);
        };
        Unloaded += (_, _) =>
        {
            StopObservingWindow();
            ClearHover();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) ClearHover();
            else if (CanRender)
            {
                if (_pendingSamples is not null) UpdateSamples(_pendingSamples);
                InvalidateVisual();
            }
        };
    }

    public void UpdateSamples(IReadOnlyList<DashboardTrafficSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        // A parent can become visible before this child. Retain only the raw
        // snapshot here, then project it when the control itself is visible.
        _pendingSamples = samples;
        if (!CanRender || ReferenceEquals(_sampleSnapshot, samples)) return;
        _sampleSnapshot = samples;
        UpdateData(samples.Count == 0 ? DashboardTrafficChartData.Empty
            : DashboardTrafficChartData.Create(samples, samples[^1].TimestampUtc));
    }

    public void UpdateData(DashboardTrafficChartData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (ReferenceEquals(Data, data)) return;

        Data = data;
        if (!CanRender)
        {
            ClearHover();
            return;
        }

        // A stationary pointer follows the sample now below it as time moves.
        if (_hoverPosition is Point position) UpdateHover(position);
        else ClearSelection();
        InvalidateVisual();
    }

    public Rect PlotBounds
    {
        get
        {
            if (!double.IsFinite(ActualWidth) || !double.IsFinite(ActualHeight)
                || ActualWidth < 100 || ActualHeight < 80) return Rect.Empty;

            var widestLabel = Math.Max(Text(Data.MaximumText).Width, Text(Data.MiddleText).Width);
            var left = Math.Max(52, widestLabel + 8);
            var width = ActualWidth - left - 12;
            var height = ActualHeight - 58;
            return width > 0 && height > 0 ? new Rect(left, 28, width, height) : Rect.Empty;
        }
    }

    public DashboardTrafficSample? GetSampleAt(Point position)
    {
        if (!CanRender || !double.IsFinite(position.X) || !double.IsFinite(position.Y)) return null;
        var plot = PlotBounds;
        if (plot.IsEmpty || !plot.Contains(position) || Data.Samples.Count == 0) return null;

        var window = Data.WindowEndUtc - Data.WindowStartUtc;
        var seconds = window.TotalSeconds * (position.X - plot.Left) / plot.Width;
        var timestamp = Data.WindowStartUtc.AddSeconds(seconds);
        DashboardTrafficSample? nearest = null;
        var nearestDistance = double.PositiveInfinity;
        foreach (var sample in Data.Samples)
        {
            var distance = Math.Abs((sample.TimestampUtc - timestamp).TotalSeconds);
            if (distance < nearestDistance)
            {
                nearest = sample;
                nearestDistance = distance;
            }
        }
        // Empty history and interrupted sampling remain genuine gaps.
        return nearestDistance <= 1.5 ? nearest : null;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (!CanRender) return;

        if (ActualWidth > 0 && ActualHeight > 0)
            drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var plot = PlotBounds;
        if (plot.IsEmpty) return;

        DrawLegend(drawingContext, plot);
        var labels = new[] { "0 B/s", Data.MiddleText, Data.MaximumText };
        var lastTick = Data.MaximumBytesPerSecond > 0 ? 2 : 0;
        for (var tick = 0; tick <= lastTick; tick++)
        {
            var y = plot.Bottom - plot.Height * tick / 2;
            drawingContext.DrawLine(GridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            if (tick == 1 && (labels[tick] == labels[0] || labels[tick] == labels[2])) continue;
            var label = Text(labels[tick]);
            drawingContext.DrawText(label, new Point(plot.Left - label.Width - 8, y - label.Height / 2));
        }
        drawingContext.DrawLine(AxisPen, plot.BottomLeft, plot.BottomRight);
        DrawTimeLabels(drawingContext, plot);

        drawingContext.PushClip(new RectangleGeometry(plot));
        DrawSeries(drawingContext, plot, Data.DownloadSegments, DownloadPen);
        DrawSeries(drawingContext, plot, Data.UploadSegments, UploadPen);
        drawingContext.Pop();

        if (Data.Samples.Count == 0)
            DrawMessage(drawingContext, plot, "Waiting for traffic samples…");
    }

    private void DrawSeries(DrawingContext drawingContext, Rect plot,
        IReadOnlyList<IReadOnlyList<DashboardTrafficChartPoint>> segments, Pen pen)
    {
        var maximum = Math.Max(1, Data.MaximumBytesPerSecond);
        foreach (var segment in segments)
        {
            if (segment.Count == 0) continue;
            Point Position(DashboardTrafficChartPoint point) => new(
                plot.Left + plot.Width * Math.Clamp(point.Position, 0, 1),
                plot.Bottom - plot.Height * Math.Clamp(point.BytesPerSecond / maximum, 0, 1));

            if (segment.Count == 1)
            {
                // A first sample is visible before a line can be established.
                drawingContext.DrawEllipse(pen.Brush, null, Position(segment[0]), 2, 2);
                continue;
            }

            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(Position(segment[0]), false, false);
                for (var index = 1; index < segment.Count; index++)
                    context.LineTo(Position(segment[index]), true, false);
            }
            geometry.Freeze();
            drawingContext.DrawGeometry(null, pen, geometry);
        }
    }

    private void DrawLegend(DrawingContext drawingContext, Rect plot)
    {
        var download = Text("Download");
        var upload = Text("Upload");
        const double swatch = 12;
        const double gap = 6;
        const double seriesGap = 16;
        var width = swatch + gap + download.Width + seriesGap + swatch + gap + upload.Width;
        var left = Math.Max(plot.Left, plot.Right - width);
        var y = 9 + download.Height / 2;
        drawingContext.DrawLine(DownloadPen, new Point(left, y), new Point(left + swatch, y));
        drawingContext.DrawText(download, new Point(left + swatch + gap, 9));
        left += swatch + gap + download.Width + seriesGap;
        drawingContext.DrawLine(UploadPen, new Point(left, y), new Point(left + swatch, y));
        drawingContext.DrawText(upload, new Point(left + swatch + gap, 9));
    }

    private void DrawTimeLabels(DrawingContext drawingContext, Rect plot)
    {
        var seconds = (double)Data.WindowDurationSeconds;
        var middleSeconds = seconds >= 120 ? 60 : seconds / 2;
        var middlePosition = seconds > 0 ? 1 - middleSeconds / seconds : 0.5;
        var labels = new[]
        {
            (Position: 0.0, Text: $"{seconds:0}s"),
            (Position: middlePosition, Text: $"{middleSeconds:0}s"),
            (Position: 1.0, Text: "Now")
        };
        var previousRight = double.NegativeInfinity;
        foreach (var label in labels)
        {
            var text = Text(label.Text);
            var left = Math.Clamp(plot.Left + plot.Width * label.Position - text.Width / 2,
                plot.Left, Math.Max(plot.Left, plot.Right - text.Width));
            if (left < previousRight + 8) continue;
            drawingContext.DrawText(text, new Point(left, plot.Bottom + 8));
            previousRight = left + text.Width;
        }
    }

    private void DrawMessage(DrawingContext drawingContext, Rect plot, string message)
    {
        var text = Text(message);
        text.MaxTextWidth = Math.Max(1, plot.Width);
        drawingContext.DrawText(text, new Point(plot.Left + Math.Max(0, (plot.Width - text.Width) / 2),
            plot.Top + Math.Max(0, (plot.Height - text.Height) / 2)));
    }

    protected override void OnMouseMove(MouseEventArgs args)
    {
        base.OnMouseMove(args);
        UpdateHover(args.GetPosition(this));
    }

    private void UpdateHover(Point position)
    {
        _hoverPosition = position;
        var sample = GetSampleAt(position);
        if (sample is null)
        {
            ClearSelection();
            return;
        }
        if (ReferenceEquals(SelectedSample, sample)) return;

        SelectedSample = sample;
        _toolTipText ??= new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 250 };
        _sampleToolTip ??= new ToolTip { Content = _toolTipText, MaxWidth = 274 };
        _toolTipText.Text = sample.ToolTipText;
        ToolTip = _sampleToolTip;
    }

    protected override void OnMouseLeave(MouseEventArgs args)
    {
        base.OnMouseLeave(args);
        ClearHover();
    }

    private bool CanRender => IsVisible && Window.GetWindow(this)?.WindowState != WindowState.Minimized;

    private void ClearSelection()
    {
        SelectedSample = null;
        if (_sampleToolTip is not null) _sampleToolTip.IsOpen = false;
        ToolTip = null;
    }

    private void ClearHover()
    {
        _hoverPosition = null;
        ClearSelection();
    }

    private void ObserveWindow()
    {
        var window = Window.GetWindow(this);
        if (ReferenceEquals(_ownerWindow, window)) return;
        StopObservingWindow();
        _ownerWindow = window;
        if (_ownerWindow is not null) _ownerWindow.StateChanged += OwnerWindowOnStateChanged;
    }

    private void StopObservingWindow()
    {
        if (_ownerWindow is not null) _ownerWindow.StateChanged -= OwnerWindowOnStateChanged;
        _ownerWindow = null;
    }

    private void OwnerWindowOnStateChanged(object? sender, EventArgs args)
    {
        if (!CanRender) ClearHover();
        else
        {
            if (_pendingSamples is not null) UpdateSamples(_pendingSamples);
            InvalidateVisual();
        }
    }

    private FormattedText Text(string value) => new(value, CultureInfo.CurrentCulture,
        FlowDirection.LeftToRight, LabelTypeface, 11, LabelBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

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
