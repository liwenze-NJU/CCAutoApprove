using System.Text.Json;
using CCAutoApprove.Core.Abstractions;
using CCAutoApprove.Core.Decisions;
using CCAutoApprove.Core.Models;
using CCAutoApprove.Infrastructure.Claude;
using CCAutoApprove.Infrastructure.Configuration;
using CCAutoApprove.Infrastructure.Logging;
using CCAutoApprove.Infrastructure.Windows;

namespace CCAutoApprove.Cli;

public sealed class CliApplication(
    HookCommand hookCommand,
    ClaudeHookManager hookManager,
    IRuntimeStateStore runtimeStateStore,
    RuntimeStateValidator runtimeStateValidator,
    ClaudeDoctor doctor)
{
    private static readonly TimeSpan HookTotalTimeout = TimeSpan.FromMilliseconds(1_000);

    public async Task<int> RunAsync(
        string[] args,
        Stream input,
        Stream output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        return args.FirstOrDefault()?.ToLowerInvariant() switch
        {
            "hook" => await hookCommand.ExecuteAsync(input, output, cancellationToken),
            "install" => await RunNonHookAsync(
                hookManager.InstallAsync, "install", error, cancellationToken),
            "uninstall" => await RunNonHookAsync(
                hookManager.UninstallAsync, "uninstall", error, cancellationToken),
            "status" => await RunNonHookAsync(
                token => StatusAsync(output, token), "status", error, cancellationToken),
            "doctor" => await RunNonHookAsync(
                token => DoctorAsync(output, token), "doctor", error, cancellationToken),
            _ => WriteUsageAndReturn2(error)
        };
    }

    public static async Task<int> RunProductionAsync(
        string[] args,
        Stream input,
        Stream output,
        TextWriter error,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<CliApplication>>? applicationFactory = null)
    {
        string? command = args.FirstOrDefault()?.ToLowerInvariant();
        bool isHook = string.Equals(command, "hook", StringComparison.Ordinal);
        applicationFactory ??= token => CreateProductionAsync(command, token);

        if (!isHook)
        {
            try
            {
                CliApplication application = await applicationFactory(cancellationToken);
                return await application.RunAsync(args, input, output, error, cancellationToken);
            }
            catch
            {
                await error.WriteLineAsync("CCAutoApprove: command initialization failed.");
                return 1;
            }
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(HookTotalTimeout);
        try
        {
            Task<CliApplication> initialization = Task.Factory.StartNew(
                    () => applicationFactory(timeout.Token),
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default)
                .Unwrap();
            ObserveFault(initialization);
            CliApplication application = await initialization.WaitAsync(timeout.Token);
            return await application.RunAsync(args, input, output, error, timeout.Token);
        }
        catch
        {
            return 0;
        }
    }

    private static async Task<CliApplication> CreateProductionAsync(
        string? command,
        CancellationToken cancellationToken)
    {
        var paths = new AppPaths();
        var clock = new SystemClock();
        var stateStore = new JsonRuntimeStateStore(paths.RuntimeStatePath);
        var stateValidator = new RuntimeStateValidator(clock, new WindowsProcessIdentityValidator());
        var coordinator = new ApprovalCoordinator(
            stateStore,
            stateValidator,
            new WindowsProjectMatcher(),
            new AlwaysAllowDecisionProvider());
        var settingsStore = new JsonSettingsStore(paths.SettingsPath);
        PersistentSettings settings = command switch
        {
            "hook" => await settingsStore.LoadRequiredAsync(cancellationToken),
            "status" or "uninstall" => new PersistentSettings(),
            _ => await settingsStore.LoadAsync(cancellationToken)
        };
        IAuditLog auditLog = settings.AuditDetailLevel == AuditDetailLevel.Disabled
            ? new NullAuditLog()
            : new JsonLineAuditLog(paths, settings, clock);
        var hookCommand = new HookCommand(
            new ClaudePermissionRequestParser(),
            coordinator,
            auditLog,
            new ClaudePermissionResponseWriter(),
            clock);

        string cliPath = ResolveCliPath();
        string claudeSettingsPath = ResolveClaudeSettingsPath();
        var hookManager = new ClaudeHookManager(claudeSettingsPath, cliPath);
        var doctor = new ClaudeDoctor(
            claudeSettingsPath,
            cliPath,
            paths.BasePath,
            paths.RuntimeStatePath,
            settings.SelectedProject);

        return new CliApplication(
            hookCommand,
            hookManager,
            stateStore,
            stateValidator,
            doctor);
    }

    private async Task StatusAsync(Stream output, CancellationToken cancellationToken)
    {
        bool hookInstalled = await hookManager.IsInstalledAsync(cancellationToken);
        bool hookOperational = hookInstalled
            && await doctor.IsOperationalAsync(cancellationToken);
        RuntimeState? state = await runtimeStateStore.LoadAsync(cancellationToken);
        RuntimeValidationResult? validation = state is null
            ? null
            : runtimeStateValidator.Validate(state);

        using var writer = new Utf8JsonWriter(output);
        writer.WriteStartObject();
        writer.WriteBoolean("hookInstalled", hookInstalled);
        writer.WriteBoolean("hookOperational", hookOperational);
        writer.WriteString(
            "runtimeState",
            state is null ? "Missing" : validation!.IsValid ? "Valid" : validation.ErrorCode);
        writer.WriteBoolean(
            "autoApproveEnabled",
            hookOperational && state is not null && validation!.IsValid);
        writer.WriteEndObject();
        await writer.FlushAsync(cancellationToken);
    }

    private async Task DoctorAsync(Stream output, CancellationToken cancellationToken)
    {
        IReadOnlyList<DoctorCheck> checks = await doctor.RunAsync(cancellationToken);
        using var writer = new Utf8JsonWriter(output);
        writer.WriteStartArray();
        foreach (DoctorCheck check in checks)
        {
            writer.WriteStartObject();
            writer.WriteString("code", check.Code);
            writer.WriteString("severity", check.Severity.ToString());
            writer.WriteString("message", check.Message);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        await writer.FlushAsync(cancellationToken);
    }

    private static async Task<int> RunNonHookAsync(
        Func<CancellationToken, Task> action,
        string command,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        try
        {
            await action(cancellationToken);
            return 0;
        }
        catch
        {
            await error.WriteLineAsync($"CCAutoApprove: {command} failed.");
            return 1;
        }
    }

    private static int WriteUsageAndReturn2(TextWriter error)
    {
        error.WriteLine("Usage: CCAutoApprove.Cli <hook|install|uninstall|status|doctor>");
        return 2;
    }

    private static string ResolveClaudeSettingsPath()
    {
        string? configured = Environment.GetEnvironmentVariable("CCAA_CLAUDE_SETTINGS_PATH");
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".claude",
                "settings.json")
            : configured;
    }

    private static string ResolveCliPath()
    {
        return Environment.ProcessPath
            ?? throw new InvalidOperationException("The CLI executable path is unavailable.");
    }

    private static void ObserveFault(Task task)
    {
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
