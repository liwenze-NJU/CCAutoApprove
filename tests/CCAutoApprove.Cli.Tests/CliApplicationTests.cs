using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CCAutoApprove.Cli;
using CCAutoApprove.Core.Abstractions;
using CCAutoApprove.Core.Decisions;
using CCAutoApprove.Core.Models;
using CCAutoApprove.Infrastructure.Claude;
using CCAutoApprove.Infrastructure.Configuration;

namespace CCAutoApprove.Cli.Tests;

public sealed class CliApplicationTests : IDisposable
{
    private static readonly byte[] ValidFixtureBytes = Encoding.UTF8.GetBytes(
        """
        {"session_id":"session-secret","cwd":"D:\\projects\\my-app","permission_mode":"default","hook_event_name":"PermissionRequest","tool_name":"Bash","tool_input":{"command":"echo TOP_SECRET"}}
        """);

    private readonly string tempDirectory = Path.Combine(
        Path.GetTempPath(), $"ccautoapprove-cli-{Guid.NewGuid():N}");

    [Fact]
    public async Task Hook_WhenDecisionIsAsk_WritesZeroBytesAndReturnsZero()
    {
        using var input = new MemoryStream(ValidFixtureBytes);
        using var output = new MemoryStream();
        using var error = new StringWriter();
        CliApplication app = CreateApp(ApprovalDecision.Ask("Disabled"));

        int exitCode = await app.RunAsync(
            ["hook"], input, output, error, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(0, output.Length);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task RunProductionHook_WhenInitializationBlocks_ReturnsSafelyWithinTheTotalDeadline()
    {
        using var input = new MemoryStream(ValidFixtureBytes);
        using var output = new MemoryStream();
        using var error = new StringWriter();
        using var releaseInitialization = new ManualResetEventSlim(initialState: false);
        var initializationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var initializationFinished = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        Task<CliApplication> BlockingFactory(CancellationToken cancellationToken)
        {
            initializationStarted.TrySetResult();
            try
            {
                releaseInitialization.Wait();
                return Task.FromResult(CreateApp(ApprovalDecision.Ask("unused")));
            }
            finally
            {
                initializationFinished.TrySetResult();
            }
        }

        var stopwatch = Stopwatch.StartNew();
        Task<int> runTask = Task.Run(() => CliApplication.RunProductionAsync(
            ["hook"],
            input,
            output,
            error,
            CancellationToken.None,
            BlockingFactory));

        try
        {
            await initializationStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            int exitCode = await runTask.WaitAsync(TimeSpan.FromSeconds(2));

            stopwatch.Stop();
            Assert.Equal(0, exitCode);
            Assert.Equal(0, output.Length);
            Assert.Equal(string.Empty, error.ToString());
            Assert.InRange(
                stopwatch.Elapsed,
                TimeSpan.FromMilliseconds(850),
                TimeSpan.FromMilliseconds(1_500));
        }
        finally
        {
            releaseInitialization.Set();
            await initializationFinished.Task.WaitAsync(TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public async Task Hook_WhenDecisionIsAllow_WritesExactlyOneAllowObject()
    {
        using var input = new MemoryStream(ValidFixtureBytes);
        using var output = new MemoryStream();
        using var error = new StringWriter();
        CliApplication app = CreateApp(ApprovalDecision.Allow(DecisionSource.LocalAlwaysAllow));

        int exitCode = await app.RunAsync(
            ["hook"], input, output, error, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(
            "{\"hookSpecificOutput\":{\"hookEventName\":\"PermissionRequest\",\"decision\":{\"behavior\":\"allow\"}}}",
            Encoding.UTF8.GetString(output.ToArray()));
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task Hook_WhenDecisionIsAllow_PerformsOneCompleteSynchronousOutputWrite()
    {
        using var input = new MemoryStream(ValidFixtureBytes);
        using var output = new RecordingWriteStream();
        using var error = new StringWriter();
        CliApplication app = CreateApp(ApprovalDecision.Allow(DecisionSource.LocalAlwaysAllow));

        int exitCode = await app.RunAsync(
            ["hook"], input, output, error, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(1, output.SynchronousWriteCount);
        Assert.Equal(0, output.AsynchronousWriteCount);
        Assert.Equal(
            "{\"hookSpecificOutput\":{\"hookEventName\":\"PermissionRequest\",\"decision\":{\"behavior\":\"allow\"}}}",
            Encoding.UTF8.GetString(output.ToArray()));
    }

    [Fact]
    public async Task Hook_WhenDeadlineCancelsAfterPayloadIsBuilt_WritesZeroBytes()
    {
        using var input = new MemoryStream(ValidFixtureBytes);
        using var output = new RecordingWriteStream();
        using var error = new StringWriter();
        using var cancellation = new CancellationTokenSource();
        var responseWriter = new CancelAfterWritingResponseWriter(cancellation);
        CliApplication app = CreateApp(
            ApprovalDecision.Allow(DecisionSource.LocalAlwaysAllow),
            responseWriter: responseWriter);

        int exitCode = await app.RunAsync(
            ["hook"], input, output, error, cancellation.Token);

        Assert.Equal(0, exitCode);
        Assert.True(responseWriter.PayloadWasBuilt);
        Assert.Equal(0, output.SynchronousWriteCount);
        Assert.Equal(0, output.Length);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task Hook_WhenAuditBlocksAndFaultsLater_ReturnsAtDeadlineWithoutOutput()
    {
        using var input = new MemoryStream(ValidFixtureBytes);
        using var output = new MemoryStream();
        using var error = new StringWriter();
        using var auditLog = new BlockingFaultingAuditLog();
        CliApplication app = CreateApp(
            ApprovalDecision.Allow(DecisionSource.LocalAlwaysAllow), auditLog);
        var stopwatch = Stopwatch.StartNew();
        Task<int> runTask = Task.Run(() => app.RunAsync(
            ["hook"], input, output, error, CancellationToken.None));

        try
        {
            await auditLog.Started.WaitAsync(TimeSpan.FromSeconds(1));
            int exitCode = await runTask.WaitAsync(TimeSpan.FromMilliseconds(1_700));

            stopwatch.Stop();
            Assert.Equal(0, exitCode);
            Assert.Equal(0, output.Length);
            Assert.Equal(string.Empty, error.ToString());
            Assert.InRange(
                stopwatch.Elapsed,
                TimeSpan.FromMilliseconds(850),
                TimeSpan.FromMilliseconds(1_500));
        }
        finally
        {
            auditLog.Release();
            await auditLog.Finished.WaitAsync(TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public async Task Hook_WhenInputIsMalformed_RecordsOnlySanitizedFailureAndWritesZeroBytes()
    {
        byte[] malformed = Encoding.UTF8.GetBytes(
            "{\"tool_input\":{\"command\":\"echo TOP_SECRET\"}");
        using var input = new MemoryStream(malformed);
        using var output = new MemoryStream();
        using var error = new StringWriter();
        var auditLog = new CapturingAuditLog();
        CliApplication app = CreateApp(
            ApprovalDecision.Allow(DecisionSource.LocalAlwaysAllow), auditLog);

        int exitCode = await app.RunAsync(
            ["hook"], input, output, error, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(0, output.Length);
        Assert.Equal(string.Empty, error.ToString());
        (ApprovalRequest request, ApprovalDecision decision) = Assert.Single(auditLog.Writes);
        Assert.Equal(ApprovalDecisionKind.Ask, decision.Kind);
        Assert.Equal("HookFailure", decision.Reason);
        Assert.Equal(JsonValueKind.Object, request.ToolInput.ValueKind);
        Assert.Empty(request.ToolInput.EnumerateObject());
        Assert.DoesNotContain("TOP_SECRET", JsonSerializer.Serialize(request));
    }

    [Fact]
    public async Task Hook_WhenInputNeverCompletes_StopsAtTheTotalTimeoutWithoutOutput()
    {
        using var input = new NeverCompletingStream();
        using var output = new MemoryStream();
        using var error = new StringWriter();
        CliApplication app = CreateApp(ApprovalDecision.Allow(DecisionSource.LocalAlwaysAllow));
        var stopwatch = Stopwatch.StartNew();

        int exitCode = await app.RunAsync(
            ["hook"], input, output, error, CancellationToken.None);

        stopwatch.Stop();
        Assert.Equal(0, exitCode);
        Assert.Equal(0, output.Length);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromMilliseconds(850), TimeSpan.FromMilliseconds(1_500));
    }

    [Fact]
    public async Task UnknownCommand_WritesUsageToStderrAndReturnsTwo()
    {
        using var input = new MemoryStream();
        using var output = new MemoryStream();
        using var error = new StringWriter();
        CliApplication app = CreateApp(ApprovalDecision.Ask("unused"));

        int exitCode = await app.RunAsync(
            ["surprise"], input, output, error, CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Equal(0, output.Length);
        Assert.Contains("Usage:", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("hook|install|uninstall|status|doctor", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstallAndUninstall_DelegateToTheHookManager()
    {
        using var input = new MemoryStream();
        using var output = new MemoryStream();
        using var error = new StringWriter();
        CliApplication app = CreateApp(ApprovalDecision.Ask("unused"), out ClaudeHookManager hookManager);

        int installExitCode = await app.RunAsync(
            ["install"], input, output, error, CancellationToken.None);
        bool installedAfterInstall = await hookManager.IsInstalledAsync(CancellationToken.None);

        int uninstallExitCode = await app.RunAsync(
            ["uninstall"], input, output, error, CancellationToken.None);
        bool installedAfterUninstall = await hookManager.IsInstalledAsync(CancellationToken.None);

        Assert.Equal(0, installExitCode);
        Assert.True(installedAfterInstall);
        Assert.Equal(0, uninstallExitCode);
        Assert.False(installedAfterUninstall);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task Status_DoesNotEchoRawToolInputOrSecrets()
    {
        using var input = new MemoryStream(ValidFixtureBytes);
        using var output = new MemoryStream();
        using var error = new StringWriter();
        CliApplication app = CreateApp(ApprovalDecision.Ask("unused"));

        int exitCode = await app.RunAsync(
            ["status"], input, output, error, CancellationToken.None);

        string status = Encoding.UTF8.GetString(output.ToArray());
        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("tool_input", status, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TOP_SECRET", status, StringComparison.Ordinal);
        using JsonDocument document = JsonDocument.Parse(status);
        Assert.True(document.RootElement.TryGetProperty("hookInstalled", out _));
        Assert.True(document.RootElement.TryGetProperty("hookOperational", out _));
        Assert.True(document.RootElement.TryGetProperty("runtimeState", out _));
        Assert.True(document.RootElement.TryGetProperty("autoApproveEnabled", out _));
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task Status_RuntimeValidWithoutOperationalHook_ReportsEffectiveApprovalDisabled()
    {
        using var output = new MemoryStream();
        using var error = new StringWriter();
        CliApplication app = CreateApp(ApprovalDecision.Ask("unused"));

        int exitCode = await app.RunAsync(
            ["status"], Stream.Null, output, error, CancellationToken.None);

        using JsonDocument document = JsonDocument.Parse(output.ToArray());
        Assert.Equal(0, exitCode);
        Assert.False(document.RootElement.GetProperty("hookInstalled").GetBoolean());
        Assert.False(document.RootElement.GetProperty("hookOperational").GetBoolean());
        Assert.False(document.RootElement.GetProperty("autoApproveEnabled").GetBoolean());
    }

    [Fact]
    public async Task Status_OperationalHookAndValidRuntime_ReportsEffectiveApprovalEnabled()
    {
        using var output = new MemoryStream();
        using var error = new StringWriter();
        CliApplication app = CreateApp(
            ApprovalDecision.Ask("unused"),
            out ClaudeHookManager hookManager);
        await hookManager.InstallAsync(CancellationToken.None);

        int exitCode = await app.RunAsync(
            ["status"], Stream.Null, output, error, CancellationToken.None);

        using JsonDocument document = JsonDocument.Parse(output.ToArray());
        Assert.Equal(0, exitCode);
        Assert.True(document.RootElement.GetProperty("hookInstalled").GetBoolean());
        Assert.True(document.RootElement.GetProperty("hookOperational").GetBoolean());
        Assert.True(document.RootElement.GetProperty("autoApproveEnabled").GetBoolean());
    }

    public void Dispose()
    {
        if (Directory.Exists(tempDirectory))
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private CliApplication CreateApp(
        ApprovalDecision decision,
        IAuditLog? auditLog = null,
        ClaudePermissionResponseWriter? responseWriter = null)
    {
        return CreateApp(decision, out _, auditLog, responseWriter);
    }

    private CliApplication CreateApp(
        ApprovalDecision decision,
        out ClaudeHookManager hookManager,
        IAuditLog? auditLog = null,
        ClaudePermissionResponseWriter? responseWriter = null)
    {
        Directory.CreateDirectory(tempDirectory);
        string claudeSettingsPath = Path.Combine(tempDirectory, "claude-settings.json");
        string cliPath = Path.Combine(tempDirectory, "CCAutoApprove.Cli.exe");
        File.WriteAllText(cliPath, string.Empty);
        hookManager = new ClaudeHookManager(claudeSettingsPath, cliPath);

        var clock = new FixedClock(DateTimeOffset.Parse("2026-08-17T00:00:00Z"));
        var stateStore = new StubRuntimeStateStore(new RuntimeState(
            1,
            true,
            1234,
            clock.UtcNow.AddMinutes(-1),
            Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            clock.UtcNow,
            @"D:\projects\my-app"));
        var validator = new RuntimeStateValidator(clock, new ValidProcessIdentityValidator());
        var coordinator = new ApprovalCoordinator(
            stateStore,
            validator,
            new MatchingProjectMatcher(),
            new StubDecisionProvider(decision));
        var hookCommand = new HookCommand(
            new ClaudePermissionRequestParser(),
            coordinator,
            auditLog ?? new CapturingAuditLog(),
            responseWriter ?? new ClaudePermissionResponseWriter(),
            clock);
        var doctor = new ClaudeDoctor(
            claudeSettingsPath,
            cliPath,
            tempDirectory,
            Path.Combine(tempDirectory, "runtime.json"),
            null,
            new SupportedClaudeVersionCapabilityProbe());

        return new CliApplication(hookCommand, hookManager, stateStore, validator, doctor);
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class SupportedClaudeVersionCapabilityProbe : IClaudeVersionCapabilityProbe
    {
        public Task<ClaudeVersionCapability> CheckAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ClaudeVersionCapability(true, "2.0.45"));
    }

    private sealed class StubRuntimeStateStore(RuntimeState? state) : IRuntimeStateStore
    {
        public Task<RuntimeState?> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(state);

        public Task SaveAsync(RuntimeState stateToSave, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class ValidProcessIdentityValidator : IProcessIdentityValidator
    {
        public bool IsValid(RuntimeState state) => true;
    }

    private sealed class MatchingProjectMatcher : IProjectMatcher
    {
        public bool IsMatch(string selectedProject, string requestDirectory) => true;
    }

    private sealed class StubDecisionProvider(ApprovalDecision decision) : IDecisionProvider
    {
        public Task<ApprovalDecision> DecideAsync(
            ApprovalRequest request,
            CancellationToken cancellationToken) => Task.FromResult(decision);
    }

    private sealed class CapturingAuditLog : IAuditLog
    {
        public List<(ApprovalRequest Request, ApprovalDecision Decision)> Writes { get; } = [];

        public Task WriteAsync(
            ApprovalRequest request,
            ApprovalDecision decision,
            CancellationToken cancellationToken)
        {
            Writes.Add((request, decision));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AuditRecord>> ReadRecentAsync(
            int maximumCount,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AuditRecord>>([]);

        public Task<int> CountAllowedAsync(
            DateTimeOffset startUtcInclusive,
            DateTimeOffset endUtcExclusive,
            CancellationToken cancellationToken) => Task.FromResult(0);

        public Task ClearAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DeleteExpiredAsync(int retentionDays, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class BlockingFaultingAuditLog : IAuditLog, IDisposable
    {
        private readonly ManualResetEventSlim release = new(initialState: false);
        private readonly TaskCompletionSource started = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource finished = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => started.Task;
        public Task Finished => finished.Task;

        public Task WriteAsync(
            ApprovalRequest request,
            ApprovalDecision decision,
            CancellationToken cancellationToken)
        {
            started.TrySetResult();
            try
            {
                release.Wait();
                return Task.FromException(new IOException("late audit failure"));
            }
            finally
            {
                finished.TrySetResult();
            }
        }

        public Task<IReadOnlyList<AuditRecord>> ReadRecentAsync(
            int maximumCount,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AuditRecord>>([]);

        public Task<int> CountAllowedAsync(
            DateTimeOffset startUtcInclusive,
            DateTimeOffset endUtcExclusive,
            CancellationToken cancellationToken) => Task.FromResult(0);

        public Task ClearAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DeleteExpiredAsync(int retentionDays, CancellationToken cancellationToken) => Task.CompletedTask;

        public void Release() => release.Set();

        public void Dispose() => release.Dispose();
    }

    private sealed class RecordingWriteStream : Stream
    {
        private readonly MemoryStream contents = new();

        public int SynchronousWriteCount { get; private set; }
        public int AsynchronousWriteCount { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => contents.Length;
        public override long Position
        {
            get => contents.Position;
            set => throw new NotSupportedException();
        }

        public byte[] ToArray() => contents.ToArray();

        public override void Write(byte[] buffer, int offset, int count)
        {
            SynchronousWriteCount++;
            contents.Write(buffer, offset, count);
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            AsynchronousWriteCount++;
            return contents.WriteAsync(buffer, cancellationToken);
        }

        public override Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            AsynchronousWriteCount++;
            return contents.WriteAsync(buffer, offset, count, cancellationToken);
        }

        public override void Flush() => contents.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                contents.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class CancelAfterWritingResponseWriter(
        CancellationTokenSource cancellation) : ClaudePermissionResponseWriter
    {
        public bool PayloadWasBuilt { get; private set; }

        public override async Task WriteAllowAsync(
            Stream output,
            CancellationToken cancellationToken)
        {
            await base.WriteAllowAsync(output, cancellationToken);
            PayloadWasBuilt = true;
            cancellation.Cancel();
        }
    }

    private sealed class NeverCompletingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            new(WaitForCancellationAsync(cancellationToken));

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private static async Task<int> WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
