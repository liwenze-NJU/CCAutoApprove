using System.ComponentModel;
using System.Runtime.CompilerServices;
using CCAutoApprove.App.Commands;
using CCAutoApprove.App.Services;

namespace CCAutoApprove.App.ViewModels;

public sealed class StatusViewModel : INotifyPropertyChanged
{
    private readonly AppController controller;
    private readonly SynchronizationContext? synchronizationContext;
    private string selectedProject;
    private string statusText;
    private string? errorMessage;
    private bool isEnabled;
    private string hookHealthText;
    private int todayApprovalCount;

    public StatusViewModel(AppController controller)
    {
        this.controller = controller ?? throw new ArgumentNullException(nameof(controller));
        synchronizationContext = SynchronizationContext.Current;
        selectedProject = controller.SelectedProject;
        isEnabled = controller.IsEnabled;
        statusText = GetStatusText(controller.IsEnabled, controller.ErrorCode);
        hookHealthText = StringResources.Get("HookHealthUnknown");
        errorMessage = GetErrorMessage(controller.ErrorCode);
        ToggleApprovalCommand = new AsyncRelayCommand(ToggleApprovalAsync, HandleCommandErrorAsync);
        ChooseProjectCommand = new RelayCommand(_ => ChooseProject(), _ => !IsEnabled);
        controller.StateChanged += OnControllerStateChanged;
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

    public string StatusText
    {
        get => statusText;
        private set => SetProperty(ref statusText, value);
    }

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

    public void SetHookHealth(bool operational) =>
        HookHealthText = StringResources.Get(operational ? "HookHealthy" : "HookUnhealthy");

    public void SetTodayApprovalCount(int count)
    {
        todayApprovalCount = Math.Max(0, count);
        OnPropertyChanged(nameof(TodayApprovalCountText));
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

    private Task HandleCommandErrorAsync(Exception exception)
    {
        RunOnCapturedContext(() =>
            ErrorMessage = GetErrorMessage(controller.ErrorCode)
                ?? StringResources.Get("ErrorOperationFailed"));
        return Task.CompletedTask;
    }

    private void OnControllerStateChanged(object? sender, EventArgs eventArgs) =>
        RunOnCapturedContext(RefreshFromController);

    private void RefreshFromController()
    {
        string? controllerError = controller.ErrorCode;
        IsEnabled = controller.IsEnabled;
        StatusText = GetStatusText(controller.IsEnabled, controllerError);
        ErrorMessage = GetErrorMessage(controllerError);
        if (controllerError is null or AppController.HeartbeatWriteFailed)
        {
            SelectedProject = controller.SelectedProject;
        }
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

    private static string GetStatusText(bool enabled, string? currentErrorCode) =>
        currentErrorCode is AppController.HeartbeatWriteFailed or AppController.SelectedProjectNotFound
            ? StringResources.Get("StatusError")
            : enabled
                ? StringResources.Get("StatusRunning")
                : StringResources.Get("StatusPaused");

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
