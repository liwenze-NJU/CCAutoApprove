using System.ComponentModel;
using System.Runtime.CompilerServices;
using CCAutoApprove.App.Commands;
using CCAutoApprove.App.Services;
using CCAutoApprove.Core.Abstractions;
using CCAutoApprove.Core.Models;

namespace CCAutoApprove.App.ViewModels;

public sealed class SettingsViewModel : INotifyPropertyChanged
{
    private readonly ISettingsStore settingsStore;
    private readonly IAuditLog auditLog;
    private readonly IStartupManager startupManager;
    private readonly Func<Task<bool>> confirmDeleteDetailedLogsAsync;
    private readonly Func<Task<bool>> confirmEnableDetailedAsync;
    private readonly Func<Task> installHookAsync;
    private readonly Func<Task> runDoctorAsync;
    private readonly Func<Task> uninstallHookAsync;
    private readonly IHookMaintenanceService? hookMaintenanceService;
    private PersistentSettings settings = new();
    private AuditDetailLevel auditDetailLevel = AuditDetailLevel.PrivacySafe;
    private bool? startupEnabled;
    private string? operationMessage;

    public SettingsViewModel()
        : this(new MemorySettingsStore(), new EmptyAuditLog(), () => Task.FromResult(false))
    {
    }

    public SettingsViewModel(
        ISettingsStore settingsStore,
        IAuditLog auditLog,
        Func<Task<bool>> confirmDeleteDetailedLogsAsync,
        Func<Task>? installHookAsync = null,
        Func<Task>? runDoctorAsync = null,
        Func<Task>? uninstallHookAsync = null,
        IStartupManager? startupManager = null,
        Func<Task<bool>>? confirmEnableDetailedAsync = null,
        IHookMaintenanceService? hookMaintenanceService = null,
        string? applicationVersion = null)
    {
        this.settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        this.auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
        this.confirmDeleteDetailedLogsAsync = confirmDeleteDetailedLogsAsync
            ?? throw new ArgumentNullException(nameof(confirmDeleteDetailedLogsAsync));
        this.confirmEnableDetailedAsync = confirmEnableDetailedAsync
            ?? (() => Task.FromResult(false));
        this.installHookAsync = installHookAsync ?? (() => Task.CompletedTask);
        this.runDoctorAsync = runDoctorAsync ?? (() => Task.CompletedTask);
        this.uninstallHookAsync = uninstallHookAsync ?? (() => Task.CompletedTask);
        this.hookMaintenanceService = hookMaintenanceService;
        this.startupManager = startupManager ?? new DisabledStartupManager();
        ApplicationVersionText = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            StringResources.Get("ApplicationVersionFormat"),
            applicationVersion ?? ResolveApplicationVersion());
        SelectDisabledCommand = CreateAuditCommand(AuditDetailLevel.Disabled);
        SelectPrivacySafeCommand = CreateAuditCommand(AuditDetailLevel.PrivacySafe);
        SelectDetailedCommand = CreateAuditCommand(AuditDetailLevel.Detailed);
        InstallHookCommand = hookMaintenanceService is null
            ? CreateOperationCommand(this.installHookAsync)
            : CreateHookOperationCommand(hookMaintenanceService.InstallAsync);
        DoctorCommand = hookMaintenanceService is null
            ? CreateOperationCommand(this.runDoctorAsync)
            : CreateHookOperationCommand(hookMaintenanceService.DoctorAsync);
        UninstallHookCommand = hookMaintenanceService is null
            ? CreateOperationCommand(this.uninstallHookAsync)
            : CreateHookOperationCommand(hookMaintenanceService.UninstallAsync);
        ChangeStartupEnabledCommand = new AsyncRelayCommand(
            parameter => parameter is bool enabled
                ? ChangeStartupEnabledAsync(enabled)
                : Task.CompletedTask,
            HandleStartupErrorAsync,
            () => CanChangeStartup);
        RecheckStartupCommand = new AsyncRelayCommand(
            RecheckStartupAsync,
            HandleStartupErrorAsync,
            () => CanRecheckStartup);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<AuditDetailLevel>? AuditDetailLevelChanged;

    public AuditDetailLevel AuditDetailLevel
    {
        get => auditDetailLevel;
        private set
        {
            if (SetProperty(ref auditDetailLevel, value))
            {
                OnPropertyChanged(nameof(IsAuditDisabled));
                OnPropertyChanged(nameof(IsPrivacySafe));
                OnPropertyChanged(nameof(IsDetailed));
                OnPropertyChanged(nameof(ShowDetailedWarning));
            }
        }
    }

    public bool IsAuditDisabled => AuditDetailLevel == AuditDetailLevel.Disabled;
    public bool IsPrivacySafe => AuditDetailLevel == AuditDetailLevel.PrivacySafe;
    public bool IsDetailed => AuditDetailLevel == AuditDetailLevel.Detailed;
    public bool ShowDetailedWarning => IsDetailed;

    public bool? StartupEnabled
    {
        get => startupEnabled;
        private set => SetStartupState(value);
    }

    public bool CanChangeStartup => StartupEnabled.HasValue;
    public bool CanRecheckStartup => !StartupEnabled.HasValue;

    public string? OperationMessage
    {
        get => operationMessage;
        private set => SetProperty(ref operationMessage, value);
    }

    public string ApplicationVersionText { get; }

    public AsyncRelayCommand SelectDisabledCommand { get; }
    public AsyncRelayCommand SelectPrivacySafeCommand { get; }
    public AsyncRelayCommand SelectDetailedCommand { get; }
    public AsyncRelayCommand InstallHookCommand { get; }
    public AsyncRelayCommand DoctorCommand { get; }
    public AsyncRelayCommand UninstallHookCommand { get; }
    public AsyncRelayCommand ChangeStartupEnabledCommand { get; }
    public AsyncRelayCommand RecheckStartupCommand { get; }

    public async Task LoadAsync()
    {
        settings = await settingsStore.LoadAsync(CancellationToken.None);
        AuditDetailLevel = settings.AuditDetailLevel;
        SetStartupState(ReadStartupState(), forceNotification: true);
        if (StartupEnabled is null)
        {
            OperationMessage = StringResources.Get("StartupStateUnknown");
        }
    }

    public async Task ChangeAuditDetailLevelAsync(AuditDetailLevel newLevel)
    {
        if (newLevel == AuditDetailLevel)
        {
            return;
        }

        if (newLevel == AuditDetailLevel.Detailed
            && AuditDetailLevel != AuditDetailLevel.Detailed
            && !await confirmEnableDetailedAsync())
        {
            return;
        }

        bool leavingDetailed = AuditDetailLevel == AuditDetailLevel.Detailed
            && newLevel != AuditDetailLevel.Detailed;
        bool deleteExistingLogs = leavingDetailed && await confirmDeleteDetailedLogsAsync();
        PersistentSettings updated = await settingsStore.UpdateAsync(
            current => current with { AuditDetailLevel = newLevel },
            CancellationToken.None);
        settings = updated;
        AuditDetailLevel = newLevel;
        AuditDetailLevelChanged?.Invoke(newLevel);
        if (deleteExistingLogs)
        {
            await auditLog.DeleteDetailedAsync(CancellationToken.None);
        }
    }

    public async Task ChangeStartupEnabledAsync(bool enabled)
    {
        bool? priorEnabled = StartupEnabled;
        if (!priorEnabled.HasValue)
        {
            OperationMessage = StringResources.Get("StartupStateUnknown");
            return;
        }

        if (enabled == priorEnabled.Value)
        {
            return;
        }

        try
        {
            if (enabled)
            {
                startupManager.Enable();
            }
            else
            {
                startupManager.Disable();
            }

            PersistentSettings updated = await settingsStore.UpdateAsync(
                current => current with { StartWithWindows = enabled },
                CancellationToken.None);
            settings = updated;
            StartupEnabled = enabled;
            OperationMessage = null;
        }
        catch
        {
            RestoreStartupState(priorEnabled);
            SetStartupState(ReadStartupState(), forceNotification: true);
            OperationMessage = StartupEnabled is null
                ? StringResources.Get("StartupStateUnknown")
                : StringResources.Get("ErrorOperationFailed");
        }
    }

    private async Task HandleStartupErrorAsync(Exception exception)
    {
        SetStartupState(ReadStartupState(), forceNotification: true);
        OperationMessage = StartupEnabled is null
            ? StringResources.Get("StartupStateUnknown")
            : StringResources.Get("ErrorOperationFailed");
        await Task.CompletedTask;
    }

    private Task RecheckStartupAsync()
    {
        SetStartupState(ReadStartupState(), forceNotification: true);
        OperationMessage = StartupEnabled is null
            ? StringResources.Get("StartupStateUnknown")
            : null;
        return Task.CompletedTask;
    }

    private void RestoreStartupState(bool? priorEnabled)
    {
        if (!priorEnabled.HasValue)
        {
            return;
        }

        try
        {
            if (startupManager.IsEnabled() == priorEnabled.Value)
            {
                return;
            }

            if (priorEnabled.Value)
            {
                startupManager.Enable();
            }
            else
            {
                startupManager.Disable();
            }
        }
        catch
        {
        }
    }

    private bool? ReadStartupState()
    {
        try
        {
            return startupManager.IsEnabled();
        }
        catch
        {
            return null;
        }
    }

    private void SetStartupState(bool? value, bool forceNotification = false)
    {
        bool changed = !EqualityComparer<bool?>.Default.Equals(startupEnabled, value);
        if (changed)
        {
            startupEnabled = value;
            OnPropertyChanged(nameof(StartupEnabled));
        }
        else if (forceNotification)
        {
            OnPropertyChanged(nameof(StartupEnabled));
        }

        if (changed || forceNotification)
        {
            OnPropertyChanged(nameof(CanChangeStartup));
            OnPropertyChanged(nameof(CanRecheckStartup));
            ChangeStartupEnabledCommand.RaiseCanExecuteChanged();
            RecheckStartupCommand.RaiseCanExecuteChanged();
        }
    }

    private AsyncRelayCommand CreateAuditCommand(AuditDetailLevel detailLevel) =>
        new(() => ChangeAuditDetailLevelAsync(detailLevel), HandleErrorAsync);

    private AsyncRelayCommand CreateOperationCommand(Func<Task> operation) =>
        new(async () =>
        {
            OperationMessage = null;
            await operation();
            OperationMessage = StringResources.Get("OperationCompleted");
        }, HandleErrorAsync);

    private AsyncRelayCommand CreateHookOperationCommand(
        Func<CancellationToken, Task<HookOperationResult>> operation) =>
        new(async () =>
        {
            OperationMessage = null;
            HookOperationResult result = await operation(CancellationToken.None);
            OperationMessage = StringResources.Get(result.Outcome switch
            {
                HookOperationOutcome.Installed => "HookInstallSucceeded",
                HookOperationOutcome.Uninstalled => "HookUninstallSucceeded",
                HookOperationOutcome.Healthy => "HookDoctorHealthy",
                HookOperationOutcome.Unhealthy => "ErrorHookNotOperational",
                _ => "ErrorOperationFailed"
            });
        }, HandleErrorAsync);

    private Task HandleErrorAsync(Exception exception)
    {
        OperationMessage = StringResources.Get("ErrorOperationFailed");
        return Task.CompletedTask;
    }

    private static string ResolveApplicationVersion()
    {
        Version? version = typeof(SettingsViewModel).Assembly.GetName().Version;
        return version is null
            ? StringResources.Get("ValueUnknown")
            : $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private sealed class MemorySettingsStore : ISettingsStore
    {
        private PersistentSettings settings = new();

        public Task<PersistentSettings> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(settings);

        public Task SaveAsync(PersistentSettings settings, CancellationToken cancellationToken)
        {
            this.settings = settings;
            return Task.CompletedTask;
        }

        public async Task<PersistentSettings> UpdateAsync(
            Func<PersistentSettings, PersistentSettings> update,
            CancellationToken cancellationToken)
        {
            PersistentSettings updated = update(await LoadAsync(cancellationToken));
            await SaveAsync(updated, cancellationToken);
            return updated;
        }
    }

    private sealed class EmptyAuditLog : IAuditLog
    {
        public Task WriteAsync(ApprovalRequest request, ApprovalDecision decision, CancellationToken cancellationToken) =>
            Task.CompletedTask;
        public Task<IReadOnlyList<AuditRecord>> ReadRecentAsync(int maximumCount, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AuditRecord>>([]);
        public Task<int> CountAllowedAsync(
            DateTimeOffset startUtcInclusive,
            DateTimeOffset endUtcExclusive,
            CancellationToken cancellationToken) => Task.FromResult(0);
        public Task ClearAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeleteExpiredAsync(int retentionDays, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class DisabledStartupManager : IStartupManager
    {
        public bool IsEnabled() => false;
        public void Enable() { }
        public void Disable() { }
    }
}
