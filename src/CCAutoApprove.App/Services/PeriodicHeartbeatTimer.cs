namespace CCAutoApprove.App.Services;

public sealed class PeriodicHeartbeatTimer : IHeartbeatTimer
{
    private readonly PeriodicTimer timer = new(TimeSpan.FromSeconds(2));

    public ValueTask<bool> WaitForNextTickAsync(CancellationToken cancellationToken) =>
        timer.WaitForNextTickAsync(cancellationToken);

    public ValueTask DisposeAsync()
    {
        timer.Dispose();
        return ValueTask.CompletedTask;
    }
}
