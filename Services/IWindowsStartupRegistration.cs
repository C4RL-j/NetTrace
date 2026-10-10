namespace NetTrace.Services;

public interface IWindowsStartupRegistration
{
    bool IsEnabled();
    void Enable();
    void Disable();
}
