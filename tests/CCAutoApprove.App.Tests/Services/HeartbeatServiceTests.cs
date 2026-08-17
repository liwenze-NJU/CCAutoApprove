using System.Threading.Channels;
using CCAutoApprove.App.Services;
using CCAutoApprove.Core.Abstractions;
using CCAutoApprove.Core.Models;

namespace CCAutoApprove.App.Tests.Services;

public sealed class HeartbeatServiceTests
{
    private static readonly DateTimeOffset ProcessStartUtc = new(2026, 8, 17, 1, 2, 3, TimeSpan.Zero);
    private const string ProjectPath = @"D:\projects\sample";

    [Fact]
    public async Task Tick_WritesFreshHeartbeatWithoutOverlappingAnotherTickWrite()
    {
        var store = new RecordingRuntimeStateStore();
        var timer = new FakeHeartbeatTimer();
        var clock = new FakeClock(new DateTimeOffset(2026, 8, 17, 2, 0, 0, TimeSpan.Zero));
        await using var service = CreateService(store, timer, clock);
        await service.InitializeAsync(ProjectPath, CancellationToken.None);
        await service.EnableAsync(ProjectPath, CancellationToken.None);
        store.BlockWrites = true;
        service.Start();

        await timer.TickAsync();
        await store.WaitForBlockedWriteAsync();
        clock.UtcNow = clock.UtcNow.AddSeconds(2);
        await timer.TickAsync();

        Assert.Equal(1, store.MaxConcurrentWrites);
        store.ReleaseBlockedWrite();
        await store.WaitForBlockedWriteAsync();
        Assert.Equal(1, store.MaxConcurrentWrites);
        store.ReleaseBlockedWrite();
        await store.WaitForSuccessfulSaveCountAsync(4);

        RuntimeState[] heartbeats = store.SuccessfulStates.Skip(2).ToArray();
        Assert.Equal(2, heartbeats.Length);
        Assert.All(heartbeats, state => Assert.True(state.Enabled));
        Assert.Equal(clock.UtcNow, heartbeats[1].HeartbeatUtc);
    }

    [Fact]
    public async Task Tick_WhenFirstTwoWritesFail_RetriesOnceThenFaultsAndAttemptsDisabledWrite()
    {
        var store = new RecordingRuntimeStateStore();
        var timer = new FakeHeartbeatTimer();
        await using var service = CreateService(store, timer);
        await service.InitializeAsync(ProjectPath, CancellationToken.None);
        await service.EnableAsync(ProjectPath, CancellationToken.None);
        store.FailNextWrites = 2;
        var faulted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Faulted += (_, _) => faulted.TrySetResult();
        service.Start();

        await timer.TickAsync();
        await faulted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        RuntimeState[] attempts = store.AttemptedStates.Skip(2).ToArray();
        Assert.Equal(3, attempts.Length);
        Assert.True(attempts[0].Enabled);
        Assert.True(attempts[1].Enabled);
        Assert.False(attempts[2].Enabled);
        Assert.False(service.IsEnabled);
        Assert.False(store.SuccessfulStates[^1].Enabled);
    }

    [Fact]
    public async Task Start_CalledTwice_OwnsOnlyOneLoop()
    {
        var store = new RecordingRuntimeStateStore();
        var timer = new FakeHeartbeatTimer();
        await using var service = CreateService(store, timer);
        await service.InitializeAsync(ProjectPath, CancellationToken.None);
        await service.EnableAsync(ProjectPath, CancellationToken.None);

        service.Start();
        service.Start();

        Assert.True(service.IsRunning);
        Assert.Equal(1, timer.ActiveWaits);
        await timer.TickAsync();
        await store.WaitForSuccessfulSaveCountAsync(3);
        Assert.Equal(3, store.SuccessfulStates.Count);
    }

    private static HeartbeatService CreateService(
        IRuntimeStateStore store,
        IHeartbeatTimer timer,
        FakeClock? clock = null) =>
        new(store, clock ?? new FakeClock(new DateTimeOffset(2026, 8, 17, 2, 0, 0, TimeSpan.Zero)),
            new FakeCurrentProcessInfo(4242, ProcessStartUtc), timer);
}

internal sealed class FakeHeartbeatTimer : IHeartbeatTimer
{
    private readonly Channel<bool> ticks = Channel.CreateUnbounded<bool>();
    private int activeWaits;

    public int ActiveWaits => Volatile.Read(ref activeWaits);

    public ValueTask TickAsync() => ticks.Writer.WriteAsync(true);

    public async ValueTask<bool> WaitForNextTickAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref activeWaits);
        try
        {
            return await ticks.Reader.ReadAsync(cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref activeWaits);
        }
    }

    public ValueTask DisposeAsync()
    {
        ticks.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}

internal sealed class RecordingRuntimeStateStore : IRuntimeStateStore
{
    private readonly object sync = new();
    private readonly Channel<bool> blockedWrites = Channel.CreateUnbounded<bool>();
    private readonly Channel<bool> releases = Channel.CreateUnbounded<bool>();
    private readonly Dictionary<int, TaskCompletionSource> saveMilestones = [];
    private int activeWrites;
    private int maxConcurrentWrites;
    private int failNextWrites;

    public bool BlockWrites { get; set; }

    public int FailNextWrites
    {
        get => Volatile.Read(ref failNextWrites);
        set => Volatile.Write(ref failNextWrites, value);
    }

    public int MaxConcurrentWrites => Volatile.Read(ref maxConcurrentWrites);
    public List<RuntimeState> AttemptedStates { get; } = [];
    public List<RuntimeState> SuccessfulStates { get; } = [];
    public Action<RuntimeState>? OnAttempt { get; set; }

    public Task<RuntimeState?> LoadAsync(CancellationToken cancellationToken) => Task.FromResult<RuntimeState?>(null);

    public async Task SaveAsync(RuntimeState state, CancellationToken cancellationToken)
    {
        lock (sync)
        {
            AttemptedStates.Add(state);
        }
        OnAttempt?.Invoke(state);

        int concurrent = Interlocked.Increment(ref activeWrites);
        UpdateMaximum(concurrent);
        try
        {
            if (BlockWrites)
            {
                await blockedWrites.Writer.WriteAsync(true, cancellationToken);
                await releases.Reader.ReadAsync(cancellationToken);
            }

            if (TryConsumeFailure())
            {
                throw new IOException("Simulated runtime-state write failure.");
            }

            TaskCompletionSource? milestone = null;
            lock (sync)
            {
                SuccessfulStates.Add(state);
                if (saveMilestones.Remove(SuccessfulStates.Count, out TaskCompletionSource? pending))
                {
                    milestone = pending;
                }
            }
            milestone?.TrySetResult();
        }
        finally
        {
            Interlocked.Decrement(ref activeWrites);
        }
    }

    public async ValueTask WaitForBlockedWriteAsync(CancellationToken cancellationToken = default) =>
        _ = await blockedWrites.Reader.ReadAsync(cancellationToken);

    public void ReleaseBlockedWrite() => releases.Writer.TryWrite(true);

    public Task WaitForSuccessfulSaveCountAsync(int count)
    {
        lock (sync)
        {
            if (SuccessfulStates.Count >= count)
            {
                return Task.CompletedTask;
            }

            if (!saveMilestones.TryGetValue(count, out TaskCompletionSource? milestone))
            {
                milestone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                saveMilestones.Add(count, milestone);
            }
            return milestone.Task;
        }
    }

    private bool TryConsumeFailure()
    {
        while (true)
        {
            int remaining = Volatile.Read(ref failNextWrites);
            if (remaining == 0)
            {
                return false;
            }
            if (Interlocked.CompareExchange(ref failNextWrites, remaining - 1, remaining) == remaining)
            {
                return true;
            }
        }
    }

    private void UpdateMaximum(int value)
    {
        int current;
        do
        {
            current = Volatile.Read(ref maxConcurrentWrites);
            if (current >= value)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref maxConcurrentWrites, value, current) != current);
    }
}

internal sealed class FakeClock(DateTimeOffset utcNow) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = utcNow;
}

internal sealed class FakeCurrentProcessInfo(int processId, DateTimeOffset processStartUtc) : ICurrentProcessInfo
{
    public int ProcessId { get; } = processId;
    public DateTimeOffset ProcessStartUtc { get; } = processStartUtc;
}
