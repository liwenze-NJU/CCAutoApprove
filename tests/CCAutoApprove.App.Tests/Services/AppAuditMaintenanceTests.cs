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
    private static readonly JsonSerializerOptions JsonLineOptions =
        new(JsonDefaults.Options) { WriteIndented = false };

    [Fact]
    public async Task AppStartedDisabled_AfterDetailedRecordAppears_SettingsFlowReadsAndClearsRealRecord()
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
        Assert.Equal(AuditDetailLevel.Detailed, hookSettings.AuditDetailLevel);
        await WriteDetailedAuditRecordAsync(paths, temp.Path);
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
    public async Task InitializeThenScheduleAsync_CompletesInitializationBeforeRetentionStarts()
    {
        var initializationEntered = CreateCompletionSource();
        var allowInitializationToComplete = CreateCompletionSource();
        var retentionStarted = CreateCompletionSource();
        var allowRetentionToComplete = CreateCompletionSource();
        var events = new List<string>();
        var log = new RecordingAuditLog
        {
            DeleteExpired = async (_, _) =>
            {
                events.Add("retention-started");
                retentionStarted.SetResult();
                await allowRetentionToComplete.Task;
            }
        };

        Task<Task> startup = AppAuditMaintenance.InitializeThenScheduleAsync(
            async () =>
            {
                events.Add("initialization-started");
                initializationEntered.SetResult();
                await allowInitializationToComplete.Task;
                events.Add("initialization-completed");
            },
            log,
            23,
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        await initializationEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(retentionStarted.Task.IsCompleted);

        allowInitializationToComplete.SetResult();
        Task backgroundMaintenance = await startup.WaitAsync(TimeSpan.FromSeconds(2));
        await retentionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(
            ["initialization-started", "initialization-completed", "retention-started"],
            events);
        Assert.Equal(23, log.RetentionDays);
        Assert.False(backgroundMaintenance.IsCompleted);

        allowRetentionToComplete.SetResult();
        await backgroundMaintenance.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ScheduleDeleteExpired_WhenCleanupIgnoresCancellation_ReturnsWithoutWaiting()
    {
        var retentionStarted = CreateCompletionSource();
        var allowRetentionToComplete = CreateCompletionSource();
        var log = new RecordingAuditLog
        {
            DeleteExpired = async (_, _) =>
            {
                retentionStarted.SetResult();
                await allowRetentionToComplete.Task;
            }
        };

        Task backgroundMaintenance = AppAuditMaintenance.ScheduleDeleteExpired(
            log,
            7,
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        await retentionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(backgroundMaintenance.IsCompleted);

        allowRetentionToComplete.SetResult();
        await backgroundMaintenance.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ScheduleDeleteExpired_UsesConfiguredRetentionDaysAndCancelsAtBudget()
    {
        var log = new RecordingAuditLog
        {
            DeleteExpired = static (_, cancellationToken) =>
                Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
        };

        Task backgroundMaintenance = AppAuditMaintenance.ScheduleDeleteExpired(
            log,
            23,
            TimeSpan.FromMilliseconds(25),
            CancellationToken.None);

        await backgroundMaintenance.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(23, log.RetentionDays);
        Assert.True(log.CleanupToken.IsCancellationRequested);
    }

    [Fact]
    public async Task ScheduleDeleteExpired_WhenCleanupFails_DoesNotFaultBackgroundTask()
    {
        var log = new RecordingAuditLog
        {
            DeleteExpired = (_, _) => Task.FromException(
                new IOException(@"Could not delete C:\private\audit.jsonl"))
        };

        Task backgroundMaintenance = AppAuditMaintenance.ScheduleDeleteExpired(
            log,
            7,
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        await backgroundMaintenance.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(7, log.RetentionDays);
    }

    [Fact]
    public async Task CancelLifetimeWithoutWaiting_CancelsButDoesNotWaitForUncancelableCleanup()
    {
        var retentionStarted = CreateCompletionSource();
        var allowRetentionToComplete = CreateCompletionSource();
        var log = new RecordingAuditLog
        {
            DeleteExpired = async (_, _) =>
            {
                retentionStarted.SetResult();
                await allowRetentionToComplete.Task;
            }
        };
        var lifetime = new CancellationTokenSource();
        Task backgroundMaintenance = AppAuditMaintenance.ScheduleDeleteExpired(
            log,
            7,
            TimeSpan.FromSeconds(1),
            lifetime.Token);
        await retentionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        AppAuditMaintenance.CancelLifetimeWithoutWaiting(lifetime, backgroundMaintenance);

        Assert.True(log.CleanupToken.IsCancellationRequested);
        Assert.False(backgroundMaintenance.IsCompleted);

        allowRetentionToComplete.SetResult();
        await backgroundMaintenance.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task CancelLifetimeWithoutWaiting_WhenCleanupFaultsLater_ObservesFailure()
    {
        var retentionStarted = CreateCompletionSource();
        var cleanupCompletion = CreateCompletionSource();
        var log = new RecordingAuditLog
        {
            DeleteExpired = (_, _) =>
            {
                retentionStarted.SetResult();
                return cleanupCompletion.Task;
            }
        };
        var lifetime = new CancellationTokenSource();
        Task backgroundMaintenance = AppAuditMaintenance.ScheduleDeleteExpired(
            log,
            7,
            TimeSpan.FromSeconds(1),
            lifetime.Token);
        await retentionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        AppAuditMaintenance.CancelLifetimeWithoutWaiting(lifetime, backgroundMaintenance);
        cleanupCompletion.SetException(
            new IOException(@"Could not delete C:\private\audit.jsonl"));

        await backgroundMaintenance.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(log.CleanupToken.IsCancellationRequested);
    }

    private static TaskCompletionSource CreateCompletionSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task WriteDetailedAuditRecordAsync(AppPaths paths, string projectPath)
    {
        ApprovalRequest request = CreateRequest(projectPath);
        var record = new AuditRecord(
            FixedUtc,
            request.RequestId,
            Path.GetFullPath(projectPath),
            request.ToolName,
            ApprovalDecisionKind.Allow,
            DecisionSource.LocalAlwaysAllow,
            request.SessionId,
            request.PermissionMode,
            request.ToolInput,
            request.PermissionSuggestions);
        string logsDirectory = Path.Combine(paths.BasePath, "logs");
        Directory.CreateDirectory(logsDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(logsDirectory, $"audit-{FixedUtc:yyyy-MM-dd}.jsonl"),
            JsonSerializer.Serialize(record, JsonLineOptions) + Environment.NewLine);
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
