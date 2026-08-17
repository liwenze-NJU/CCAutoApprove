namespace CCAutoApprove.App.Services;

public interface IHeartbeatTimer : IAsyncDisposable
{
    ValueTask<bool> WaitForNextTickAsync(CancellationToken cancellationToken);
}
