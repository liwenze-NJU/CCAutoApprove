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

    public StatusViewModel(AppController controller)
    {
        this.controller = controller ?? throw new ArgumentNullException(nameof(controller));
        synchronizationContext = SynchronizationContext.Current;
        selectedProject = controller.SelectedProject;
        isEnabled = controller.IsEnabled;
        statusText = GetStatusText(controller.IsEnabled, controller.ErrorCode);
        errorMessage = controller.ErrorCode;
        ToggleApprovalCommand = new AsyncRelayCommand(ToggleApprovalAsync, HandleCommandErrorAsync);
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

    public string ToggleButtonText => IsEnabled ? "暂停自动批准" : "开启自动批准";

    public AsyncRelayCommand ToggleApprovalCommand { get; }

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
        RunOnCapturedContext(() => ErrorMessage = exception.Message);
        return Task.CompletedTask;
    }

    private void OnControllerStateChanged(object? sender, EventArgs eventArgs) =>
        RunOnCapturedContext(RefreshFromController);

    private void RefreshFromController()
    {
        string? controllerError = controller.ErrorCode;
        IsEnabled = controller.IsEnabled;
        StatusText = GetStatusText(controller.IsEnabled, controllerError);
        ErrorMessage = controllerError;
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
        currentErrorCode == AppController.HeartbeatWriteFailed
            ? "异常"
            : enabled
                ? "正在自动批准"
                : "已暂停";

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
