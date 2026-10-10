using System.Drawing;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace NetTrace.Services;

/// <summary>Owns the Windows notification-area icon and its standard context menu.</summary>
public sealed class SystemTrayIcon : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Icon _ownedIcon;
    private bool _disposed;

    public SystemTrayIcon(Dispatcher dispatcher, Action openDashboard, Action exit)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(openDashboard);
        ArgumentNullException.ThrowIfNull(exit);
        dispatcher.VerifyAccess();
        _dispatcher = dispatcher;
        _ownedIcon = LoadApplicationIcon();
        _menu = new Forms.ContextMenuStrip();
        _menu.Items.Add("Open NetTrace", null, (_, _) => Invoke(openDashboard));
        _menu.Items.Add("Exit", null, (_, _) => Invoke(exit));
        Forms.NotifyIcon? icon = null;
        try
        {
            icon = new Forms.NotifyIcon
            {
                Text = "NetTrace",
                Icon = _ownedIcon,
                ContextMenuStrip = _menu,
                Visible = true
            };
            icon.DoubleClick += (_, _) => Invoke(openDashboard);
            _icon = icon;
        }
        catch
        {
            icon?.Dispose();
            _menu.Dispose();
            _ownedIcon.Dispose();
            throw;
        }
    }

    private static Icon LoadApplicationIcon()
    {
        var resource = System.Windows.Application.GetResourceStream(
            new Uri("pack://application:,,,/NetTrace;component/assets/NetTrace.ico", UriKind.Absolute))
            ?? throw new InvalidOperationException("The embedded NetTrace icon could not be loaded.");
        using var stream = resource.Stream;
        using var icon = new Icon(stream, Forms.SystemInformation.SmallIconSize);
        return (Icon)icon.Clone();
    }

    private void Invoke(Action action)
    {
        if (_disposed || _dispatcher.HasShutdownStarted) return;
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_disposed && !_dispatcher.HasShutdownStarted) action();
        }));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _dispatcher.VerifyAccess();
        _disposed = true;
        try { _icon.Visible = false; }
        finally
        {
            try { _icon.Dispose(); }
            finally
            {
                try { _menu.Dispose(); }
                finally { _ownedIcon.Dispose(); }
            }
        }
    }
}
