namespace NetTrace.Services;

// Passive route-change events reuse the VPN observer's existing debounced refresh.
public interface IVpnRouteChangeSource : IDisposable
{
    event EventHandler? Changed;
    void Start();
}
