using System.Text;
using System.Text.Json;
using CCAutoApprove.Core.Abstractions;
using CCAutoApprove.Core.Models;
using CCAutoApprove.Infrastructure.Configuration;
using CCAutoApprove.Infrastructure.Logging;

namespace CCAutoApprove.Infrastructure.Tests.Logging;

public sealed class JsonLineAuditLogTests
{
    private static readonly DateTimeOffset FixedUtc = new(2026, 8, 17, 10, 20, 30, TimeSpan.Zero);

    [Theory]
    [InlineData(AuditDetailLevel.Disabled, false, false)]
    [InlineData(AuditDetailLevel.PrivacySafe, true, false)]
    [InlineData(AuditDetailLevel.Detailed, true, true)]
    public async Task WriteAsync_RespectsDetailLevel(
        AuditDetailLevel level, bool createsLog, bool containsSecret)
    {
        using var temp = new TemporaryDirectory();
        IAuditLog log = CreateLog(temp.Path, level);

        await log.WriteAsync(CreateSecretRequest(Guid.Parse("11111111-1111-1111-1111-111111111111")),
            ApprovalDecision.Allow(DecisionSource.LocalAlwaysAllow), CancellationToken.None);

        byte[] bytes = ReadAllLogBytes(temp.Path);
        string combined = Encoding.UTF8.GetString(bytes);
        Assert.Equal(createsLog, bytes.Length > 0);
        Assert.Equal(containsSecret, combined.Contains("secret-value-123", StringComparison.Ordinal));

        if (!createsLog)
        {
            Assert.False(Directory.Exists(Path.Combine(temp.Path, "logs")));
            return;
        }

        Assert.Equal(Encoding.UTF8.GetBytes(combined), bytes);
        string line = Assert.Single(combined.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        using JsonDocument document = JsonDocument.Parse(line);
        JsonElement root = document.RootElement;
        Assert.Equal(FixedUtc, root.GetProperty("timeUtc").GetDateTimeOffset());
        Assert.Equal("11111111-1111-1111-1111-111111111111", root.GetProperty("requestId").GetString());
        Assert.Equal("D:\\projects\\safe-app", root.GetProperty("project").GetString());
        Assert.Equal("Bash", root.GetProperty("tool").GetString());
        Assert.Equal((int)ApprovalDecisionKind.Allow, root.GetProperty("decision").GetInt32());
        Assert.Equal((int)DecisionSource.LocalAlwaysAllow, root.GetProperty("source").GetInt32());

        string[] optionalProperties = ["sessionId", "permissionMode", "toolInput", "permissionSuggestions"];
        foreach (string property in optionalProperties)
        {
            Assert.Equal(level == AuditDetailLevel.Detailed, root.TryGetProperty(property, out _));
        }

        if (level == AuditDetailLevel.PrivacySafe)
        {
            Assert.DoesNotContain("toolInput", combined, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("command", combined, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.Equal("session-secret", root.GetProperty("sessionId").GetString());
            Assert.Equal("default", root.GetProperty("permissionMode").GetString());
            Assert.Equal("deploy --api-key secret-value-123",
                root.GetProperty("toolInput").GetProperty("command").GetString());
            Assert.Equal("addRules",
                root.GetProperty("permissionSuggestions")[0].GetProperty("type").GetString());
            Assert.Equal(10, root.EnumerateObject().Count());
        }
    }

    [Fact]
    public async Task WriteAsync_FiftyConcurrentWrites_ProducesOneCompleteJsonObjectPerLine()
    {
        using var temp = new TemporaryDirectory();
        IAuditLog log = CreateLog(temp.Path, AuditDetailLevel.Detailed);
        Guid[] requestIds = Enumerable.Range(1, 50)
            .Select(index => Guid.Parse($"00000000-0000-0000-0000-{index:000000000000}"))
            .ToArray();

        await Task.WhenAll(requestIds.Select(requestId => log.WriteAsync(
            CreateSecretRequest(requestId),
            ApprovalDecision.Allow(DecisionSource.LocalAlwaysAllow),
            CancellationToken.None)));

        string path = Assert.Single(Directory.EnumerateFiles(Path.Combine(temp.Path, "logs"), "audit-*.jsonl"));
        byte[] bytes = await File.ReadAllBytesAsync(path);
        string text = Encoding.UTF8.GetString(bytes);
        Assert.EndsWith(Environment.NewLine, text, StringComparison.Ordinal);
        string[] lines = text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.InRange(lines.Length, 1, requestIds.Length);

        Guid[] actualIds = lines.Select(line =>
        {
            using JsonDocument document = JsonDocument.Parse(line);
            Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
            return document.RootElement.GetProperty("requestId").GetGuid();
        }).ToArray();
        Assert.Equal(actualIds.Length, actualIds.Distinct().Count());
        Assert.All(actualIds, requestId => Assert.Contains(requestId, requestIds));
    }

    [Fact]
    public async Task DeleteExpiredAsync_DeletesOnlyAuditFilesOlderThanRetentionBoundary()
    {
        using var temp = new TemporaryDirectory();
        string logs = Path.Combine(temp.Path, "logs");
        Directory.CreateDirectory(logs);
        string expired = Path.Combine(logs, "audit-2026-08-09.jsonl");
        string boundary = Path.Combine(logs, "audit-2026-08-10.jsonl");
        string current = Path.Combine(logs, "audit-2026-08-17.jsonl");
        string unrelated = Path.Combine(logs, "error-2026-08-01.jsonl");
        foreach (string path in new[] { expired, boundary, current, unrelated })
        {
            await File.WriteAllTextAsync(path, "fixture");
        }

        IAuditLog log = CreateLog(temp.Path, AuditDetailLevel.PrivacySafe);
        await log.DeleteExpiredAsync(7, CancellationToken.None);

        Assert.False(File.Exists(expired));
        Assert.True(File.Exists(boundary));
        Assert.True(File.Exists(current));
        Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public async Task ReadRecentAsync_TwoHundred_ReturnsRecordsNewestFirstAcrossDailyFiles()
    {
        using var temp = new TemporaryDirectory();
        var clock = new StubClock(new DateTimeOffset(2026, 8, 16, 23, 59, 59, TimeSpan.Zero));
        IAuditLog log = CreateLog(temp.Path, AuditDetailLevel.PrivacySafe, clock);
        Guid oldest = Guid.Parse("10000000-0000-0000-0000-000000000001");
        Guid middle = Guid.Parse("20000000-0000-0000-0000-000000000002");
        Guid newest = Guid.Parse("30000000-0000-0000-0000-000000000003");

        await log.WriteAsync(CreateSecretRequest(middle), ApprovalDecision.Allow(DecisionSource.LocalRule), CancellationToken.None);
        clock.UtcNow = new DateTimeOffset(2026, 8, 15, 8, 0, 0, TimeSpan.Zero);
        await log.WriteAsync(CreateSecretRequest(oldest), ApprovalDecision.Ask("confirm"), CancellationToken.None);
        clock.UtcNow = new DateTimeOffset(2026, 8, 17, 0, 0, 1, TimeSpan.Zero);
        await log.WriteAsync(CreateSecretRequest(newest), ApprovalDecision.Allow(DecisionSource.AI), CancellationToken.None);

        IReadOnlyList<AuditRecord> records = await log.ReadRecentAsync(200, CancellationToken.None);

        Assert.Equal(new[] { newest, middle, oldest }, records.Select(record => record.RequestId));
        Assert.All(records, record =>
        {
            Assert.Null(record.SessionId);
            Assert.Null(record.PermissionMode);
            Assert.Null(record.ToolInput);
            Assert.Null(record.PermissionSuggestions);
        });
    }

    [Fact]
    public async Task ReadRecentAsync_WhenNewestPartitionIsSufficient_DoesNotOpenLockedOlderPartition()
    {
        using var temp = new TemporaryDirectory();
        string logs = Path.Combine(temp.Path, "logs");
        Directory.CreateDirectory(logs);
        string older = Path.Combine(logs, "audit-2026-08-19.jsonl");
        await File.WriteAllTextAsync(
            older,
            CreateCountLine("2026-08-19T23:59:59+00:00", 999, 0));
        string[] newestLines = Enumerable.Range(1, 200)
            .Select(index => CreateCountLine(
                $"2026-08-20T12:{index / 60:00}:{index % 60:00}+00:00",
                index,
                0))
            .ToArray();
        await File.WriteAllLinesAsync(
            Path.Combine(logs, "audit-2026-08-20.jsonl"),
            newestLines);
        await using var lockedOlderFile = new FileStream(
            older,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None);
        IAuditLog log = CreateLog(temp.Path, AuditDetailLevel.PrivacySafe);

        IReadOnlyList<AuditRecord> records = await log.ReadRecentAsync(200, CancellationToken.None);

        Assert.Equal(200, records.Count);
        Assert.DoesNotContain(records, record =>
            record.RequestId == Guid.Parse("00000000-0000-0000-0000-000000000999"));
    }

    [Fact]
    public async Task CountAllowedAsync_UtcWindow_ReadsOnlyOverlappingFilesAndSkipsMalformedLines()
    {
        using var temp = new TemporaryDirectory();
        string logs = Path.Combine(temp.Path, "logs");
        Directory.CreateDirectory(logs);
        string older = Path.Combine(logs, "audit-2026-08-18.jsonl");
        await File.WriteAllTextAsync(older, CreateCountLine("2026-08-18T20:00:00+00:00", 1, 0));
        await File.WriteAllLinesAsync(Path.Combine(logs, "audit-2026-08-19.jsonl"),
        [
            CreateCountLine("2026-08-19T15:59:59+00:00", 2, 0),
            CreateCountLine("2026-08-19T16:00:00+00:00", 3, 0),
            "{malformed"
        ]);
        await File.WriteAllLinesAsync(Path.Combine(logs, "audit-2026-08-20.jsonl"),
        [
            CreateCountLine("2026-08-20T15:59:59+00:00", 4, 0),
            CreateCountLine("2026-08-20T12:00:00+00:00", 5, 1),
            CreateCountLine("2026-08-20T16:00:00+00:00", 6, 0),
            "not-json"
        ]);
        await using var lockedOlderFile = new FileStream(
            older,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None);
        IAuditLog log = CreateLog(temp.Path, AuditDetailLevel.PrivacySafe);

        int count = await log.CountAllowedAsync(
            new DateTimeOffset(2026, 8, 19, 16, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 20, 16, 0, 0, TimeSpan.Zero),
            CancellationToken.None);

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task CountAllowedAsync_WhenCanceledWhileWaitingForMutex_StopsWaiting()
    {
        using var temp = new TemporaryDirectory();
        IAuditLog log = CreateLog(temp.Path, AuditDetailLevel.PrivacySafe);
        await using var heldMutex = new HeldAuditMutex(temp.Path);
        using var cancellation = new CancellationTokenSource();

        Task<int> count = log.CountAllowedAsync(
            new DateTimeOffset(2026, 8, 19, 16, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 20, 16, 0, 0, TimeSpan.Zero),
            cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            count.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task ClearAsync_RemovesAuditFilesAndLeavesOtherLogFiles()
    {
        using var temp = new TemporaryDirectory();
        IAuditLog log = CreateLog(temp.Path, AuditDetailLevel.PrivacySafe);
        await log.WriteAsync(CreateSecretRequest(Guid.NewGuid()),
            ApprovalDecision.Allow(DecisionSource.LocalAlwaysAllow), CancellationToken.None);
        string otherLog = Path.Combine(temp.Path, "logs", "error-2026-08-17.jsonl");
        await File.WriteAllTextAsync(otherLog, "keep");

        await log.ClearAsync(CancellationToken.None);

        Assert.Empty(Directory.EnumerateFiles(Path.Combine(temp.Path, "logs"), "audit-*.jsonl"));
        Assert.True(File.Exists(otherLog));
    }

    [Fact]
    public async Task DeleteDetailedAsync_RemovesOnlyDetailedRecordsAndPreservesMalformedLines()
    {
        using var temp = new TemporaryDirectory();
        string logs = Path.Combine(temp.Path, "logs");
        Directory.CreateDirectory(logs);
        string file = Path.Combine(logs, "audit-2026-08-17.jsonl");
        string privacy = CreateCountLine("2026-08-17T10:00:00+00:00", 1, 0);
        string detailed = CreateCountLine("2026-08-17T10:01:00+00:00", 2, 0)[..^1]
            + ",\"toolInput\":{\"command\":\"TOP_SECRET\"}}";
        const string malformed = "{malformed";
        await File.WriteAllLinesAsync(file, [privacy, detailed, malformed]);
        IAuditLog log = CreateLog(temp.Path, AuditDetailLevel.PrivacySafe);

        await log.DeleteDetailedAsync(CancellationToken.None);

        Assert.Equal([privacy, malformed], await File.ReadAllLinesAsync(file));
        Assert.DoesNotContain("TOP_SECRET", await File.ReadAllTextAsync(file), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeleteDetailedAsync_ConcurrentPrivacyWrite_CannotRaceAtomicRewrite()
    {
        using var temp = new TemporaryDirectory();
        string logs = Path.Combine(temp.Path, "logs");
        Directory.CreateDirectory(logs);
        string file = Path.Combine(logs, "audit-2026-08-17.jsonl");
        string privacy = CreateCountLine("2026-08-17T10:00:00+00:00", 1, 0);
        string detailed = CreateCountLine("2026-08-17T10:01:00+00:00", 2, 0)[..^1]
            + ",\"sessionId\":\"secret\"}";
        await File.WriteAllLinesAsync(file, [privacy, detailed]);
        using var rewriter = new BlockingAuditFileRewriter(new AtomicAuditFileRewriter());
        IAuditLog deletingLog = new JsonLineAuditLog(
            new AppPaths(temp.Path),
            new PersistentSettings(AuditDetailLevel: AuditDetailLevel.PrivacySafe),
            new StubClock(FixedUtc),
            rewriter);
        IAuditLog writingLog = CreateLog(temp.Path, AuditDetailLevel.PrivacySafe);

        Task deletion = deletingLog.DeleteDetailedAsync(CancellationToken.None);
        Assert.True(rewriter.Started.Wait(TimeSpan.FromSeconds(2)));
        Task concurrentWrite = writingLog.WriteAsync(
            CreateSecretRequest(Guid.Parse("00000000-0000-0000-0000-000000000003")),
            ApprovalDecision.Allow(DecisionSource.LocalAlwaysAllow),
            CancellationToken.None);
        await concurrentWrite.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(deletion.IsCompleted);

        rewriter.Release.Set();
        await deletion;

        Assert.Equal([privacy], await File.ReadAllLinesAsync(file));
    }

    [Fact]
    public async Task DeleteDetailedAsync_WhenCanceledWhileWaitingForMutex_StopsWithoutMutation()
    {
        using var temp = new TemporaryDirectory();
        string logs = Path.Combine(temp.Path, "logs");
        Directory.CreateDirectory(logs);
        string file = Path.Combine(logs, "audit-2026-08-17.jsonl");
        string privacy = CreateCountLine("2026-08-17T10:00:00+00:00", 1, 0);
        string detailed = CreateCountLine("2026-08-17T10:01:00+00:00", 2, 0)[..^1]
            + ",\"sessionId\":\"secret\"}";
        await File.WriteAllLinesAsync(file, [privacy, detailed]);
        byte[] original = await File.ReadAllBytesAsync(file);
        IAuditLog log = CreateLog(temp.Path, AuditDetailLevel.PrivacySafe);
        await using var heldMutex = new HeldAuditMutex(temp.Path);
        using var cancellation = new CancellationTokenSource();

        Task deletion = log.DeleteDetailedAsync(cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            deletion.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(original, await File.ReadAllBytesAsync(file));
    }

    [Fact]
    public async Task WriteAsync_AlreadyCanceled_DoesNotCreateLogOrLeaveAHandleOpen()
    {
        using var temp = new TemporaryDirectory();
        IAuditLog log = CreateLog(temp.Path, AuditDetailLevel.Detailed);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => log.WriteAsync(
            CreateSecretRequest(Guid.NewGuid()),
            ApprovalDecision.Allow(DecisionSource.LocalAlwaysAllow),
            cancellation.Token));

        Assert.False(Directory.Exists(Path.Combine(temp.Path, "logs")));
        Directory.Delete(temp.Path);
        Directory.CreateDirectory(temp.Path);
    }

    [Fact]
    public async Task WriteAsync_WhenAuditPreparationFails_DoesNotEscapeOrCreatePartialLog()
    {
        using var temp = new TemporaryDirectory();
        IAuditLog log = CreateLog(temp.Path, AuditDetailLevel.Detailed, new ThrowingClock());

        await log.WriteAsync(CreateSecretRequest(Guid.NewGuid()),
            ApprovalDecision.Allow(DecisionSource.LocalAlwaysAllow), CancellationToken.None);

        Assert.False(Directory.Exists(Path.Combine(temp.Path, "logs")));
    }

    [Fact]
    public async Task WriteAsync_WhenAuditDependencyThrowsNonCallerCancellation_DoesNotEscape()
    {
        using var temp = new TemporaryDirectory();
        IAuditLog log = CreateLog(temp.Path, AuditDetailLevel.Detailed, new NonCallerCancellingClock());

        await log.WriteAsync(CreateSecretRequest(Guid.NewGuid()),
            ApprovalDecision.Allow(DecisionSource.LocalAlwaysAllow), CancellationToken.None);

        Assert.False(Directory.Exists(Path.Combine(temp.Path, "logs")));
    }

    [Fact]
    public async Task WriteAsync_PrivacySafe_NormalizesProjectPathBeforePersisting()
    {
        using var temp = new TemporaryDirectory();
        IAuditLog log = CreateLog(temp.Path, AuditDetailLevel.PrivacySafe);
        ApprovalRequest request = CreateSecretRequest(Guid.NewGuid()) with
        {
            ProjectPath = @"D:\projects\safe-app\subdirectory\..\"
        };

        await log.WriteAsync(request,
            ApprovalDecision.Allow(DecisionSource.LocalAlwaysAllow), CancellationToken.None);

        string json = Encoding.UTF8.GetString(ReadAllLogBytes(temp.Path));
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal(@"D:\projects\safe-app", document.RootElement.GetProperty("project").GetString());
    }

    [Fact]
    public async Task ClearAsync_WhenMutexIsHeld_WaitsAndThenClearsAuditFiles()
    {
        using var temp = new TemporaryDirectory();
        string logs = Path.Combine(temp.Path, "logs");
        Directory.CreateDirectory(logs);
        string auditFile = Path.Combine(logs, "audit-2026-08-17.jsonl");
        await File.WriteAllTextAsync(auditFile, "fixture");
        IAuditLog log = CreateLog(temp.Path, AuditDetailLevel.PrivacySafe);
        Task clear;

        await using (var heldMutex = new HeldAuditMutex(temp.Path))
        {
            clear = log.ClearAsync(CancellationToken.None);
            await Task.Delay(TimeSpan.FromMilliseconds(100));
            Assert.False(clear.IsCompleted);
        }

        await clear;
        Assert.False(File.Exists(auditFile));
    }

    [Fact]
    public async Task DeleteExpiredAsync_WhenMutexIsHeld_WaitsAndThenDeletesExpiredFiles()
    {
        using var temp = new TemporaryDirectory();
        string logs = Path.Combine(temp.Path, "logs");
        Directory.CreateDirectory(logs);
        string expired = Path.Combine(logs, "audit-2026-08-09.jsonl");
        await File.WriteAllTextAsync(expired, "fixture");
        IAuditLog log = CreateLog(temp.Path, AuditDetailLevel.PrivacySafe);
        Task deletion;

        await using (var heldMutex = new HeldAuditMutex(temp.Path))
        {
            deletion = log.DeleteExpiredAsync(7, CancellationToken.None);
            await Task.Delay(TimeSpan.FromMilliseconds(100));
            Assert.False(deletion.IsCompleted);
        }

        await deletion;
        Assert.False(File.Exists(expired));
    }

    [Fact]
    public async Task ReadRecentAsync_WhenMutexIsHeld_WaitsAndThenReturnsRecords()
    {
        using var temp = new TemporaryDirectory();
        IAuditLog log = CreateLog(temp.Path, AuditDetailLevel.PrivacySafe);
        Guid requestId = Guid.NewGuid();
        await log.WriteAsync(CreateSecretRequest(requestId),
            ApprovalDecision.Allow(DecisionSource.LocalAlwaysAllow), CancellationToken.None);
        Task<IReadOnlyList<AuditRecord>> read;

        await using (var heldMutex = new HeldAuditMutex(temp.Path))
        {
            read = log.ReadRecentAsync(1, CancellationToken.None);
            await Task.Delay(TimeSpan.FromMilliseconds(100));
            Assert.False(read.IsCompleted);
        }

        Assert.Equal(requestId, Assert.Single(await read).RequestId);
    }

    [Fact]
    public async Task WriteAsync_WhenDifferentDataRootMutexIsHeld_WritesRecord()
    {
        using var blockedRoot = new TemporaryDirectory();
        using var writableRoot = new TemporaryDirectory();
        await using var heldMutex = new HeldAuditMutex(blockedRoot.Path);
        IAuditLog log = CreateLog(writableRoot.Path, AuditDetailLevel.Detailed);

        await log.WriteAsync(
            CreateSecretRequest(Guid.NewGuid()),
            ApprovalDecision.Allow(DecisionSource.LocalAlwaysAllow),
            CancellationToken.None);

        Assert.NotEmpty(Directory.EnumerateFiles(
            Path.Combine(writableRoot.Path, "logs"),
            "audit-*.jsonl"));
    }

    [Fact]
    public async Task WriteAsync_WhenMutexIsUnavailable_DiscardsRecordWithinShortTimeout()
    {
        using var temp = new TemporaryDirectory();
        using var mutexHeld = new ManualResetEventSlim();
        using var releaseMutex = new ManualResetEventSlim();
        Task holder = Task.Run(() =>
        {
            using var mutex = new Mutex(false, AuditMutexName(temp.Path));
            Assert.True(mutex.WaitOne(TimeSpan.FromSeconds(1)));
            try
            {
                mutexHeld.Set();
                releaseMutex.Wait(TimeSpan.FromSeconds(5));
            }
            finally
            {
                mutex.ReleaseMutex();
            }
        });
        Assert.True(mutexHeld.Wait(TimeSpan.FromSeconds(2)));

        try
        {
            IAuditLog log = CreateLog(temp.Path, AuditDetailLevel.Detailed);
            Task write = log.WriteAsync(CreateSecretRequest(Guid.NewGuid()),
                ApprovalDecision.Allow(DecisionSource.LocalAlwaysAllow), CancellationToken.None);

            await write.WaitAsync(TimeSpan.FromMilliseconds(500));
            Assert.Empty(Directory.Exists(Path.Combine(temp.Path, "logs"))
                ? Directory.EnumerateFiles(Path.Combine(temp.Path, "logs"), "audit-*.jsonl")
                : []);
        }
        finally
        {
            releaseMutex.Set();
            await holder;
        }
    }

    private static IAuditLog CreateLog(
        string basePath,
        AuditDetailLevel level,
        IClock? clock = null) => level == AuditDetailLevel.Disabled
        ? new NullAuditLog()
        : new JsonLineAuditLog(
            new AppPaths(basePath),
            new PersistentSettings(AuditDetailLevel: level),
            clock ?? new StubClock(FixedUtc));

    private static string AuditMutexName(string basePath) =>
        JsonLineAuditLog.CreateMutexName(Path.Combine(basePath, "logs"));

    private static ApprovalRequest CreateSecretRequest(Guid requestId)
    {
        using JsonDocument toolInput = JsonDocument.Parse("""
            {"command":"deploy --api-key secret-value-123","description":"production deploy"}
            """);
        using JsonDocument suggestions = JsonDocument.Parse("""
            [{"type":"addRules","rules":[{"toolName":"Bash","ruleContent":"deploy"}],"behavior":"allow"}]
            """);
        return new ApprovalRequest(
            requestId,
            "session-secret",
            "D:\\projects\\safe-app",
            "Bash",
            toolInput.RootElement.Clone(),
            "default",
            suggestions.RootElement.Clone(),
            FixedUtc.AddMinutes(-1));
    }

    private static byte[] ReadAllLogBytes(string basePath)
    {
        string logs = Path.Combine(basePath, "logs");
        return Directory.Exists(logs)
            ? Directory.EnumerateFiles(logs, "audit-*.jsonl")
                .Order()
                .SelectMany(File.ReadAllBytes)
                .ToArray()
            : [];
    }

    private static string CreateCountLine(string timeUtc, int idSuffix, int decision) =>
        $$"""{"timeUtc":"{{timeUtc}}","requestId":"00000000-0000-0000-0000-{{idSuffix.ToString("000000000000")}}","project":"D:\\projects\\safe-app","tool":"Bash","decision":{{decision}},"source":0}""";

    private sealed class StubClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }

    private sealed class ThrowingClock : IClock
    {
        public DateTimeOffset UtcNow => throw new IOException("Synthetic audit clock failure.");
    }

    private sealed class NonCallerCancellingClock : IClock
    {
        public DateTimeOffset UtcNow => throw new OperationCanceledException("Synthetic dependency cancellation.");
    }

    private sealed class HeldAuditMutex : IAsyncDisposable
    {
        private readonly ManualResetEventSlim release = new();
        private readonly Task holder;

        public HeldAuditMutex(string basePath)
        {
            using var acquired = new ManualResetEventSlim();
            holder = Task.Run(() =>
            {
                using var mutex = new Mutex(false, AuditMutexName(basePath));
                Assert.True(mutex.WaitOne(TimeSpan.FromSeconds(1)));
                try
                {
                    acquired.Set();
                    release.Wait(TimeSpan.FromSeconds(5));
                }
                finally
                {
                    mutex.ReleaseMutex();
                }
            });
            Assert.True(acquired.Wait(TimeSpan.FromSeconds(2)));
        }

        public async ValueTask DisposeAsync()
        {
            release.Set();
            await holder;
            release.Dispose();
        }
    }

    private sealed class BlockingAuditFileRewriter(IAuditFileRewriter inner) : IAuditFileRewriter, IDisposable
    {
        public ManualResetEventSlim Started { get; } = new();
        public ManualResetEventSlim Release { get; } = new();

        public void Rewrite(
            string filePath,
            IReadOnlyList<string> retainedLines,
            CancellationToken cancellationToken)
        {
            Started.Set();
            Assert.True(Release.Wait(TimeSpan.FromSeconds(2)));
            inner.Rewrite(filePath, retainedLines, cancellationToken);
        }

        public void Dispose()
        {
            Started.Dispose();
            Release.Dispose();
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CCAutoApprove.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
