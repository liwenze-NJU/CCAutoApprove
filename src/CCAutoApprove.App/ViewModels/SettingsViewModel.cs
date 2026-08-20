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
    private readonly Func<Task<bool>> confirmDeleteDetailedLogsAsync;
    private readonly Func<Task> installHookAsync;
    private readonly Func<Task> runDoctorAsync;
    private readonly Func<Task> uninstallHookAsync;
    private PersistentSettings settings = new();
    private AuditDetailLevel auditDetailLevel = AuditDetailLevel.PrivacySafe;
    private bool startupEnabled;
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
        Func<Task>? uninstallHookAsync = null)
    {
        this.settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        this.auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
        this.confirmDeleteDetailedLogsAsync = confirmDeleteDetailedLogsAsync
            ?? throw new ArgumentNullException(nameof(confirmDeleteDetailedLogsAsync));
        this.installHookAsync = installHookAsync ?? (() => Task.CompletedTask);
        this.runDoctorAsync = runDoctorAsync ?? (() => Task.CompletedTask);
        this.uninstallHookAsync = uninstallHookAsync ?? (() => Task.CompletedTask);
        SelectDisabledCommand = CreateAuditCommand(AuditDetailLevel.Disabled);
        SelectPrivacySafeCommand = CreateAuditCommand(AuditDetailLevel.PrivacySafe);
        SelectDetailedCommand = CreateAuditCommand(AuditDetailLevel.Detailed);
        InstallHookCommand = CreateOperationCommand(this.installHookAsync);
        DoctorCommand = CreateOperationCommand(this.runDoctorAsync);
        UninstallHookCommand = CreateOperationCommand(this.uninstallHookAsync);
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

    public bool StartupEnabled
    {
        get => startupEnabled;
        private set => SetProperty(ref startupEnabled, value);
    }

    public bool CanChangeStartup => false;

    public string? OperationMessage
    {
        get => operationMessage;
        private set => SetProperty(ref operationMessage, value);
    }

    public AsyncRelayCommand SelectDisabledCommand { get; }
    public AsyncRelayCommand SelectPrivacySafeCommand { get; }
    public AsyncRelayCommand SelectDetailedCommand { get; }
    public AsyncRelayCommand InstallHookCommand { get; }
    public AsyncRelayCommand DoctorCommand { get; }
    public AsyncRelayCommand UninstallHookCommand { get; }

    public async Task LoadAsync()
    {
        settings = await settingsStore.LoadAsync(CancellationToken.None);
        AuditDetailLevel = settings.AuditDetailLevel;
        StartupEnabled = settings.StartWithWindows;
    }

    public async Task ChangeAuditDetailLevelAsync(AuditDetailLevel newLevel)
    {
        if (newLevel == AuditDetailLevel)
        {
            return;
        }

        bool leavingDetailed = AuditDetailLevel == AuditDetailLevel.Detailed
            && newLevel != AuditDetailLevel.Detailed;
        bool deleteExistingLogs = leavingDetailed && await confirmDeleteDetailedLogsAsync();
        PersistentSettings updated = settings with { AuditDetailLevel = newLevel };
        await settingsStore.SaveAsync(updated, CancellationToken.None);
        settings = updated;
        AuditDetailLevel = newLevel;
        AuditDetailLevelChanged?.Invoke(newLevel);
        if (deleteExistingLogs)
        {
            await auditLog.ClearAsync(CancellationToken.None);
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

    private Task HandleErrorAsync(Exception exception)
    {
        OperationMessage = exception.Message;
        return Task.CompletedTask;
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
    }

    private sealed class EmptyAuditLog : IAuditLog
    {
        public Task WriteAsync(ApprovalRequest request, ApprovalDecision decision, CancellationToken cancellationToken) =>
            Task.CompletedTask;
        public Task<IReadOnlyList<AuditRecord>> ReadRecentAsync(int maximumCount, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AuditRecord>>([]);
        public Task ClearAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeleteExpiredAsync(int retentionDays, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
