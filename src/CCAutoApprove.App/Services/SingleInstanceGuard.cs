namespace CCAutoApprove.App.Services;

public sealed class SingleInstanceGuard : IDisposable
{
    public const string DefaultMutexName = @"Local\CCAutoApprove.App.Singleton";

    private readonly string mutexName;
    private Mutex? mutex;
    private bool ownsMutex;

    public SingleInstanceGuard(string mutexName = DefaultMutexName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mutexName);
        this.mutexName = mutexName;
    }

    public bool TryAcquire()
    {
        if (mutex is not null)
        {
            return ownsMutex;
        }

        mutex = new Mutex(initiallyOwned: true, mutexName, out bool createdNew);
        ownsMutex = createdNew;
        return createdNew;
    }

    public void Dispose()
    {
        if (ownsMutex)
        {
            mutex?.ReleaseMutex();
            ownsMutex = false;
        }

        mutex?.Dispose();
        mutex = null;
    }
}
