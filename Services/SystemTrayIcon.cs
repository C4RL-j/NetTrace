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
    private readonly DispatcherTimer _singleClickTimer;
    private System.Drawing.Point _popupAnchor;
    private System.Drawing.Rectangle? _popupTaskbarBounds;
    private bool _disposed;

    public System.Drawing.Point PopupAnchor => _popupAnchor;
    internal System.Drawing.Rectangle? PopupTaskbarBounds => _popupTaskbarBounds;

    public SystemTrayIcon(Dispatcher dispatcher, Action togglePopup, Action openDashboard,
        Action openSettings, Action dismissPopup, Action exit)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(togglePopup);
        ArgumentNullException.ThrowIfNull(openDashboard);
        ArgumentNullException.ThrowIfNull(openSettings);
        ArgumentNullException.ThrowIfNull(dismissPopup);
        ArgumentNullException.ThrowIfNull(exit);
        dispatcher.VerifyAccess();
        _dispatcher = dispatcher;
        _popupAnchor = Forms.Cursor.Position;
        _popupTaskbarBounds = TrayPopupPlacement.GetTaskbarBounds(_popupAnchor);
        // This one-shot input delay distinguishes a single click from Windows'
        // double-click gesture. It never polls or samples monitoring data.
        _singleClickTimer = new DispatcherTimer(DispatcherPriority.Input, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(Forms.SystemInformation.DoubleClickTime)
        };
        _singleClickTimer.Tick += (_, _) =>
        {
            _singleClickTimer.Stop();
            Invoke(togglePopup);
        };
        _ownedIcon = LoadApplicationIcon();
        _menu = new Forms.ContextMenuStrip();
        _menu.Items.Add("Open NetTrace", null, (_, _) => Invoke(openDashboard));
        _menu.Items.Add("Settings", null, (_, _) => Invoke(openSettings));
        _menu.Items.Add("Exit", null, (_, _) => Invoke(exit));
        _menu.Opening += (_, _) =>
        {
            _singleClickTimer.Stop();
            Invoke(dismissPopup);
        };
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
            icon.MouseClick += (_, args) =>
            {
                if (args.Button != Forms.MouseButtons.Left) return;
                _popupAnchor = Forms.Cursor.Position;
                // Snapshot the exposed taskbar before focus changes can hide it.
                _popupTaskbarBounds = TrayPopupPlacement.GetTaskbarBounds(_popupAnchor);
                _singleClickTimer.Stop();
                _singleClickTimer.Start();
            };
            icon.MouseDoubleClick += (_, args) =>
            {
                if (args.Button != Forms.MouseButtons.Left) return;
                _singleClickTimer.Stop();
                Invoke(openDashboard);
            };
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
        _singleClickTimer.Stop();
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
