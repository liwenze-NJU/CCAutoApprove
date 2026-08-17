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
        Assert.True(document.RootElement.TryGetProperty("runtimeState", out _));
        Assert.True(document.RootElement.TryGetProperty("autoApproveEnabled", out _));
        Assert.Equal(string.Empty, error.ToString());
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
        CapturingAuditLog? auditLog = null)
    {
        return CreateApp(decision, out _, auditLog);
    }

    private CliApplication CreateApp(
        ApprovalDecision decision,
        out ClaudeHookManager hookManager,
        CapturingAuditLog? auditLog = null)
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
            new ClaudePermissionResponseWriter(),
            clock);
        var doctor = new ClaudeDoctor(
            claudeSettingsPath,
            cliPath,
            tempDirectory,
            Path.Combine(tempDirectory, "runtime.json"),
            null);

        return new CliApplication(hookCommand, hookManager, stateStore, validator, doctor);
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
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

        public Task ClearAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DeleteExpiredAsync(int retentionDays, CancellationToken cancellationToken) => Task.CompletedTask;
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
