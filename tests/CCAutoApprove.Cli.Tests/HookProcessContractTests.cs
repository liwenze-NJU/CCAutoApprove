using System.Diagnostics;
using System.Text;
using CCAutoApprove.Cli;
using CCAutoApprove.Core.Models;
using CCAutoApprove.Infrastructure.Claude;
using CCAutoApprove.Infrastructure.Configuration;

namespace CCAutoApprove.Cli.Tests;

public sealed class HookProcessContractTests
{
    private const string ExpectedAllowJson =
        "{\"hookSpecificOutput\":{\"hookEventName\":\"PermissionRequest\",\"decision\":{\"behavior\":\"allow\"}}}";

    [Fact]
    public async Task HookProcess_WhenEnabledFreshAndMatching_WritesExactlyOneAllowObject()
    {
        await using var scenario = await ProcessScenario.CreateAsync(
            enabled: true,
            heartbeatAge: TimeSpan.Zero,
            projectMatches: true);

        ProcessResult result = await scenario.RunAsync(scenario.ValidFixtureBytes);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Encoding.UTF8.GetBytes(ExpectedAllowJson), result.StandardOutput);
        Assert.Empty(result.StandardError);
        Assert.True(result.Elapsed < TimeSpan.FromSeconds(2),
            $"Hook process took {result.Elapsed.TotalMilliseconds:F0} ms.");
    }

    [Theory]
    [InlineData(false, 0, true)]
    [InlineData(true, 11, true)]
    [InlineData(true, 0, false)]
    public async Task HookProcess_WhenRuntimeCannotApprove_WritesZeroStdoutBytes(
        bool enabled,
        int heartbeatAgeSeconds,
        bool projectMatches)
    {
        await using var scenario = await ProcessScenario.CreateAsync(
            enabled,
            TimeSpan.FromSeconds(heartbeatAgeSeconds),
            projectMatches);

        ProcessResult result = await scenario.RunAsync(scenario.ValidFixtureBytes);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Empty(result.StandardError);
        Assert.True(result.Elapsed < TimeSpan.FromSeconds(2),
            $"Hook process took {result.Elapsed.TotalMilliseconds:F0} ms.");
    }

    [Fact]
    public async Task HookProcess_WhenInputIsMalformed_WritesZeroBytesAndDoesNotPersistTheSecret()
    {
        await using var scenario = await ProcessScenario.CreateAsync(
            enabled: true,
            heartbeatAge: TimeSpan.Zero,
            projectMatches: true);
        byte[] malformedInput = Encoding.UTF8.GetBytes(
            "{\"tool_input\":{\"command\":\"echo TOP_SECRET_PROCESS\"}");

        ProcessResult result = await scenario.RunAsync(malformedInput);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Empty(result.StandardError);
        Assert.True(result.Elapsed < TimeSpan.FromSeconds(2),
            $"Hook process took {result.Elapsed.TotalMilliseconds:F0} ms.");
        foreach (string file in Directory.EnumerateFiles(
                     scenario.DataDirectory,
                     "*",
                     SearchOption.AllDirectories))
        {
            string contents = await File.ReadAllTextAsync(file);
            Assert.DoesNotContain("TOP_SECRET_PROCESS", contents, StringComparison.Ordinal);
            Assert.DoesNotContain("tool_input", contents, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task HookProcess_WhenAuditIsDisabled_DoesNotCreateAnAuditLog()
    {
        await using var scenario = await ProcessScenario.CreateAsync(
            enabled: true,
            heartbeatAge: TimeSpan.Zero,
            projectMatches: true,
            auditDetailLevel: AuditDetailLevel.Disabled);

        ProcessResult result = await scenario.RunAsync(scenario.ValidFixtureBytes);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Encoding.UTF8.GetBytes(ExpectedAllowJson), result.StandardOutput);
        Assert.Empty(result.StandardError);
        Assert.False(Directory.Exists(Path.Combine(scenario.DataDirectory, "logs")));
    }

    [Theory]
    [InlineData(SettingsFileState.Missing)]
    [InlineData(SettingsFileState.Malformed)]
    [InlineData(SettingsFileState.Unreadable)]
    public async Task HookProcess_WhenSettingsCannotBeStrictlyLoaded_AsksWithoutCreatingAudit(
        SettingsFileState settingsFileState)
    {
        await using var scenario = await ProcessScenario.CreateAsync(
            enabled: true,
            heartbeatAge: TimeSpan.Zero,
            projectMatches: true);
        await scenario.SetSettingsFileStateAsync(settingsFileState);

        ProcessResult result = await scenario.RunAsync(scenario.ValidFixtureBytes);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Empty(result.StandardError);
        Assert.False(Directory.Exists(Path.Combine(scenario.DataDirectory, "logs")));
    }

    [Fact]
    public async Task HookProcess_WhenPermissionSuggestionsHasWrongType_WritesZeroBytes()
    {
        await using var scenario = await ProcessScenario.CreateAsync(
            enabled: true,
            heartbeatAge: TimeSpan.Zero,
            projectMatches: true);
        byte[] input = Encoding.UTF8.GetBytes(
            "{\"session_id\":\"process-session\",\"cwd\":"
            + System.Text.Json.JsonSerializer.Serialize(scenario.ProjectDirectory)
            + ",\"permission_mode\":\"default\",\"hook_event_name\":\"PermissionRequest\","
            + "\"tool_name\":\"Bash\",\"tool_input\":{},\"permission_suggestions\":{}}");

        ProcessResult result = await scenario.RunAsync(input);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Empty(result.StandardError);
    }

    [Theory]
    [InlineData(SettingsFileState.Missing)]
    [InlineData(SettingsFileState.Malformed)]
    [InlineData(SettingsFileState.Unreadable)]
    public async Task StatusProcess_WhenDecisionSettingsCannotBeLoaded_RemainsAvailableForRepairCommands(
        SettingsFileState settingsFileState)
    {
        await using var scenario = await ProcessScenario.CreateAsync(
            enabled: true,
            heartbeatAge: TimeSpan.Zero,
            projectMatches: true);
        await scenario.SetSettingsFileStateAsync(settingsFileState);

        ProcessResult result = await scenario.RunAsync([], "status");

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StandardError);
        using System.Text.Json.JsonDocument status =
            System.Text.Json.JsonDocument.Parse(result.StandardOutput);
        Assert.False(status.RootElement.GetProperty("hookInstalled").GetBoolean());
        Assert.False(status.RootElement.GetProperty("autoApproveEnabled").GetBoolean());
    }

    [Theory]
    [InlineData(SettingsFileState.Malformed)]
    [InlineData(SettingsFileState.Unreadable)]
    public async Task UninstallProcess_WhenDecisionSettingsCannotBeLoaded_RemovesTheOwnedHook(
        SettingsFileState settingsFileState)
    {
        await using var scenario = await ProcessScenario.CreateAsync(
            enabled: true,
            heartbeatAge: TimeSpan.Zero,
            projectMatches: true);
        await scenario.InstallHookAsync();
        Assert.True(await scenario.IsHookInstalledAsync());
        await scenario.SetSettingsFileStateAsync(settingsFileState);

        ProcessResult result = await scenario.RunAsync([], "uninstall");

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Empty(result.StandardError);
        Assert.False(await scenario.IsHookInstalledAsync());
    }

    public enum SettingsFileState
    {
        Missing,
        Malformed,
        Unreadable
    }

    private sealed class ProcessScenario : IAsyncDisposable
    {
        private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(1);

        private readonly string rootDirectory;
        private readonly string claudeSettingsPath;
        private readonly string projectDirectory;

        private ProcessScenario(
            string rootDirectory,
            string dataDirectory,
            string claudeSettingsPath,
            string projectDirectory)
        {
            this.rootDirectory = rootDirectory;
            DataDirectory = dataDirectory;
            this.claudeSettingsPath = claudeSettingsPath;
            this.projectDirectory = projectDirectory;
        }

        public string DataDirectory { get; }
        public string ProjectDirectory => projectDirectory;

        public byte[] ValidFixtureBytes => Encoding.UTF8.GetBytes(
            "{\"session_id\":\"process-session\",\"cwd\":"
            + JsonString(projectDirectory)
            + ",\"permission_mode\":\"default\",\"hook_event_name\":\"PermissionRequest\","
            + "\"tool_name\":\"Bash\",\"tool_input\":{\"command\":\"echo PROCESS_SECRET\"}}");

        public static async Task<ProcessScenario> CreateAsync(
            bool enabled,
            TimeSpan heartbeatAge,
            bool projectMatches,
            AuditDetailLevel auditDetailLevel = AuditDetailLevel.PrivacySafe)
        {
            string root = Path.Combine(
                Path.GetTempPath(), $"ccautoapprove-process-{Guid.NewGuid():N}");
            string data = Path.Combine(root, "data");
            string claudeSettings = Path.Combine(root, "claude", "settings.json");
            string project = Path.Combine(root, "project");
            string selectedProject = projectMatches
                ? project
                : Path.Combine(root, "different-project");
            Directory.CreateDirectory(data);
            Directory.CreateDirectory(project);
            Directory.CreateDirectory(selectedProject);

            using Process current = Process.GetCurrentProcess();
            var state = new RuntimeState(
                1,
                enabled,
                current.Id,
                current.StartTime.ToUniversalTime(),
                Guid.NewGuid(),
                DateTimeOffset.UtcNow.Subtract(heartbeatAge),
                selectedProject);
            await new JsonRuntimeStateStore(Path.Combine(data, "runtime.json"))
                .SaveAsync(state, CancellationToken.None);
            await new JsonSettingsStore(Path.Combine(data, "settings.json"))
                .SaveAsync(
                    new PersistentSettings(AuditDetailLevel: auditDetailLevel),
                    CancellationToken.None);

            return new ProcessScenario(root, data, claudeSettings, project);
        }

        public async Task<ProcessResult> RunAsync(
            byte[] standardInput,
            string arguments = "hook")
        {
            string cliPath = GetCliPath();
            Assert.True(File.Exists(cliPath), $"CLI apphost was not found at {cliPath}.");

            var startInfo = new ProcessStartInfo
            {
                FileName = cliPath,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = rootDirectory
            };
            startInfo.Environment["CCAA_DATA_DIR"] = DataDirectory;
            startInfo.Environment["CCAA_CLAUDE_SETTINGS_PATH"] = claudeSettingsPath;

            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The CLI process could not be started.");
            var standardOutput = new MemoryStream();
            var standardError = new MemoryStream();
            Task outputDrain = process.StandardOutput.BaseStream.CopyToAsync(standardOutput);
            Task errorDrain = process.StandardError.BaseStream.CopyToAsync(standardError);
            var stopwatch = Stopwatch.StartNew();

            try
            {
                try
                {
                    await process.StandardInput.BaseStream.WriteAsync(standardInput);
                    await process.StandardInput.BaseStream.FlushAsync();
                }
                catch (IOException)
                {
                    // Some fail-closed paths intentionally exit before reading stdin.
                    // Only tolerate the resulting closed pipe after confirming that
                    // the child process really has exited.
                    using var exitConfirmation = new CancellationTokenSource(ProcessTimeout);
                    await process.WaitForExitAsync(exitConfirmation.Token);
                }
                finally
                {
                    process.StandardInput.Close();
                }

                using var timeout = new CancellationTokenSource(ProcessTimeout);
                await process.WaitForExitAsync(timeout.Token);
                await Task.WhenAll(outputDrain, errorDrain).WaitAsync(timeout.Token);
            }
            catch
            {
                await KillAndWaitAsync(process);
                throw;
            }
            finally
            {
                stopwatch.Stop();
                if (!process.HasExited)
                {
                    await KillAndWaitAsync(process);
                }
            }

            return new ProcessResult(
                process.ExitCode,
                standardOutput.ToArray(),
                standardError.ToArray(),
                stopwatch.Elapsed);
        }

        public Task InstallHookAsync() =>
            CreateHookManager().InstallAsync(CancellationToken.None);

        public Task<bool> IsHookInstalledAsync() =>
            CreateHookManager().IsInstalledAsync(CancellationToken.None);

        public async Task SetSettingsFileStateAsync(SettingsFileState state)
        {
            string path = Path.Combine(DataDirectory, "settings.json");
            File.Delete(path);
            switch (state)
            {
                case SettingsFileState.Missing:
                    return;
                case SettingsFileState.Malformed:
                    await File.WriteAllTextAsync(path, "{\"auditDetailLevel\":");
                    return;
                case SettingsFileState.Unreadable:
                    Directory.CreateDirectory(path);
                    return;
                default:
                    throw new ArgumentOutOfRangeException(nameof(state));
            }
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(rootDirectory))
            {
                Directory.Delete(rootDirectory, recursive: true);
            }

            return ValueTask.CompletedTask;
        }

        private static async Task KillAndWaitAsync(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }

            try
            {
                using var cleanup = new CancellationTokenSource(CleanupTimeout);
                await process.WaitForExitAsync(cleanup.Token);
            }
            catch (OperationCanceledException)
            {
            }
        }

        private static string JsonString(string value) =>
            System.Text.Json.JsonSerializer.Serialize(value);

        private ClaudeHookManager CreateHookManager() =>
            new(claudeSettingsPath, GetCliPath());

        private static string GetCliPath()
        {
            string assemblyPath = typeof(CliApplication).Assembly.Location;
            return Path.ChangeExtension(assemblyPath, ".exe");
        }
    }

    private sealed record ProcessResult(
        int ExitCode,
        byte[] StandardOutput,
        byte[] StandardError,
        TimeSpan Elapsed);
}
