using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NetTrace.Services;

public enum AppUsageIconKind { Application, System, Unknown }

public interface IAppUsageIconCache
{
    ImageSource GetFallback(AppUsageIconKind kind);
    Task<ImageSource> GetIconAsync(string? executablePath, AppUsageIconKind kind = AppUsageIconKind.Application);
}

// Icons are presentation metadata. No executable is started and no collector,
// usage storage or dispatcher is consulted while resolving this session cache.
public sealed class AppUsageIconCache : IAppUsageIconCache
{
    private const int MaximumCachedPaths = 1024;
    private readonly object cacheGate = new();
    private readonly Dictionary<string, Task<ImageSource>> cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim extractionSlots = new(3, 3);
    private readonly Func<string, ImageSource?> extractor;
    private static readonly ImageSource ApplicationFallback = CreateApplicationFallback();
    private static readonly ImageSource SystemFallback = CreateSystemFallback();
    private static readonly ImageSource UnknownFallback = CreateUnknownFallback();

    public static AppUsageIconCache Default { get; } = new();

    public AppUsageIconCache(Func<string, ImageSource?>? extractor = null) =>
        this.extractor = extractor ?? ExtractAssociatedIcon;

    public ImageSource GetFallback(AppUsageIconKind kind) => kind switch
    {
        AppUsageIconKind.System => SystemFallback,
        AppUsageIconKind.Unknown => UnknownFallback,
        _ => ApplicationFallback
    };

    public Task<ImageSource> GetIconAsync(string? executablePath, AppUsageIconKind kind = AppUsageIconKind.Application)
    {
        var fallback = GetFallback(kind);
        // This lexical check performs no path resolution or filesystem access.
        // Network/device paths never reach the shell, even on a background task.
        if (kind != AppUsageIconKind.Application || !HasLocalDriveRoot(executablePath))
            return Task.FromResult(fallback);
        return Task.Run(() => FindOrAddAsync(executablePath!, fallback));
    }

    private Task<ImageSource> FindOrAddAsync(string path, ImageSource fallback)
    {
        string normalized;
        try
        {
            normalized = Path.GetFullPath(path.Replace('/', '\\'));
            if (!HasLocalDriveRoot(normalized)) return Task.FromResult(fallback);
        }
        catch (Exception) { return Task.FromResult(fallback); }

        lock (cacheGate)
        {
            if (cache.TryGetValue(normalized, out var existing)) return existing;
            // Keep in-flight entries until they finish and cache negative results.
            // A full session cache uses fallback for new paths instead of evicting
            // pending work and repeatedly extracting the same executable.
            if (cache.Count >= MaximumCachedPaths) return Task.FromResult(fallback);
            var pending = Task.Run(() => ExtractAsync(normalized, fallback));
            cache.Add(normalized, pending);
            return pending;
        }
    }

    private async Task<ImageSource> ExtractAsync(string path, ImageSource fallback)
    {
        await extractionSlots.WaitAsync().ConfigureAwait(false);
        try
        {
            // A mapped network drive also has a drive-letter path. Reject it
            // before File.Exists or shell extraction can wait on a remote host.
            if (new DriveInfo(path[..3]).DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.CDRom or DriveType.Ram))
                return fallback;
            if (!File.Exists(path)) return fallback;
            var image = extractor(path);
            if (image is null) return fallback;
            if (!image.IsFrozen)
            {
                if (!image.CanFreeze) return fallback;
                image.Freeze();
            }
            return image;
        }
        catch (Exception) { return fallback; }
        finally { extractionSlots.Release(); }
    }

    private static bool HasLocalDriveRoot(string? path) => !string.IsNullOrWhiteSpace(path)
        && path.Length >= 4 && char.IsAsciiLetter(path[0]) && path[1] == ':'
        && path[2] is '\\' or '/' && !path.AsSpan(3).Contains(':') && !path.Contains('\0');

    private static ImageSource? ExtractAssociatedIcon(string path)
    {
        // ExtractAssociatedIcon owns the returned HICON. WPF copies its pixels;
        // disposing Icon releases that handle exactly once after freezing them.
        using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
        if (icon is null) return null;
        var source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty,
            BitmapSizeOptions.FromWidthAndHeight(32, 32));
        source.Freeze();
        return source;
    }

    private static ImageSource CreateApplicationFallback()
    {
        var group = new DrawingGroup();
        var outline = new Pen(Brush(139, 160, 187), 1.5);
        group.Children.Add(new GeometryDrawing(Brush(38, 51, 69), outline,
            new RectangleGeometry(new Rect(3, 4, 18, 16), 2, 2)));
        group.Children.Add(new GeometryDrawing(Brush(139, 160, 187), null,
            new RectangleGeometry(new Rect(6, 7, 12, 2), 0.5, 0.5)));
        group.Children.Add(new GeometryDrawing(Brush(139, 160, 187), null,
            new RectangleGeometry(new Rect(7, 12, 4, 4), 0.5, 0.5)));
        group.Children.Add(new GeometryDrawing(Brush(84, 107, 136), null,
            new RectangleGeometry(new Rect(13, 12, 4, 4), 0.5, 0.5)));
        return Freeze(group);
    }

    private static ImageSource CreateSystemFallback()
    {
        var group = new DrawingGroup();
        var fill = Brush(115, 172, 218);
        foreach (var rectangle in new[] { new Rect(3, 3, 8, 8), new Rect(13, 3, 8, 8),
            new Rect(3, 13, 8, 8), new Rect(13, 13, 8, 8) })
            group.Children.Add(new GeometryDrawing(fill, null, new RectangleGeometry(rectangle, 0.6, 0.6)));
        return Freeze(group);
    }

    private static ImageSource CreateUnknownFallback()
    {
        var group = new DrawingGroup();
        var pen = new Pen(Brush(152, 168, 191), 1.7) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        group.Children.Add(new GeometryDrawing(Brush(38, 51, 69), pen, new EllipseGeometry(new Point(12, 12), 9, 9)));
        var question = new StreamGeometry();
        using (var context = question.Open())
        {
            context.BeginFigure(new Point(8.8, 8.5), false, false);
            context.BezierTo(new Point(9, 5.3), new Point(15.8, 5.6), new Point(15.2, 9), true, false);
            context.BezierTo(new Point(15, 10.5), new Point(12, 10.7), new Point(12, 13), true, false);
        }
        group.Children.Add(new GeometryDrawing(null, pen, question));
        group.Children.Add(new GeometryDrawing(Brush(152, 168, 191), null, new EllipseGeometry(new Point(12, 16), 1, 1)));
        return Freeze(group);
    }

    private static SolidColorBrush Brush(byte red, byte green, byte blue) => new(Color.FromRgb(red, green, blue));
    private static ImageSource Freeze(DrawingGroup group)
    {
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }
}
