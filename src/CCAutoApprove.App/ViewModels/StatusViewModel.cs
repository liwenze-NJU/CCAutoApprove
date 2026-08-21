using System.ComponentModel;
using System.Runtime.CompilerServices;
using CCAutoApprove.App.Commands;
using CCAutoApprove.App.Services;

namespace CCAutoApprove.App.ViewModels;

public sealed class StatusViewModel : INotifyPropertyChanged
{
    private readonly AppController controller;
    private readonly IHookMaintenanceService? hookMaintenanceService;
    private readonly Func<CancellationToken, Task<int>>? refreshTodayCountAsync;
    private readonly SynchronizationContext? synchronizationContext;
    private string selectedProject;
    private string? errorMessage;
    private bool isEnabled;
    private string hookHealthText;
    private bool? hookOperational;
    private StatusPresentation presentation;
    private int todayApprovalCount;

    public StatusViewModel(
        AppController controller,
        IHookMaintenanceService? hookMaintenanceService = null,
        Func<CancellationToken, Task<int>>? refreshTodayCountAsync = null)
    {
        this.controller = controller ?? throw new ArgumentNullException(nameof(controller));
        this.hookMaintenanceService = hookMaintenanceService;
        this.refreshTodayCountAsync = refreshTodayCountAsync;
        synchronizationContext = SynchronizationContext.Current;
        selectedProject = controller.SelectedProject;
        isEnabled = controller.IsEnabled;
        hookOperational = hookMaintenanceService?.Current.IsOperational;
        presentation = StatusPresentationMapper.Map(
            controller.IsEnabled,
            controller.ErrorCode,
            hookOperational);
        hookHealthText = GetHookHealthText(hookOperational);
        errorMessage = GetErrorMessage(controller.ErrorCode);
        ToggleApprovalCommand = new AsyncRelayCommand(ToggleApprovalAsync, HandleCommandErrorAsync);
        ChooseProjectCommand = new RelayCommand(_ => ChooseProject(), _ => !IsEnabled);
        InstallHookCommand = new AsyncRelayCommand(
            () => RunHookOperationAsync(service => service.InstallAsync(CancellationToken.None)),
            HandleCommandErrorAsync,
            () => this.hookMaintenanceService is not null);
        DoctorCommand = new AsyncRelayCommand(
            () => RunHookOperationAsync(service => service.DoctorAsync(CancellationToken.None)),
            HandleCommandErrorAsync,
            () => this.hookMaintenanceService is not null);
        RefreshCommand = new AsyncRelayCommand(
            () => RefreshStatusAsync(CancellationToken.None),
            HandleCommandErrorAsync);
        controller.StateChanged += OnControllerStateChanged;
        if (hookMaintenanceService is not null)
        {
            hookMaintenanceService.HealthChanged += OnHookHealthChanged;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsEnabled
    {
        get => isEnabled;
        private set
        {
            if (SetProperty(ref isEnabled, value))
            {
                OnPropertyChanged(nameof(ToggleButtonText));
                OnPropertyChanged(nameof(HeartbeatText));
                ChooseProjectCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public StatusPresentation Presentation => presentation;
    public string StatusText => Presentation.Text;
    public string StatusIconGlyph => Presentation.IconGlyph;
    public string StatusColor => Presentation.Color;

    public string SelectedProject
    {
        get => selectedProject;
        set => SetProperty(ref selectedProject, value ?? string.Empty);
    }

    public string? ErrorMessage
    {
        get => errorMessage;
        private set => SetProperty(ref errorMessage, value);
    }

    public string ToggleButtonText => IsEnabled
        ? StringResources.Get("PauseApproval")
        : StringResources.Get("EnableApproval");

    public string HookHealthText
    {
        get => hookHealthText;
        private set => SetProperty(ref hookHealthText, value);
    }

    public string HeartbeatText => IsEnabled
        ? StringResources.Get("HeartbeatActive")
        : StringResources.Get("HeartbeatStopped");

    public string TodayApprovalCountText => string.Format(
        System.Globalization.CultureInfo.CurrentCulture,
        StringResources.Get("TodayApprovalCountFormat"),
        todayApprovalCount);

    public Func<string?>? ChooseProjectPath { get; set; }

    public AsyncRelayCommand ToggleApprovalCommand { get; }
    public RelayCommand ChooseProjectCommand { get; }
    public AsyncRelayCommand InstallHookCommand { get; }
    public AsyncRelayCommand DoctorCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }

    public void SetHookHealth(bool operational)
    {
        hookOperational = operational;
        HookHealthText = GetHookHealthText(operational);
        UpdatePresentation(controller.ErrorCode);
    }

    public void SetTodayApprovalCount(int count)
    {
        todayApprovalCount = Math.Max(0, count);
        OnPropertyChanged(nameof(TodayApprovalCountText));
    }

    public async Task RefreshTodayCountAsync(CancellationToken cancellationToken)
    {
        if (refreshTodayCountAsync is null)
        {
            return;
        }

        int count = await refreshTodayCountAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        SetTodayApprovalCount(count);
    }

    private void ChooseProject()
    {
        if (controller.IsEnabled)
        {
            ErrorMessage = StringResources.Get("PauseBeforeChangingProject");
            return;
        }

        string? selected = ChooseProjectPath?.Invoke();
        if (!string.IsNullOrWhiteSpace(selected))
        {
            SelectedProject = selected;
        }
    }

    private async Task ToggleApprovalAsync()
    {
        ErrorMessage = null;
        if (controller.IsEnabled)
        {
            await controller.PauseAsync(CancellationToken.None);
        }
        else
        {
            _ = await controller.EnableAsync(SelectedProject, CancellationToken.None);
        }
    }

    private async Task RunHookOperationAsync(
        Func<IHookMaintenanceService, Task<HookOperationResult>> operation)
    {
        if (hookMaintenanceService is null)
        {
            return;
        }

        ErrorMessage = null;
        HookOperationResult result = await operation(hookMaintenanceService);
        if (result.Outcome is HookOperationOutcome.Unhealthy or HookOperationOutcome.Failed)
        {
            ErrorMessage = StringResources.Get(result.Outcome == HookOperationOutcome.Unhealthy
                ? "ErrorHookNotOperational"
                : "ErrorOperationFailed");
        }
    }

    private async Task RefreshStatusAsync(CancellationToken cancellationToken)
    {
        ErrorMessage = null;
        if (hookMaintenanceService is not null)
        {
            await hookMaintenanceService.RefreshAsync(cancellationToken);
        }

        await RefreshTodayCountAsync(cancellationToken);
    }

    private Task HandleCommandErrorAsync(Exception exception)
    {
        RunOnCapturedContext(() =>
            ErrorMessage = GetErrorMessage(controller.ErrorCode)
                ?? StringResources.Get("ErrorOperationFailed"));
        return Task.CompletedTask;
    }

    private void OnControllerStateChanged(object? sender, EventArgs eventArgs) =>
        RunOnCapturedContext(RefreshFromController);

    private void OnHookHealthChanged(object? sender, HookHealthSnapshot snapshot) =>
        RunOnCapturedContext(() =>
        {
            hookOperational = snapshot.IsOperational;
            HookHealthText = GetHookHealthText(snapshot.IsOperational);
            UpdatePresentation(controller.ErrorCode);
        });

    private void RefreshFromController()
    {
        string? controllerError = controller.ErrorCode;
        IsEnabled = controller.IsEnabled;
        ErrorMessage = GetErrorMessage(controllerError);
        if (controllerError is null or AppController.HeartbeatWriteFailed)
        {
            SelectedProject = controller.SelectedProject;
        }
        UpdatePresentation(controllerError);
    }

    private void RunOnCapturedContext(Action action)
    {
        if (synchronizationContext is not null && synchronizationContext != SynchronizationContext.Current)
        {
            synchronizationContext.Post(static state => ((Action)state!).Invoke(), action);
            return;
        }
        action();
    }

    private void UpdatePresentation(string? currentErrorCode)
    {
        StatusPresentation updated = StatusPresentationMapper.Map(
            controller.IsEnabled,
            currentErrorCode,
            hookOperational);
        if (Equals(presentation, updated))
        {
            return;
        }

        presentation = updated;
        OnPropertyChanged(nameof(Presentation));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusIconGlyph));
        OnPropertyChanged(nameof(StatusColor));
    }

    private static string GetHookHealthText(bool? operational) => StringResources.Get(operational switch
    {
        true => "HookHealthy",
        false => "HookUnhealthy",
        null => "HookHealthUnknown"
    });

    private static string? GetErrorMessage(string? errorCode) => errorCode switch
    {
        null => null,
        AppController.SelectedProjectRequired => StringResources.Get("ErrorSelectedProjectRequired"),
        AppController.SelectedProjectNotFound => StringResources.Get("ErrorSelectedProjectNotFound"),
        AppController.HookNotOperational => StringResources.Get("ErrorHookNotOperational"),
        AppController.HeartbeatWriteFailed => StringResources.Get("ErrorHeartbeatWriteFailed"),
        _ => StringResources.Get("ErrorOperationFailed")
    };

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
}
