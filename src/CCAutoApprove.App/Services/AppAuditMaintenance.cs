using CCAutoApprove.Core.Abstractions;
using CCAutoApprove.Core.Models;
using CCAutoApprove.Infrastructure.Configuration;
using CCAutoApprove.Infrastructure.Logging;

namespace CCAutoApprove.App.Services;

internal static class AppAuditMaintenance
{
    private static readonly TimeSpan StartupCleanupTimeout = TimeSpan.FromSeconds(2);

    internal static IAuditLog CreateLog(
        AppPaths paths,
        PersistentSettings settings,
        IClock clock) => new JsonLineAuditLog(paths, settings, clock);

    internal static Task DeleteExpiredFailSafeAsync(
        IAuditLog auditLog,
        int retentionDays,
        CancellationToken cancellationToken) =>
        DeleteExpiredFailSafeAsync(
            auditLog,
            retentionDays,
            StartupCleanupTimeout,
            cancellationToken);

    internal static async Task DeleteExpiredFailSafeAsync(
        IAuditLog auditLog,
        int retentionDays,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auditLog);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        using var cleanupCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cleanupCancellation.CancelAfter(timeout);

        try
        {
            Task cleanup = auditLog.DeleteExpiredAsync(
                retentionDays,
                cleanupCancellation.Token);
            ObserveFault(cleanup);
            await cleanup.WaitAsync(cleanupCancellation.Token).ConfigureAwait(false);
        }
        catch
        {
            // Audit maintenance must not prevent the local status UI from starting.
        }
    }

    private static void ObserveFault(Task task)
    {
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted |
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
