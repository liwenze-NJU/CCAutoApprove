using System.ComponentModel;
using CCAutoApprove.App.Commands;
using CCAutoApprove.App.Services;
using CCAutoApprove.App.Tests.Services;
using CCAutoApprove.App.ViewModels;
using CCAutoApprove.Core.Abstractions;
using CCAutoApprove.Core.Models;

namespace CCAutoApprove.App.Tests.ViewModels;

public sealed class StatusViewModelTests
{
    private const string ProjectPath = @"D:\projects\status";

    [Fact]
    public async Task ControllerTransitions_UpdateStatusTextAndObservableProperties()
    {
        await using var environment = await ViewModelEnvironment.CreateAsync();
        var viewModel = new StatusViewModel(environment.Controller);
        var changedProperties = new List<string?>();
        var faultObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.PropertyChanged += (_, args) =>
        {
            changedProperties.Add(args.PropertyName);
            if (args.PropertyName == nameof(StatusViewModel.StatusText) && viewModel.StatusText == "异常")
            {
                faultObserved.TrySetResult();
            }
        };

        Assert.Equal("已暂停", viewModel.StatusText);
        viewModel.SelectedProject = ProjectPath;
        await viewModel.ToggleApprovalCommand.ExecuteAsync();

        Assert.True(viewModel.IsEnabled);
        Assert.Equal("正在自动批准", viewModel.StatusText);
        environment.RuntimeStore.FailNextWrites = 2;
        await environment.Timer.TickAsync();
        await faultObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(viewModel.IsEnabled);
        Assert.Equal("异常", viewModel.StatusText);
        Assert.Contains(nameof(StatusViewModel.SelectedProject), changedProperties);
        Assert.Contains(nameof(StatusViewModel.IsEnabled), changedProperties);
        Assert.Contains(nameof(StatusViewModel.StatusText), changedProperties);
    }

    [Fact]
    public async Task ToggleApprovalCommand_WhenControllerThrows_SetsErrorMessage()
    {
        await using var environment = await ViewModelEnvironment.CreateAsync();
        var viewModel = new StatusViewModel(environment.Controller)
        {
            SelectedProject = ProjectPath
        };
        environment.RuntimeStore.FailNextWrites = 1;

        await viewModel.ToggleApprovalCommand.ExecuteAsync();

        Assert.Equal("Simulated runtime-state write failure.", viewModel.ErrorMessage);
        Assert.False(viewModel.IsEnabled);
    }

    [Fact]
    public async Task MainViewModel_ExposesItsThreePageViewModels()
    {
        await using var environment = await ViewModelEnvironment.CreateAsync();
        var status = new StatusViewModel(environment.Controller);
        var records = new RecordsViewModel();
        var settings = new SettingsViewModel();

        var main = new MainViewModel(status, records, settings);

        Assert.Same(status, main.Status);
        Assert.Same(records, main.Records);
        Assert.Same(settings, main.Settings);
    }

    private sealed class ViewModelEnvironment(
        AppController controller,
        HeartbeatService heartbeat,
        RecordingRuntimeStateStore runtimeStore,
        FakeHeartbeatTimer timer) : IAsyncDisposable
    {
        public AppController Controller { get; } = controller;
        public RecordingRuntimeStateStore RuntimeStore { get; } = runtimeStore;
        public FakeHeartbeatTimer Timer { get; } = timer;

        public static async Task<ViewModelEnvironment> CreateAsync()
        {
            var runtimeStore = new RecordingRuntimeStateStore();
            var timer = new FakeHeartbeatTimer();
            var heartbeat = new HeartbeatService(runtimeStore,
                new FakeClock(new DateTimeOffset(2026, 8, 17, 3, 0, 0, TimeSpan.Zero)),
                new FakeCurrentProcessInfo(8112,
                    new DateTimeOffset(2026, 8, 17, 2, 59, 0, TimeSpan.Zero)),
                new ExistingDirectoryService(), timer);
            var controller = new AppController(new StatusSettingsStore(),
                new ExistingDirectoryService(), new OperationalHookHealthService(), heartbeat);
            await controller.InitializeAsync(CancellationToken.None);
            return new ViewModelEnvironment(controller, heartbeat, runtimeStore, timer);
        }

        public ValueTask DisposeAsync() => heartbeat.DisposeAsync();
    }

    private sealed class StatusSettingsStore : ISettingsStore
    {
        public Task<PersistentSettings> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new PersistentSettings());

        public Task SaveAsync(PersistentSettings settings, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class ExistingDirectoryService : IDirectoryService
    {
        public bool Exists(string path) => true;
    }

    private sealed class OperationalHookHealthService : IHookHealthService
    {
        public Task<bool> IsOperationalAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    }
}

public sealed class CommandTests
{
    [Fact]
    public async Task AsyncRelayCommand_WhileExecuting_DisablesThenReenablesCanExecute()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canExecuteChanges = new List<bool>();
        var command = new AsyncRelayCommand(async () =>
        {
            started.SetResult();
            await release.Task;
        }, _ => Task.CompletedTask);
        command.CanExecuteChanged += (_, _) => canExecuteChanges.Add(command.CanExecute(null));

        Task execution = command.ExecuteAsync();
        await started.Task;

        Assert.False(command.CanExecute(null));
        release.SetResult();
        await execution;
        Assert.True(command.CanExecute(null));
        Assert.Equal([false, true], canExecuteChanges);
    }

    [Fact]
    public async Task AsyncRelayCommand_WhenExecutionThrows_AwaitsSuppliedErrorHandler()
    {
        Exception? handled = null;
        var command = new AsyncRelayCommand(
            () => Task.FromException(new InvalidOperationException("command failed")),
            exception =>
            {
                handled = exception;
                return Task.CompletedTask;
            });

        await command.ExecuteAsync();

        Assert.Equal("command failed", handled?.Message);
    }

    [Fact]
    public void RelayCommand_UsesPredicateAndExecutesAction()
    {
        object? received = null;
        var command = new RelayCommand(parameter => received = parameter, parameter => parameter is int value && value > 0);

        Assert.False(command.CanExecute(0));
        Assert.True(command.CanExecute(3));
        command.Execute(3);
        Assert.Equal(3, received);
    }
}
