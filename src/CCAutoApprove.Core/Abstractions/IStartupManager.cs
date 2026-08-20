namespace CCAutoApprove.Core.Abstractions;

public interface IStartupManager
{
    bool IsEnabled();
    void Enable();
    void Disable();
}
