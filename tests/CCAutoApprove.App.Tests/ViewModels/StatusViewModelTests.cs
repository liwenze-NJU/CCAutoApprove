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
    [Fact]
    public void AsyncRelayCommand_LegacyOverload_NullExecuteThrowsDuringConstruction()
    {
        Assert.Throws<ArgumentNullException>(() => new AsyncRelayCommand(
            (Func<Task>)null!,
            _ => Task.CompletedTask));
    }

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

        Assert.Equal("心跳写入失败，自动批准已暂停。", viewModel.ErrorMessage);
        Assert.DoesNotContain(AppController.HeartbeatWriteFailed, viewModel.ErrorMessage);
        Assert.False(viewModel.IsEnabled);
    }

    [Theory]
    [InlineData("", true, true, "请先选择项目。")]
    [InlineData(@"D:\projects\missing", false, true, "所选项目不存在。")]
    [InlineData(ProjectPath, true, false, "Hook 状态异常，请先运行检查。")]
    public async Task ToggleApprovalCommand_WhenPrerequisiteFails_ShowsLocalizedDiagnostic(
        string project,
        bool directoryExists,
        bool hookOperational,
        string expectedMessage)
    {
        await using var environment = await ViewModelEnvironment.CreateAsync(
            directoryExists: directoryExists,
            hookOperational: hookOperational);
        var viewModel = new StatusViewModel(environment.Controller)
        {
            SelectedProject = project
        };

        await viewModel.ToggleApprovalCommand.ExecuteAsync();

        Assert.Equal(expectedMessage, viewModel.ErrorMessage);
        Assert.DoesNotContain(environment.Controller.ErrorCode!, viewModel.ErrorMessage);
    }

    [Fact]
    public async Task ChooseProjectCommand_WhileEnabled_RejectsReplacementWithoutChangingRunningProject()
    {
        const string replacementProject = @"D:\projects\replacement";
        await using var environment = await ViewModelEnvironment.CreateAsync(ProjectPath);
        var viewModel = new StatusViewModel(environment.Controller)
        {
            ChooseProjectPath = () => replacementProject
        };
        await viewModel.ToggleApprovalCommand.ExecuteAsync();

        Assert.False(viewModel.ChooseProjectCommand.CanExecute(null));
        viewModel.ChooseProjectCommand.Execute(null);

        Assert.Equal(ProjectPath, viewModel.SelectedProject);
        Assert.Equal(ProjectPath, environment.Controller.SelectedProject);
        Assert.Equal(ProjectPath, environment.SettingsStore.Settings.SelectedProject);
        Assert.Equal(ProjectPath, environment.RuntimeStore.SuccessfulStates[^1].SelectedProject);
        Assert.Equal("请先暂停自动批准，再更改项目。", viewModel.ErrorMessage);
    }

    [Fact]
    public async Task HookHealthFaultAndRecovery_UpdateOneSharedStatusAndTrayPresentation()
    {
        await using var environment = await ViewModelEnvironment.CreateAsync();
        var maintenance = new PublishingHookMaintenanceService(initialOperational: true);
        var viewModel = new StatusViewModel(environment.Controller, maintenance)
        {
            SelectedProject = ProjectPath
        };

        Assert.Equal(StatusVisualState.Paused, viewModel.Presentation.State);
        await viewModel.ToggleApprovalCommand.ExecuteAsync();
        Assert.Equal(StatusVisualState.Running, viewModel.Presentation.State);

        maintenance.Publish(operational: false);

        Assert.Equal(StatusVisualState.Error, viewModel.Presentation.State);
        Assert.Equal(TrayIconKind.Error, viewModel.Presentation.TrayIcon);
        Assert.Equal(StringResources.Get("HookUnhealthy"), viewModel.HookHealthText);

        maintenance.Publish(operational: true);

        Assert.Equal(StatusVisualState.Running, viewModel.Presentation.State);
        Assert.Equal(TrayIconKind.Running, viewModel.Presentation.TrayIcon);
        Assert.Equal(StringResources.Get("HookHealthy"), viewModel.HookHealthText);
    }

    [Fact]
    public async Task HookHealthRecovery_AfterHookEnableFailure_StopsPresentingTheResolvedControllerError()
    {
        await using var environment = await ViewModelEnvironment.CreateAsync(hookOperational: false);
        var maintenance = new PublishingHookMaintenanceService(initialOperational: false);
        var viewModel = new StatusViewModel(environment.Controller, maintenance)
        {
            SelectedProject = ProjectPath
        };

        await viewModel.ToggleApprovalCommand.ExecuteAsync();

        Assert.Equal(AppController.HookNotOperational, environment.Controller.ErrorCode);
        Assert.Equal(StatusVisualState.Error, viewModel.Presentation.State);
        Assert.Equal("异常", viewModel.StatusText);
        Assert.Equal("!", viewModel.StatusIconGlyph);
        Assert.Equal(TrayIconKind.Error, viewModel.Presentation.TrayIcon);

        maintenance.Publish(operational: true);

        Assert.Equal(AppController.HookNotOperational, environment.Controller.ErrorCode);
        Assert.Equal(StatusVisualState.Paused, viewModel.Presentation.State);
        Assert.Equal("已暂停", viewModel.StatusText);
        Assert.Equal("Ⅱ", viewModel.StatusIconGlyph);
        Assert.Equal(TrayIconKind.Paused, viewModel.Presentation.TrayIcon);
        Assert.Null(viewModel.ErrorMessage);
    }

    [Fact]
    public async Task HookHealthyCache_BeforeHookEnableFailure_DoesNotHideTheNewControllerError()
    {
        await using var environment = await ViewModelEnvironment.CreateAsync(hookOperational: false);
        var maintenance = new PublishingHookMaintenanceService(initialOperational: true);
        var viewModel = new StatusViewModel(environment.Controller, maintenance)
        {
            SelectedProject = ProjectPath
        };

        Assert.Equal(StringResources.Get("HookHealthy"), viewModel.HookHealthText);
        Assert.Equal(StatusVisualState.Paused, viewModel.Presentation.State);

        await viewModel.ToggleApprovalCommand.ExecuteAsync();

        Assert.Equal(AppController.HookNotOperational, environment.Controller.ErrorCode);
        Assert.Equal(StatusVisualState.Error, viewModel.Presentation.State);
        Assert.Equal("异常", viewModel.StatusText);
        Assert.Equal("!", viewModel.StatusIconGlyph);
        Assert.Equal(TrayIconKind.Error, viewModel.Presentation.TrayIcon);
        Assert.Equal(StringResources.Get("ErrorHookNotOperational"), viewModel.ErrorMessage);
    }

    [Fact]
    public async Task HookHealthRecovery_DoesNotClearAnUnrelatedHeartbeatError()
    {
        await using var environment = await ViewModelEnvironment.CreateAsync();
        var maintenance = new PublishingHookMaintenanceService(initialOperational: true);
        var viewModel = new StatusViewModel(environment.Controller, maintenance)
        {
            SelectedProject = ProjectPath
        };
        await viewModel.ToggleApprovalCommand.ExecuteAsync();
        var heartbeatFailed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        environment.Controller.StateChanged += (_, _) =>
        {
            if (environment.Controller.ErrorCode == AppController.HeartbeatWriteFailed)
            {
                heartbeatFailed.TrySetResult();
            }
        };
        environment.RuntimeStore.FailNextWrites = 2;

        await environment.Timer.TickAsync();
        await heartbeatFailed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        maintenance.Publish(operational: true);

        Assert.Equal(AppController.HeartbeatWriteFailed, environment.Controller.ErrorCode);
        Assert.Equal(StatusVisualState.Error, viewModel.Presentation.State);
        Assert.Equal(TrayIconKind.Error, viewModel.Presentation.TrayIcon);
        Assert.Equal(
            StringResources.Get("ErrorHeartbeatWriteFailed"),
            viewModel.ErrorMessage);
    }

    [Fact]
    public async Task StatusInstallAndDoctorCommands_UseSharedMaintenanceService()
    {
        await using var environment = await ViewModelEnvironment.CreateAsync();
        var maintenance = new PublishingHookMaintenanceService(initialOperational: false);
        var viewModel = new StatusViewModel(environment.Controller, maintenance);

        await viewModel.InstallHookCommand.ExecuteAsync();
        await viewModel.DoctorCommand.ExecuteAsync();

        Assert.Equal(1, maintenance.InstallCalls);
        Assert.Equal(1, maintenance.DoctorCalls);
        Assert.Equal(StatusVisualState.Paused, viewModel.Presentation.State);
    }

    [Fact]
    public async Task RefreshCommand_RefreshesHookHealthAndTodayApprovalCountTogether()
    {
        await using var environment = await ViewModelEnvironment.CreateAsync();
        var maintenance = new PublishingHookMaintenanceService(initialOperational: false);
        int countCalls = 0;
        var viewModel = new StatusViewModel(
            environment.Controller,
            maintenance,
            _ =>
            {
                countCalls++;
                return Task.FromResult(17);
            });

        await viewModel.RefreshCommand.ExecuteAsync();

        Assert.Equal(1, maintenance.RefreshCalls);
        Assert.Equal(1, countCalls);
        Assert.Equal(
            string.Format(StringResources.Get("TodayApprovalCountFormat"), 17),
            viewModel.TodayApprovalCountText);
    }

    [Fact]
    public async Task MainViewModel_ExposesItsThreePageViewModels()
    {
        await using var environment = await ViewModelEnvironment.CreateAsync();
        var status = new StatusViewModel(environment.Controller);
        var records = new RecordsViewModel();
        var settings = new SettingsViewModel(
            new StatusSettingsStore(new PersistentSettings()),
            new TestAuditLog(),
            () => Task.FromResult(false),
            confirmEnableDetailedAsync: () => Task.FromResult(true));

        var main = new MainViewModel(status, records, settings);

        Assert.Same(status, main.Status);
        Assert.Same(records, main.Records);
        Assert.Same(settings, main.Settings);

        await settings.ChangeAuditDetailLevelAsync(AuditDetailLevel.Detailed);

        Assert.Equal(AuditDetailLevel.Detailed, records.AuditDetailLevel);

        await settings.ChangeAuditDetailLevelAsync(AuditDetailLevel.PrivacySafe);

        Assert.Equal(AuditDetailLevel.PrivacySafe, records.AuditDetailLevel);
    }

    private sealed class TestAuditLog : IAuditLog
    {
        public Task WriteAsync(
            ApprovalRequest request,
            ApprovalDecision decision,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<AuditRecord>> ReadRecentAsync(
            int maximumCount,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AuditRecord>>([]);

        public Task<int> CountAllowedAsync(
            DateTimeOffset startUtcInclusive,
            DateTimeOffset endUtcExclusive,
            CancellationToken cancellationToken) => Task.FromResult(0);

        public Task ClearAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeleteExpiredAsync(int retentionDays, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class ViewModelEnvironment(
        AppController controller,
        HeartbeatService heartbeat,
        RecordingRuntimeStateStore runtimeStore,
        FakeHeartbeatTimer timer,
        StatusSettingsStore settingsStore) : IAsyncDisposable
    {
        public AppController Controller { get; } = controller;
        public RecordingRuntimeStateStore RuntimeStore { get; } = runtimeStore;
        public FakeHeartbeatTimer Timer { get; } = timer;
        public StatusSettingsStore SettingsStore { get; } = settingsStore;

        public static async Task<ViewModelEnvironment> CreateAsync(
            string? selectedProject = null,
            bool directoryExists = true,
            bool hookOperational = true)
        {
            var runtimeStore = new RecordingRuntimeStateStore();
            var timer = new FakeHeartbeatTimer();
            var heartbeat = new HeartbeatService(runtimeStore,
                new FakeClock(new DateTimeOffset(2026, 8, 17, 3, 0, 0, TimeSpan.Zero)),
                new FakeCurrentProcessInfo(8112,
                    new DateTimeOffset(2026, 8, 17, 2, 59, 0, TimeSpan.Zero)),
                new ConfigurableDirectoryService(directoryExists), timer);
            var settingsStore = new StatusSettingsStore(new PersistentSettings(SelectedProject: selectedProject));
            var controller = new AppController(settingsStore,
                new ConfigurableDirectoryService(directoryExists),
                new ConfigurableHookHealthService(hookOperational),
                heartbeat);
            await controller.InitializeAsync(CancellationToken.None);
            return new ViewModelEnvironment(controller, heartbeat, runtimeStore, timer, settingsStore);
        }

        public ValueTask DisposeAsync() => heartbeat.DisposeAsync();
    }

    public sealed class StatusSettingsStore(PersistentSettings settings) : ISettingsStore
    {
        public PersistentSettings Settings { get; private set; } = settings;

        public Task<PersistentSettings> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Settings);

        public Task SaveAsync(PersistentSettings settings, CancellationToken cancellationToken)
        {
            Settings = settings;
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

    private sealed class ConfigurableDirectoryService(bool exists) : IDirectoryService
    {
        public bool Exists(string path) => exists;
    }

    private sealed class ConfigurableHookHealthService(bool operational) : IHookHealthService
    {
        public Task<bool> IsOperationalAsync(CancellationToken cancellationToken) => Task.FromResult(operational);
    }

    private sealed class PublishingHookMaintenanceService(bool initialOperational)
        : IHookMaintenanceService
    {
        public event EventHandler<HookHealthSnapshot>? HealthChanged;
        public HookHealthSnapshot Current { get; private set; } = new(initialOperational, []);
        public int InstallCalls { get; private set; }
        public int DoctorCalls { get; private set; }
        public int RefreshCalls { get; private set; }

        public Task<HookOperationResult> InstallAsync(CancellationToken cancellationToken)
        {
            InstallCalls++;
            Publish(true);
            return Task.FromResult(new HookOperationResult(HookOperationOutcome.Installed, true, []));
        }

        public Task<HookOperationResult> DoctorAsync(CancellationToken cancellationToken)
        {
            DoctorCalls++;
            Publish(true);
            return Task.FromResult(new HookOperationResult(HookOperationOutcome.Healthy, true, []));
        }

        public Task<HookOperationResult> UninstallAsync(CancellationToken cancellationToken)
        {
            Publish(false);
            return Task.FromResult(new HookOperationResult(HookOperationOutcome.Uninstalled, false, []));
        }

        public Task<HookHealthSnapshot> RefreshAsync(CancellationToken cancellationToken)
        {
            RefreshCalls++;
            return Task.FromResult(Current);
        }

        public void Publish(bool operational)
        {
            Current = new HookHealthSnapshot(operational, []);
            HealthChanged?.Invoke(this, Current);
        }
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
