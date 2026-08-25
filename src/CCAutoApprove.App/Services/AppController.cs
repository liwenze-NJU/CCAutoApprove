using CCAutoApprove.Core.Abstractions;
using CCAutoApprove.Core.Models;

namespace CCAutoApprove.App.Services;

public sealed class AppController
{
    public const string HeartbeatWriteFailed = nameof(HeartbeatWriteFailed);
    public const string HookNotOperational = nameof(HookNotOperational);
    public const string SelectedProjectNotFound = nameof(SelectedProjectNotFound);
    public const string SelectedProjectRequired = nameof(SelectedProjectRequired);

    private readonly ISettingsStore settingsStore;
    private readonly IDirectoryService directoryService;
    private readonly IHookHealthService hookHealthService;
    private readonly HeartbeatService heartbeatService;
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly object stateLock = new();
    private PersistentSettings settings = new();
    private string selectedProject = string.Empty;
    private string? errorCode;
    private bool isEnabled;
    private bool isInitialized;
    private bool isShutdown;

    public AppController(
        ISettingsStore settingsStore,
        IDirectoryService directoryService,
        IHookHealthService hookHealthService,
        HeartbeatService heartbeatService)
    {
        this.settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        this.directoryService = directoryService ?? throw new ArgumentNullException(nameof(directoryService));
        this.hookHealthService = hookHealthService ?? throw new ArgumentNullException(nameof(hookHealthService));
        this.heartbeatService = heartbeatService ?? throw new ArgumentNullException(nameof(heartbeatService));
        heartbeatService.Faulted += OnHeartbeatFaulted;
    }

    public event EventHandler? StateChanged;

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

    public string SelectedProject
    {
        get
        {
            lock (stateLock)
            {
                return selectedProject;
            }
        }
    }

    public string? ErrorCode
    {
        get
        {
            lock (stateLock)
            {
                return errorCode;
            }
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (isInitialized)
            {
                return;
            }

            settings = await settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            string project = settings.SelectedProject ?? string.Empty;
            await heartbeatService.InitializeAsync(project, cancellationToken).ConfigureAwait(false);
            lock (stateLock)
            {
                selectedProject = project;
                isEnabled = false;
                errorCode = null;
                isInitialized = true;
            }
        }
        finally
        {
            operationGate.Release();
        }

        OnStateChanged();
    }

    public async Task<bool> EnableAsync(string? project, CancellationToken cancellationToken)
    {
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool raiseStateChanged = false;
        try
        {
            EnsureReady();
            if (string.IsNullOrWhiteSpace(project))
            {
                SetValidationError(SelectedProjectRequired);
                raiseStateChanged = true;
                return false;
            }
            if (!directoryService.Exists(project))
            {
                SetValidationError(SelectedProjectNotFound);
                raiseStateChanged = true;
                return false;
            }
            if (!await hookHealthService.IsOperationalAsync(cancellationToken).ConfigureAwait(false))
            {
                SetValidationError(HookNotOperational);
                raiseStateChanged = true;
                return false;
            }

            PersistentSettings updatedSettings = await settingsStore.UpdateAsync(
                    current => current with { SelectedProject = project },
                    cancellationToken)
                .ConfigureAwait(false);
            settings = updatedSettings;
            await heartbeatService.StopAsync().ConfigureAwait(false);
            try
            {
                await heartbeatService.EnableAsync(project, cancellationToken).ConfigureAwait(false);
                lock (stateLock)
                {
                    selectedProject = project;
                    isEnabled = true;
                    errorCode = null;
                }
                heartbeatService.Start();
                if (!heartbeatService.IsEnabled || !heartbeatService.IsRunning)
                {
                    Task? disableTask = heartbeatService.IsEnabled
                        ? heartbeatService.DisableAndStopAsync()
                        : null;
                    lock (stateLock)
                    {
                        isEnabled = false;
                        errorCode ??= HeartbeatWriteFailed;
                    }

                    if (disableTask is not null)
                    {
                        try
                        {
                            await disableTask.ConfigureAwait(false);
                        }
                        catch
                        {
                            // In-memory state and loop ownership are already disabled.
                        }
                    }

                    raiseStateChanged = true;
                    return false;
                }
            }
            catch
            {
                lock (stateLock)
                {
                    selectedProject = project;
                    isEnabled = false;
                    errorCode = HeartbeatWriteFailed;
                }
                raiseStateChanged = true;
                Task disableTask = heartbeatService.DisableAndStopAsync();

                try
                {
                    await disableTask.ConfigureAwait(false);
                }
                catch
                {
                    // The original enable failure remains the operation result; safety state is already disabled.
                }
                throw;
            }
            raiseStateChanged = true;
            return true;
        }
        finally
        {
            operationGate.Release();
            if (raiseStateChanged)
            {
                OnStateChanged();
            }
        }
    }

    public async Task PauseAsync(CancellationToken cancellationToken)
    {
        await operationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        bool changed = false;
        try
        {
            EnsureInitialized();
            if (!IsEnabled)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return;
            }

            lock (stateLock)
            {
                isEnabled = false;
                errorCode = null;
            }
            changed = true;
            await heartbeatService.DisableAndStopAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            operationGate.Release();
            if (changed)
            {
                OnStateChanged();
            }
        }
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        await operationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        bool changed = false;
        try
        {
            if (!isInitialized || isShutdown)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return;
            }

            bool disabledStateSaved = false;
            lock (stateLock)
            {
                isEnabled = false;
            }
            changed = true;
            try
            {
                await heartbeatService.DisableAndStopAsync().ConfigureAwait(false);
                disabledStateSaved = true;
            }
            finally
            {
                lock (stateLock)
                {
                    isShutdown = disabledStateSaved;
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            operationGate.Release();
            if (changed)
            {
                OnStateChanged();
            }
        }
    }

    private void OnHeartbeatFaulted(object? sender, HeartbeatFaultedEventArgs eventArgs)
    {
        lock (stateLock)
        {
            isEnabled = false;
            errorCode = eventArgs.Kind == HeartbeatFaultKind.SelectedProjectUnavailable
                ? SelectedProjectNotFound
                : HeartbeatWriteFailed;
        }
        OnStateChanged();
    }

    private void SetValidationError(string value)
    {
        lock (stateLock)
        {
            errorCode = value;
        }
    }

    private void EnsureReady()
    {
        EnsureInitialized();
        if (isShutdown)
        {
            throw new InvalidOperationException("App controller has been shut down.");
        }
    }

    private void EnsureInitialized()
    {
        if (!isInitialized)
        {
            throw new InvalidOperationException("App controller has not been initialized.");
        }
    }

    private void OnStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);
}
