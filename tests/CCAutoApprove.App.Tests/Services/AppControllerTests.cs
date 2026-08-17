using CCAutoApprove.App.Services;
using CCAutoApprove.Core.Abstractions;
using CCAutoApprove.Core.Models;

namespace CCAutoApprove.App.Tests.Services;

public sealed class AppControllerTests
{
    private static readonly DateTimeOffset ProcessStartUtc = new(2026, 8, 17, 1, 2, 3, TimeSpan.Zero);
    private const string ExistingProject = @"D:\projects\existing";

    [Fact]
    public async Task InitializeAsync_WritesNewDisabledStateForCurrentProcess()
    {
        await using var environment = CreateEnvironment(selectedProject: ExistingProject);

        await environment.Controller.InitializeAsync(CancellationToken.None);

        RuntimeState state = Assert.Single(environment.RuntimeStore.SuccessfulStates);
        Assert.False(state.Enabled);
        Assert.Equal(7123, state.ProcessId);
        Assert.Equal(ProcessStartUtc, state.ProcessStartUtc);
        Assert.NotEqual(Guid.Empty, state.InstanceId);
        Assert.Equal(ExistingProject, state.SelectedProject);
        Assert.False(environment.Controller.IsEnabled);
    }

    [Fact]
    public async Task InitializeAsync_CalledTwice_WritesStartupStateOnce()
    {
        await using var environment = CreateEnvironment(selectedProject: ExistingProject);

        await environment.Controller.InitializeAsync(CancellationToken.None);
        Guid instanceId = environment.Heartbeat.InstanceId;
        await environment.Controller.InitializeAsync(CancellationToken.None);

        Assert.Single(environment.RuntimeStore.SuccessfulStates);
        Assert.Equal(instanceId, environment.Heartbeat.InstanceId);
    }

    [Theory]
    [InlineData(null, true, true, "SelectedProjectRequired")]
    [InlineData("", true, true, "SelectedProjectRequired")]
    [InlineData(@"D:\projects\missing", false, true, "SelectedProjectNotFound")]
    [InlineData(ExistingProject, true, false, "HookNotOperational")]
    public async Task EnableAsync_WhenPrerequisiteFails_RemainsDisabled(
        string? selectedProject,
        bool directoryExists,
        bool hookOperational,
        string expectedErrorCode)
    {
        await using var environment = CreateEnvironment(directoryExists: directoryExists, hookOperational: hookOperational);
        await environment.Controller.InitializeAsync(CancellationToken.None);

        bool enabled = await environment.Controller.EnableAsync(selectedProject, CancellationToken.None);

        Assert.False(enabled);
        Assert.False(environment.Controller.IsEnabled);
        Assert.Equal(expectedErrorCode, environment.Controller.ErrorCode);
        Assert.DoesNotContain(environment.RuntimeStore.SuccessfulStates.Skip(1), state => state.Enabled);
    }

    [Fact]
    public async Task EnableAsync_WhenValid_FollowsSafetyOrderAndStartsHeartbeat()
    {
        var order = new List<string>();
        await using var environment = CreateEnvironment(order: order);
        await environment.Controller.InitializeAsync(CancellationToken.None);
        order.Clear();

        bool enabled = await environment.Controller.EnableAsync(ExistingProject, CancellationToken.None);

        Assert.True(enabled);
        Assert.Equal(["directory", "health", "settings", "runtime:True", "timer"], order);
        Assert.True(environment.Controller.IsEnabled);
        Assert.True(environment.Heartbeat.IsRunning);
        Assert.True(environment.RuntimeStore.SuccessfulStates[^1].Enabled);
        Assert.Equal(ExistingProject, environment.SettingsStore.Settings.SelectedProject);
    }

    [Fact]
    public async Task PauseAsync_WhenEnabled_WritesDisabledAndStopsHeartbeat()
    {
        await using var environment = CreateEnvironment();
        await environment.Controller.InitializeAsync(CancellationToken.None);
        await environment.Controller.EnableAsync(ExistingProject, CancellationToken.None);

        await environment.Controller.PauseAsync(CancellationToken.None);

        Assert.False(environment.Controller.IsEnabled);
        Assert.False(environment.Heartbeat.IsRunning);
        Assert.False(environment.RuntimeStore.SuccessfulStates[^1].Enabled);
    }

    [Fact]
    public async Task PauseAsync_CalledTwice_DoesNotWriteASecondDisabledState()
    {
        await using var environment = CreateEnvironment();
        await environment.Controller.InitializeAsync(CancellationToken.None);
        await environment.Controller.EnableAsync(ExistingProject, CancellationToken.None);
        await environment.Controller.PauseAsync(CancellationToken.None);
        int writesAfterFirstPause = environment.RuntimeStore.SuccessfulStates.Count;

        await environment.Controller.PauseAsync(CancellationToken.None);

        Assert.Equal(writesAfterFirstPause, environment.RuntimeStore.SuccessfulStates.Count);
    }

    [Fact]
    public async Task PauseAsync_WhenDisabledWriteFails_StillDisablesMemoryAndStopsHeartbeat()
    {
        await using var environment = CreateEnvironment();
        await environment.Controller.InitializeAsync(CancellationToken.None);
        await environment.Controller.EnableAsync(ExistingProject, CancellationToken.None);
        environment.RuntimeStore.FailNextWrites = 1;

        await Assert.ThrowsAsync<IOException>(() =>
            environment.Controller.PauseAsync(CancellationToken.None));

        Assert.False(environment.Controller.IsEnabled);
        Assert.False(environment.Heartbeat.IsEnabled);
        Assert.False(environment.Heartbeat.IsRunning);
    }

    [Fact]
    public async Task HeartbeatFault_TransitionsControllerToDisabledErrorState()
    {
        await using var environment = CreateEnvironment();
        await environment.Controller.InitializeAsync(CancellationToken.None);
        await environment.Controller.EnableAsync(ExistingProject, CancellationToken.None);
        environment.RuntimeStore.FailNextWrites = 2;
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        environment.Controller.StateChanged += (_, _) =>
        {
            if (environment.Controller.ErrorCode == "HeartbeatWriteFailed")
            {
                changed.TrySetResult();
            }
        };

        await environment.Timer.TickAsync();
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(environment.Controller.IsEnabled);
        Assert.Equal("HeartbeatWriteFailed", environment.Controller.ErrorCode);
        Assert.False(environment.RuntimeStore.SuccessfulStates[^1].Enabled);
    }

    [Fact]
    public async Task ShutdownAsync_CancelsLoopAndWritesDisabledBeforeReturning()
    {
        await using var environment = CreateEnvironment();
        await environment.Controller.InitializeAsync(CancellationToken.None);
        await environment.Controller.EnableAsync(ExistingProject, CancellationToken.None);

        await environment.Controller.ShutdownAsync(CancellationToken.None);
        int saveCountAtReturn = environment.RuntimeStore.SuccessfulStates.Count;
        await environment.Timer.TickAsync();

        Assert.False(environment.Heartbeat.IsRunning);
        Assert.False(environment.RuntimeStore.SuccessfulStates[^1].Enabled);
        Assert.Equal(saveCountAtReturn, environment.RuntimeStore.SuccessfulStates.Count);
    }

    [Fact]
    public async Task ShutdownAsync_CalledTwice_DoesNotWriteASecondDisabledState()
    {
        await using var environment = CreateEnvironment();
        await environment.Controller.InitializeAsync(CancellationToken.None);
        await environment.Controller.EnableAsync(ExistingProject, CancellationToken.None);
        await environment.Controller.ShutdownAsync(CancellationToken.None);
        int writesAfterFirstShutdown = environment.RuntimeStore.SuccessfulStates.Count;

        await environment.Controller.ShutdownAsync(CancellationToken.None);

        Assert.Equal(writesAfterFirstShutdown, environment.RuntimeStore.SuccessfulStates.Count);
    }

    [Fact]
    public async Task ShutdownAsync_WhenDisabledWriteFails_StillDisablesMemoryAndStopsHeartbeat()
    {
        await using var environment = CreateEnvironment();
        await environment.Controller.InitializeAsync(CancellationToken.None);
        await environment.Controller.EnableAsync(ExistingProject, CancellationToken.None);
        environment.RuntimeStore.FailNextWrites = 1;

        await Assert.ThrowsAsync<IOException>(() =>
            environment.Controller.ShutdownAsync(CancellationToken.None));

        Assert.False(environment.Controller.IsEnabled);
        Assert.False(environment.Heartbeat.IsEnabled);
        Assert.False(environment.Heartbeat.IsRunning);
    }

    private static TestEnvironment CreateEnvironment(
        string? selectedProject = null,
        bool directoryExists = true,
        bool hookOperational = true,
        List<string>? order = null)
    {
        var runtimeStore = new RecordingRuntimeStateStore
        {
            OnAttempt = state => order?.Add($"runtime:{state.Enabled}")
        };
        var settingsStore = new FakeSettingsStore(new PersistentSettings(SelectedProject: selectedProject),
            () => order?.Add("settings"));
        var timer = new OrderedFakeHeartbeatTimer(() => order?.Add("timer"));
        var heartbeat = new HeartbeatService(runtimeStore,
            new FakeClock(new DateTimeOffset(2026, 8, 17, 2, 0, 0, TimeSpan.Zero)),
            new FakeCurrentProcessInfo(7123, ProcessStartUtc), timer);
        var controller = new AppController(settingsStore,
            new FakeDirectoryService(directoryExists, () => order?.Add("directory")),
            new FakeHookHealthService(hookOperational, () => order?.Add("health")),
            heartbeat);
        return new TestEnvironment(controller, heartbeat, runtimeStore, settingsStore, timer);
    }

    private sealed record TestEnvironment(
        AppController Controller,
        HeartbeatService Heartbeat,
        RecordingRuntimeStateStore RuntimeStore,
        FakeSettingsStore SettingsStore,
        OrderedFakeHeartbeatTimer Timer) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Heartbeat.DisposeAsync();
    }

    private sealed class OrderedFakeHeartbeatTimer(Action onWait) : IHeartbeatTimer
    {
        private readonly FakeHeartbeatTimer inner = new();

        public ValueTask TickAsync() => inner.TickAsync();

        public ValueTask<bool> WaitForNextTickAsync(CancellationToken cancellationToken)
        {
            onWait();
            return inner.WaitForNextTickAsync(cancellationToken);
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class FakeSettingsStore(PersistentSettings settings, Action onSave) : ISettingsStore
    {
        public PersistentSettings Settings { get; private set; } = settings;

        public Task<PersistentSettings> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(Settings);

        public Task SaveAsync(PersistentSettings value, CancellationToken cancellationToken)
        {
            onSave();
            Settings = value;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeDirectoryService(bool exists, Action onExists) : IDirectoryService
    {
        public bool Exists(string path)
        {
            onExists();
            return exists;
        }
    }

    private sealed class FakeHookHealthService(bool operational, Action onCheck) : IHookHealthService
    {
        public Task<bool> IsOperationalAsync(CancellationToken cancellationToken)
        {
            onCheck();
            return Task.FromResult(operational);
        }
    }
}
