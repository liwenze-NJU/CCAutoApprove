namespace CCAutoApprove.App.Services;

internal static class AppStartupLifecycle
{
    internal static async Task RunAsync(
        Func<CancellationToken, Task> startupAsync,
        Action onFailure,
        CancellationToken lifetimeToken)
    {
        ArgumentNullException.ThrowIfNull(startupAsync);
        ArgumentNullException.ThrowIfNull(onFailure);

        try
        {
            lifetimeToken.ThrowIfCancellationRequested();
            await startupAsync(lifetimeToken);
        }
        catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested)
        {
            // App shutdown during async startup is a normal lifecycle transition.
        }
        catch when (lifetimeToken.IsCancellationRequested)
        {
            // Do not surface a startup failure after shutdown has already begun.
        }
        catch
        {
            onFailure();
        }
    }
}
