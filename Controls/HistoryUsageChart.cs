using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NetTrace.Presentation;

namespace NetTrace.Controls;

// Native drawing over one immutable daily-usage snapshot. The existing page
// lifecycle supplies data; the chart owns no sampling, queries or timers.
public sealed class HistoryUsageChart : FrameworkElement
{
    private static readonly Typeface LabelTypeface = new("Segoe UI");
    private static readonly Brush LabelBrush = FrozenBrush("#94A3B8");
    private static readonly Pen GridPen = FrozenPen("#243244", 1);
    private static readonly Pen AxisPen = FrozenPen("#334155", 1);
    private ToolTip? _pointToolTip;
    private TextBlock? _toolTipText;
    private int _selectedIndex = -1;

    // Series colors describe measurements, independently of semantic statuses.
    public static Brush DownloadBrush { get; } = FrozenBrush("#38BDF8");
    public static Brush UploadBrush { get; } = FrozenBrush("#A78BFA");

    public HistoryUsageChartData Data { get; private set; } = HistoryUsageChartData.Create(
        [], null, DateTimeOffset.UtcNow, TimeZoneInfo.Local, HistoryUsageRange.SevenDays);

    public DailyUsagePoint? SelectedPoint { get; private set; }

    public HistoryUsageChart()
    {
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) ClearHover();
        };
    }

    public void UpdateData(HistoryUsageChartData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (ReferenceEquals(Data, data)) return;

        var keepHover = SelectedPoint is not null && IsVisible
            && Window.GetWindow(this)?.WindowState != WindowState.Minimized
            && data.Range == Data.Range && _selectedIndex >= 0 && _selectedIndex < data.Points.Count
            && data.Points[_selectedIndex].LocalDate == SelectedPoint.LocalDate;
        Data = data;
        if (keepHover) SelectPoint(data.Points[_selectedIndex], _selectedIndex);
        else ClearHover();
        InvalidateVisual();
    }

    public Rect PlotBounds
    {
        get
        {
            if (!double.IsFinite(ActualWidth) || !double.IsFinite(ActualHeight)
                || ActualWidth < 100 || ActualHeight < 60) return Rect.Empty;

            var widestLabel = Math.Max(Text(Data.MaximumText).Width, Text(Data.MiddleText).Width);
            var left = Math.Max(52, widestLabel + 8);
            var width = ActualWidth - left - 14;
            return width > 0 ? new Rect(left, 12, width, ActualHeight - 42) : Rect.Empty;
        }
    }

    // A whole daily column can be hovered even when its recorded total is zero.
    public DailyUsagePoint? GetPointAt(Point position)
    {
        var index = GetPointIndex(position);
        return index >= 0 ? Data.Points[index] : null;
    }

    private int GetPointIndex(Point position)
    {
        if (!IsVisible || Window.GetWindow(this)?.WindowState == WindowState.Minimized
            || !double.IsFinite(position.X) || !double.IsFinite(position.Y)) return -1;

        var plot = PlotBounds;
        if (plot.IsEmpty || Data.Points.Count == 0 || !plot.Contains(position)) return -1;
        return Math.Min(Data.Points.Count - 1,
            (int)((position.X - plot.Left) / plot.Width * Data.Points.Count));
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (!IsVisible || Window.GetWindow(this)?.WindowState == WindowState.Minimized) return;

        // Keep blank day columns hit-testable without adding a background panel.
        if (ActualWidth > 0 && ActualHeight > 0)
            drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var plot = PlotBounds;
        if (plot.IsEmpty) return;

        var labels = new[] { "0 B", Data.MiddleText, Data.MaximumText };
        var lastTick = Data.MaximumTotalBytes > 0 ? 2 : 0;
        for (var tick = 0; tick <= lastTick; tick++)
        {
            var y = plot.Bottom - plot.Height * tick / 2;
            drawingContext.DrawLine(GridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var label = Text(labels[tick]);
            drawingContext.DrawText(label, new Point(plot.Left - label.Width - 8, y - label.Height / 2));
        }
        drawingContext.DrawLine(AxisPen, plot.BottomLeft, plot.BottomRight);
        DrawDateLabels(drawingContext, plot);

        if (!Data.HasUsage || Data.Points.Count == 0)
        {
            DrawMessage(drawingContext, plot, "No recorded data usage in this period.");
            return;
        }

        var slotWidth = plot.Width / Data.Points.Count;
        var barWidth = Math.Min(Data.Points.Count <= 7 ? 48 : 18, slotWidth * 0.7);
        var maximum = (double)Math.Max(1, Data.MaximumTotalBytes);
        drawingContext.PushClip(new RectangleGeometry(plot));
        for (var index = 0; index < Data.Points.Count; index++)
        {
            var point = Data.Points[index];
            var downloadRatio = Math.Max(0, point.DownloadBytes) / maximum;
            var uploadRatio = Math.Max(0, point.UploadBytes) / maximum;
            var combinedRatio = downloadRatio + uploadRatio;
            if (combinedRatio > 1)
            {
                // Saturated integer totals can hide a sum larger than long.MaxValue.
                // Normalize both geometry ratios together to preserve their balance.
                downloadRatio /= combinedRatio;
                uploadRatio /= combinedRatio;
            }
            downloadRatio = Math.Clamp(downloadRatio, 0, 1);
            uploadRatio = Math.Clamp(uploadRatio, 0, 1 - downloadRatio);
            var downloadedHeight = plot.Height * downloadRatio;
            var uploadedHeight = plot.Height * uploadRatio;
            var left = plot.Left + slotWidth * (index + 0.5) - barWidth / 2;
            if (downloadedHeight > 0)
                drawingContext.DrawRectangle(DownloadBrush, null,
                    new Rect(left, plot.Bottom - downloadedHeight, barWidth, downloadedHeight));
            if (uploadedHeight > 0)
                drawingContext.DrawRectangle(UploadBrush, null,
                    new Rect(left, plot.Bottom - downloadedHeight - uploadedHeight, barWidth, uploadedHeight));
        }
        drawingContext.Pop();
    }

    private void DrawDateLabels(DrawingContext drawingContext, Rect plot)
    {
        if (Data.Points.Count == 0) return;
        foreach (var label in Data.AxisLabels)
        {
            if (label.Index < 0 || label.Index >= Data.Points.Count) continue;
            var text = Text(label.Text);
            var center = plot.Left + plot.Width * (label.Index + 0.5) / Data.Points.Count;
            var left = Math.Clamp(center - text.Width / 2, plot.Left, Math.Max(plot.Left, plot.Right - text.Width));
            drawingContext.DrawText(text, new Point(left, plot.Bottom + 8));
        }
    }

    private void DrawMessage(DrawingContext drawingContext, Rect plot, string message)
    {
        var text = Text(message);
        text.MaxTextWidth = plot.Width;
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
        var index = GetPointIndex(position);
        if (index < 0)
        {
            ClearHover();
            return;
        }
        var point = Data.Points[index];
        if (ReferenceEquals(SelectedPoint, point)) return;

        SelectPoint(point, index);
    }

    private void SelectPoint(DailyUsagePoint point, int index)
    {
        SelectedPoint = point;
        _selectedIndex = index;
        _toolTipText ??= new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 250 };
        _pointToolTip ??= new ToolTip { Content = _toolTipText, MaxWidth = 274 };
        _toolTipText.Text = point.ToolTipText;
        ToolTip = _pointToolTip;
    }

    protected override void OnMouseLeave(MouseEventArgs args)
    {
        base.OnMouseLeave(args);
        ClearHover();
    }

    private void ClearHover()
    {
        SelectedPoint = null;
        _selectedIndex = -1;
        if (_pointToolTip is not null) _pointToolTip.IsOpen = false;
        ToolTip = null;
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
