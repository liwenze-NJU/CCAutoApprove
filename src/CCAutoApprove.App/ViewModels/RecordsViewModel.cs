using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using CCAutoApprove.App.Commands;
using CCAutoApprove.App.Services;
using CCAutoApprove.Core.Abstractions;
using CCAutoApprove.Core.Models;

namespace CCAutoApprove.App.ViewModels;

public sealed class RecordsViewModel : INotifyPropertyChanged
{
    private const int MaximumRecordCount = 200;
    private readonly IAuditLog auditLog;
    private readonly Func<Task<bool>> confirmClearAsync;
    private readonly Func<CancellationToken, Task>? afterRefreshAsync;
    private AuditDetailLevel auditDetailLevel;
    private AuditRecordItemViewModel? selectedRecord;
    private string? errorMessage;

    public RecordsViewModel()
        : this(new EmptyAuditLog(), () => Task.FromResult(false))
    {
    }

    public RecordsViewModel(
        IAuditLog auditLog,
        Func<Task<bool>> confirmClearAsync,
        AuditDetailLevel auditDetailLevel = AuditDetailLevel.PrivacySafe,
        Func<CancellationToken, Task>? afterRefreshAsync = null)
    {
        this.auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
        this.confirmClearAsync = confirmClearAsync ?? throw new ArgumentNullException(nameof(confirmClearAsync));
        this.auditDetailLevel = auditDetailLevel;
        this.afterRefreshAsync = afterRefreshAsync;
        ClearCommand = new AsyncRelayCommand(ClearAsync, HandleErrorAsync);
        RefreshCommand = new AsyncRelayCommand(LoadAsync, HandleErrorAsync);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<AuditRecordItemViewModel> Records { get; } = [];
    public AuditDetailLevel AuditDetailLevel => auditDetailLevel;
    public string AuditDetailLevelText => StringResources.Get(auditDetailLevel switch
    {
        AuditDetailLevel.Disabled => "AuditDisabled",
        AuditDetailLevel.PrivacySafe => "AuditPrivacySafe",
        AuditDetailLevel.Detailed => "AuditDetailed",
        _ => "ValueUnknown"
    });

    public AuditRecordItemViewModel? SelectedRecord
    {
        get => selectedRecord;
        set
        {
            if (SetProperty(ref selectedRecord, value))
            {
                OnPropertyChanged(nameof(ShowDetails));
                OnPropertyChanged(nameof(SelectedSessionId));
                OnPropertyChanged(nameof(SelectedPermissionMode));
                OnPropertyChanged(nameof(SelectedToolInput));
                OnPropertyChanged(nameof(SelectedPermissionSuggestions));
            }
        }
    }

    public bool ShowDetails => auditDetailLevel == AuditDetailLevel.Detailed && SelectedRecord is not null;
    public string? SelectedSessionId => ShowDetails ? SelectedRecord?.SessionId : null;
    public string? SelectedPermissionMode => ShowDetails ? SelectedRecord?.PermissionMode : null;
    public string? SelectedToolInput => ShowDetails ? SelectedRecord?.ToolInput : null;
    public string? SelectedPermissionSuggestions => ShowDetails ? SelectedRecord?.PermissionSuggestions : null;

    public string? ErrorMessage
    {
        get => errorMessage;
        private set => SetProperty(ref errorMessage, value);
    }

    public AsyncRelayCommand ClearCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }

    public void SetAuditDetailLevel(AuditDetailLevel detailLevel)
    {
        if (auditDetailLevel == detailLevel)
        {
            return;
        }

        auditDetailLevel = detailLevel;
        OnPropertyChanged(nameof(AuditDetailLevel));
        OnPropertyChanged(nameof(AuditDetailLevelText));
        OnPropertyChanged(nameof(ShowDetails));
        OnPropertyChanged(nameof(SelectedSessionId));
        OnPropertyChanged(nameof(SelectedPermissionMode));
        OnPropertyChanged(nameof(SelectedToolInput));
        OnPropertyChanged(nameof(SelectedPermissionSuggestions));
    }

    public Task LoadAsync() => LoadAsync(CancellationToken.None);

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ErrorMessage = null;
        IReadOnlyList<AuditRecord> recent = await auditLog.ReadRecentAsync(
            MaximumRecordCount,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        Records.Clear();
        foreach (AuditRecord record in recent
                     .OrderByDescending(item => item.TimeUtc)
                     .Take(MaximumRecordCount))
        {
            Records.Add(new AuditRecordItemViewModel(record));
        }

        SelectedRecord = Records.FirstOrDefault();
        if (afterRefreshAsync is not null)
        {
            await afterRefreshAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    public Task<int> CountTodayApprovalsAsync(
        DateOnly localDate,
        TimeZoneInfo timeZone,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        cancellationToken.ThrowIfCancellationRequested();

        DateTime localStart = localDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        DateTime localEnd = localDate.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        DateTimeOffset startUtc = new(TimeZoneInfo.ConvertTimeToUtc(localStart, timeZone), TimeSpan.Zero);
        DateTimeOffset endUtc = new(TimeZoneInfo.ConvertTimeToUtc(localEnd, timeZone), TimeSpan.Zero);
        return auditLog.CountAllowedAsync(startUtc, endUtc, cancellationToken);
    }

    private async Task ClearAsync()
    {
        if (!await confirmClearAsync())
        {
            return;
        }

        await auditLog.ClearAsync(CancellationToken.None);
        await LoadAsync();
    }

    private Task HandleErrorAsync(Exception exception)
    {
        ErrorMessage = StringResources.Get("ErrorRecordsOperationFailed");
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
}
