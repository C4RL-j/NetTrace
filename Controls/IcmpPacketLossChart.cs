using System.Globalization;
using System.Windows;
using System.Windows.Media;
using NetTrace.Models;
using NetTrace.Presentation;

namespace NetTrace.Controls;

// Draws the same completed-cycle snapshot as the latency/jitter chart, with a percent scale.
public sealed class IcmpPacketLossChart : FrameworkElement
{
    private static readonly Typeface LabelTypeface = new("Segoe UI");
    private static readonly Brush LabelBrush = FrozenBrush("#94A3B8");
    private static readonly Brush LossBrush = FrozenBrush("#A78BFA");
    private static readonly Pen GridPen = FrozenPen("#243244", 1);
    private static readonly Pen AxisPen = FrozenPen("#334155", 1);

    public IcmpStabilityChartData Data { get; private set; } =
        IcmpStabilityChartData.Create(Array.Empty<IcmpProbeResult>());

    public void UpdateData(IcmpStabilityChartData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (ReferenceEquals(Data, data)) return;

        Data = data;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (!IsVisible || Window.GetWindow(this)?.WindowState == WindowState.Minimized
            || ActualWidth < 100 || ActualHeight < 40) return;

        // Match the line chart's horizontal plot bounds and completed-cycle positions.
        var plot = new Rect(52, 7, Math.Max(1, ActualWidth - 66), Math.Max(1, ActualHeight - 14));
        for (var tick = 0; tick <= 2; tick++)
        {
            var y = plot.Bottom - plot.Height * tick / 2;
            drawingContext.DrawLine(GridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var label = Text($"{tick * 50}%");
            drawingContext.DrawText(label, new Point(plot.Left - label.Width - 8, y - label.Height / 2));
        }
        drawingContext.DrawLine(AxisPen, plot.BottomLeft, plot.BottomRight);

        if (Data.Count == 0)
        {
            DrawMessage(drawingContext, plot, "Waiting for packet loss samples...");
            return;
        }

        var spacing = Data.Count == 1 ? plot.Width : plot.Width / (Data.Count - 1);
        var barWidth = Math.Min(8, spacing * 0.7);
        foreach (var point in Data.PacketLossPoints)
        {
            // A genuine zero has no artificial height; the common baseline represents it.
            if (point.Percent is not { } percent || percent <= 0) continue;

            var center = plot.Left + plot.Width * point.Position;
            var left = Math.Clamp(center - barWidth / 2, plot.Left, plot.Right - barWidth);
            var height = plot.Height * (percent / 100);
            drawingContext.DrawRectangle(LossBrush, null,
                new Rect(left, plot.Bottom - height, barWidth, height));
        }
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
