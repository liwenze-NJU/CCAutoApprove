using System.Text.Json;
using CCAutoApprove.App.Services;
using CCAutoApprove.App.ViewModels;
using CCAutoApprove.Core.Abstractions;
using CCAutoApprove.Core.Models;
using CCAutoApprove.Infrastructure.Configuration;
using CCAutoApprove.Infrastructure.Logging;

namespace CCAutoApprove.App.Tests.Services;

public sealed class AppAuditMaintenanceTests
{
    private static readonly DateTimeOffset FixedUtc =
        new(2026, 8, 20, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AppStartedDisabled_AfterDetailedHookWrite_SettingsFlowReadsAndClearsRealRecord()
    {
        using var temp = new TemporaryDirectory();
        var paths = new AppPaths(temp.Path);
        var settingsStore = new JsonSettingsStore(paths.SettingsPath);
        var startupSettings = new PersistentSettings(AuditDetailLevel: AuditDetailLevel.Disabled);
        await settingsStore.SaveAsync(startupSettings, CancellationToken.None);
        var clock = new StubClock(FixedUtc);
        IAuditLog maintenanceLog = AppAuditMaintenance.CreateLog(paths, startupSettings, clock);
        var settings = new SettingsViewModel(
            settingsStore,
            maintenanceLog,
            () => Task.FromResult(true));
        await settings.LoadAsync();
        await settings.ChangeAuditDetailLevelAsync(AuditDetailLevel.Detailed);

        PersistentSettings hookSettings = await settingsStore.LoadAsync(CancellationToken.None);
        var hookLog = new JsonLineAuditLog(paths, hookSettings, clock);
        await hookLog.WriteAsync(
            CreateRequest(temp.Path),
            ApprovalDecision.Allow(DecisionSource.LocalAlwaysAllow),
            CancellationToken.None);
        var records = new RecordsViewModel(
            maintenanceLog,
            () => Task.FromResult(true),
            AuditDetailLevel.Detailed);

        await records.LoadAsync();

        AuditRecordItemViewModel record = Assert.Single(records.Records);
        Assert.Contains("review-secret", record.ToolInput, StringComparison.Ordinal);

        await settings.ChangeAuditDetailLevelAsync(AuditDetailLevel.PrivacySafe);

        Assert.Empty(await maintenanceLog.ReadRecentAsync(10, CancellationToken.None));
        Assert.Empty(Directory.EnumerateFiles(
            Path.Combine(temp.Path, "logs"),
            "audit-*.jsonl"));
    }

    [Fact]
    public async Task DeleteExpiredFailSafeAsync_UsesConfiguredRetentionDays()
    {
        var log = new RecordingAuditLog();

        await AppAuditMaintenance.DeleteExpiredFailSafeAsync(
            log,
            23,
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.Equal(23, log.RetentionDays);
        Assert.False(log.CleanupToken.IsCancellationRequested);
    }

    [Fact]
    public async Task DeleteExpiredFailSafeAsync_WhenCleanupFails_DoesNotBlockStartupOrExposeFailure()
    {
        var log = new RecordingAuditLog
        {
            DeleteExpired = (_, _) => Task.FromException(
                new IOException(@"Could not delete C:\private\audit.jsonl"))
        };

        await AppAuditMaintenance.DeleteExpiredFailSafeAsync(
            log,
            7,
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.Equal(7, log.RetentionDays);
    }

    [Fact]
    public async Task DeleteExpiredFailSafeAsync_WhenCleanupExceedsBudget_CancelsAndReturns()
    {
        var log = new RecordingAuditLog
        {
            DeleteExpired = static (_, cancellationToken) =>
                Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
        };

        await AppAuditMaintenance.DeleteExpiredFailSafeAsync(
                log,
                7,
                TimeSpan.FromMilliseconds(25),
                CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(log.CleanupToken.IsCancellationRequested);
    }

    [Fact]
    public async Task DeleteExpiredFailSafeAsync_WhenLifecycleIsCanceled_PropagatesCancellationToCleanup()
    {
        var log = new RecordingAuditLog
        {
            DeleteExpired = static (_, cancellationToken) =>
                Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
        };
        using var lifecycle = new CancellationTokenSource();
        lifecycle.Cancel();

        await AppAuditMaintenance.DeleteExpiredFailSafeAsync(
            log,
            7,
            TimeSpan.FromSeconds(1),
            lifecycle.Token);

        Assert.True(log.CleanupToken.IsCancellationRequested);
    }

    private static ApprovalRequest CreateRequest(string projectPath)
    {
        using JsonDocument input = JsonDocument.Parse("""
            {"command":"echo review-secret"}
            """);
        return new ApprovalRequest(
            Guid.Parse("11111111-2222-3333-4444-555555555555"),
            "review-session",
            projectPath,
            "Bash",
            input.RootElement.Clone(),
            "default",
            null,
            FixedUtc);
    }

    private sealed class RecordingAuditLog : IAuditLog
    {
        public Func<int, CancellationToken, Task> DeleteExpired { get; init; } =
            static (_, _) => Task.CompletedTask;
        public int? RetentionDays { get; private set; }
        public CancellationToken CleanupToken { get; private set; }

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

        public Task DeleteExpiredAsync(int retentionDays, CancellationToken cancellationToken)
        {
            RetentionDays = retentionDays;
            CleanupToken = cancellationToken;
            return DeleteExpired(retentionDays, cancellationToken);
        }
    }

    private sealed class StubClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "CCAutoApprove.App.Tests",
                $"audit-maintenance-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (!Directory.Exists(Path))
            {
                return;
            }

            string temporaryPrefix = System.IO.Path.GetFullPath(
                System.IO.Path.GetTempPath()).TrimEnd(
                    System.IO.Path.DirectorySeparatorChar,
                    System.IO.Path.AltDirectorySeparatorChar)
                + System.IO.Path.DirectorySeparatorChar;
            string resolved = System.IO.Path.GetFullPath(Path);
            if (!resolved.StartsWith(temporaryPrefix, StringComparison.OrdinalIgnoreCase)
                || !System.IO.Path.GetFileName(resolved).StartsWith(
                    "audit-maintenance-",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Refusing to delete unexpected test directory {resolved}.");
            }

            Directory.Delete(resolved, recursive: true);
        }
    }
}
