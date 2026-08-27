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

    internal static Task<Task> InitializeThenScheduleAsync(
        Func<CancellationToken, Task> initializeAsync,
        IAuditLog auditLog,
        int retentionDays,
        CancellationToken cancellationToken) =>
        InitializeThenScheduleAsync(
            initializeAsync,
            auditLog,
            retentionDays,
            StartupCleanupTimeout,
            cancellationToken);

    internal static async Task<Task> InitializeThenScheduleAsync(
        Func<CancellationToken, Task> initializeAsync,
        IAuditLog auditLog,
        int retentionDays,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(initializeAsync);
        cancellationToken.ThrowIfCancellationRequested();
        await initializeAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return ScheduleDeleteExpired(
            auditLog,
            retentionDays,
            timeout,
            cancellationToken);
    }

    internal static Task ScheduleDeleteExpired(
        IAuditLog auditLog,
        int retentionDays,
        CancellationToken cancellationToken) =>
        ScheduleDeleteExpired(
            auditLog,
            retentionDays,
            StartupCleanupTimeout,
            cancellationToken);

    internal static Task ScheduleDeleteExpired(
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

        Task backgroundTask = Task.Run(async () =>
        {
            try
            {
                using var cleanupCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cleanupCancellation.CancelAfter(timeout);
                await auditLog.DeleteExpiredAsync(
                        retentionDays,
                        cleanupCancellation.Token)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Background audit maintenance is best-effort and must stay invisible.
            }
        }, CancellationToken.None);
        ObserveFault(backgroundTask);
        return backgroundTask;
    }

    internal static void CancelLifetimeWithoutWaiting(
        CancellationTokenSource lifetimeCancellation,
        Task? backgroundTask)
    {
        ArgumentNullException.ThrowIfNull(lifetimeCancellation);
        try
        {
            lifetimeCancellation.Cancel();
        }
        catch
        {
            // Exit remains fail-safe even if a cancellation callback misbehaves.
        }

        if (backgroundTask is null || backgroundTask.IsCompleted)
        {
            ObserveFault(backgroundTask);
            lifetimeCancellation.Dispose();
            return;
        }

        ObserveFault(backgroundTask);
        _ = backgroundTask.ContinueWith(
            static (_, state) => ((CancellationTokenSource)state!).Dispose(),
            lifetimeCancellation,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static void ObserveFault(Task? task)
    {
        if (task is null)
        {
            return;
        }

        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted |
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
