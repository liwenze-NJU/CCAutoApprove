using CCAutoApprove.Core.Abstractions;
using CCAutoApprove.Core.Models;

namespace CCAutoApprove.App.Services;

public sealed class HeartbeatService : IAsyncDisposable
{
    private readonly IRuntimeStateStore runtimeStateStore;
    private readonly IClock clock;
    private readonly ICurrentProcessInfo processInfo;
    private readonly IDirectoryService directoryService;
    private readonly IHeartbeatTimer timer;
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly object stateLock = new();
    private readonly object loopLock = new();
    private CancellationTokenSource? loopCancellation;
    private Task? loopTask;
    private Guid instanceId;
    private string selectedProject = string.Empty;
    private bool isEnabled;
    private bool isInitialized;
    private bool isDisposed;

    public HeartbeatService(
        IRuntimeStateStore runtimeStateStore,
        IClock clock,
        ICurrentProcessInfo processInfo,
        IDirectoryService directoryService,
        IHeartbeatTimer timer)
    {
        this.runtimeStateStore = runtimeStateStore ?? throw new ArgumentNullException(nameof(runtimeStateStore));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.processInfo = processInfo ?? throw new ArgumentNullException(nameof(processInfo));
        this.directoryService = directoryService ?? throw new ArgumentNullException(nameof(directoryService));
        this.timer = timer ?? throw new ArgumentNullException(nameof(timer));
    }

    public event EventHandler<HeartbeatFaultedEventArgs>? Faulted;

    public Guid InstanceId
    {
        get
        {
            lock (stateLock)
            {
                return instanceId;
            }
        }
    }

    public bool IsEnabled
    {
        get
        {
            lock (stateLock)
            {
                return isEnabled;
            }
        }
    }

    public bool IsRunning
    {
        get
        {
            lock (loopLock)
            {
                return loopTask is { IsCompleted: false };
            }
        }
    }

    public async Task InitializeAsync(string selectedProject, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);
        Guid newInstanceId = Guid.NewGuid();
        string project = selectedProject ?? string.Empty;

        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await runtimeStateStore.SaveAsync(
                CreateState(enabled: false, project, newInstanceId), cancellationToken).ConfigureAwait(false);
            lock (stateLock)
            {
                instanceId = newInstanceId;
                this.selectedProject = project;
                isEnabled = false;
                isInitialized = true;
            }
        }
        finally
        {
            writeGate.Release();
        }
    }

    public async Task EnableAsync(string selectedProject, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedProject);
        EnsureInitialized();

        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Guid currentInstanceId;
            lock (stateLock)
            {
                currentInstanceId = instanceId;
            }
            await runtimeStateStore.SaveAsync(
                CreateState(enabled: true, selectedProject, currentInstanceId), cancellationToken).ConfigureAwait(false);
            lock (stateLock)
            {
                this.selectedProject = selectedProject;
                isEnabled = true;
            }
        }
        finally
        {
            writeGate.Release();
        }
    }

    public async Task DisableAsync(CancellationToken cancellationToken)
    {
        EnsureInitialized();

        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string project;
            Guid currentInstanceId;
            lock (stateLock)
            {
                project = selectedProject;
                currentInstanceId = instanceId;
                isEnabled = false;
            }
            await runtimeStateStore.SaveAsync(
                CreateState(enabled: false, project, currentInstanceId), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writeGate.Release();
        }
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);
        EnsureInitialized();
        if (!IsEnabled)
        {
            throw new InvalidOperationException("Heartbeat cannot start while disabled.");
        }

        lock (loopLock)
        {
            if (loopTask is { IsCompleted: false })
            {
                return;
            }

            loopCancellation?.Dispose();
            loopCancellation = new CancellationTokenSource();
            loopTask = RunLoopAsync(loopCancellation.Token);
        }
    }

    public async Task StopAsync()
    {
        Task? task;
        CancellationTokenSource? cancellation;
        lock (loopLock)
        {
            task = loopTask;
            cancellation = loopCancellation;
            cancellation?.Cancel();
        }

        if (task is not null)
        {
            await task.ConfigureAwait(false);
        }

        lock (loopLock)
        {
            if (ReferenceEquals(loopTask, task))
            {
                loopTask = null;
                loopCancellation = null;
                cancellation?.Dispose();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (isDisposed)
        {
            return;
        }

        isDisposed = true;
        await StopAsync().ConfigureAwait(false);
        await timer.DisposeAsync().ConfigureAwait(false);
        writeGate.Dispose();
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!IsEnabled)
                {
                    return;
                }

                HeartbeatFaultKind? fault = await TryWriteHeartbeatAsync(cancellationToken).ConfigureAwait(false);
                if (fault is HeartbeatFaultKind faultKind)
                {
                    Faulted?.Invoke(this, new HeartbeatFaultedEventArgs(faultKind));
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task<HeartbeatFaultKind?> TryWriteHeartbeatAsync(CancellationToken cancellationToken)
    {
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string project;
            Guid currentInstanceId;
            lock (stateLock)
            {
                if (!isEnabled)
                {
                    return null;
                }
                project = selectedProject;
                currentInstanceId = instanceId;
            }

            if (!directoryService.Exists(project))
            {
                lock (stateLock)
                {
                    isEnabled = false;
                }
                await TryWriteFinalDisabledStateAsync(project, currentInstanceId).ConfigureAwait(false);
                return HeartbeatFaultKind.SelectedProjectUnavailable;
            }

            RuntimeState enabledState = CreateState(enabled: true, project, currentInstanceId);
            try
            {
                await runtimeStateStore.SaveAsync(enabledState, cancellationToken).ConfigureAwait(false);
                return null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                try
                {
                    await runtimeStateStore.SaveAsync(enabledState, cancellationToken).ConfigureAwait(false);
                    return null;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    lock (stateLock)
                    {
                        isEnabled = false;
                    }

                    await TryWriteFinalDisabledStateAsync(project, currentInstanceId).ConfigureAwait(false);
                    return HeartbeatFaultKind.WriteFailed;
                }
            }
        }
        finally
        {
            writeGate.Release();
        }
    }

    private async Task TryWriteFinalDisabledStateAsync(string project, Guid currentInstanceId)
    {
        try
        {
            await runtimeStateStore.SaveAsync(
                CreateState(enabled: false, project, currentInstanceId), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    private RuntimeState CreateState(bool enabled, string project, Guid stateInstanceId) =>
        new(
            1,
            enabled,
            processInfo.ProcessId,
            processInfo.ProcessStartUtc,
            stateInstanceId,
            clock.UtcNow,
            project);

    private void EnsureInitialized()
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);
        lock (stateLock)
        {
            if (!isInitialized)
            {
                throw new InvalidOperationException("Heartbeat service has not been initialized.");
            }
        }
    }
}

public enum HeartbeatFaultKind
{
    WriteFailed,
    SelectedProjectUnavailable
}

public sealed class HeartbeatFaultedEventArgs(HeartbeatFaultKind kind) : EventArgs
{
    public HeartbeatFaultKind Kind { get; } = kind;
}
