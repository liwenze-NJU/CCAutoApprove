using System.Text.Json;
using System.Text.Json.Nodes;
using CCAutoApprove.Core.Abstractions;
using CCAutoApprove.Infrastructure.Configuration;

namespace CCAutoApprove.Infrastructure.Claude;

public sealed class ClaudeDoctor : IHookHealthService
{
    private static readonly string[] CheckCodes =
    [
        "ClaudeSettingsReadable",
        "ClaudeSettingsValid",
        "HookInstalled",
        "HookNotDuplicated",
        "HookExecutableExists",
        "HookCommandPathMatches",
        "HooksNotDisabled",
        "DataDirectoryWritable",
        "RuntimeStateValid",
        "SelectedProjectExists"
    ];

    private readonly string claudeSettingsPath;
    private readonly string cliPath;
    private readonly string dataDirectoryPath;
    private readonly string runtimeStatePath;
    private readonly string? selectedProjectPath;
    private readonly string managedCommand;

    public ClaudeDoctor(
        string claudeSettingsPath,
        string cliPath,
        string dataDirectoryPath,
        string runtimeStatePath,
        string? selectedProjectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claudeSettingsPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(cliPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeStatePath);
        this.claudeSettingsPath = Path.GetFullPath(claudeSettingsPath);
        this.cliPath = Path.GetFullPath(cliPath);
        this.dataDirectoryPath = Path.GetFullPath(dataDirectoryPath);
        this.runtimeStatePath = Path.GetFullPath(runtimeStatePath);
        this.selectedProjectPath = string.IsNullOrWhiteSpace(selectedProjectPath)
            ? null
            : Path.GetFullPath(selectedProjectPath);
        managedCommand = ClaudeHookManager.BuildCommand(cliPath);
    }

    public async Task<IReadOnlyList<DoctorCheck>> RunAsync(CancellationToken cancellationToken)
    {
        var checks = new List<DoctorCheck>(CheckCodes.Length);
        JsonObject? settings = await ReadClaudeSettingsAsync(checks, cancellationToken);
        AddHookChecks(checks, settings);
        checks.Add(File.Exists(cliPath)
            ? Pass("HookExecutableExists", "Hook executable is present.")
            : Error("HookExecutableExists", "Hook executable is missing."));
        AddCommandPathAndDisabledChecks(checks, settings);
        checks.Add(await CheckDataDirectoryAsync(cancellationToken));
        checks.Add(await CheckRuntimeStateAsync(cancellationToken));
        checks.Add(selectedProjectPath is not null && Directory.Exists(selectedProjectPath)
            ? Pass("SelectedProjectExists", "Selected project exists.")
            : Warning("SelectedProjectExists", "No existing selected project was found."));
        return checks;
    }

    public async Task<bool> IsOperationalAsync(CancellationToken cancellationToken) =>
        !(await RunAsync(cancellationToken)).Any(check => check.Severity == DoctorSeverity.Error);

    private async Task<JsonObject?> ReadClaudeSettingsAsync(
        ICollection<DoctorCheck> checks,
        CancellationToken cancellationToken)
    {
        string contents;
        try
        {
            contents = await File.ReadAllTextAsync(claudeSettingsPath, cancellationToken);
            checks.Add(Pass("ClaudeSettingsReadable", "Claude settings are readable."));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            checks.Add(Error("ClaudeSettingsReadable", "Claude settings could not be read."));
            checks.Add(Error("ClaudeSettingsValid", "Claude settings could not be validated."));
            return null;
        }

        try
        {
            JsonNode? parsed = JsonNode.Parse(contents);
            if (parsed is not JsonObject settings)
            {
                checks.Add(Error("ClaudeSettingsValid", "Claude settings must contain a JSON object."));
                return null;
            }

            if (!HasSupportedHookStructure(settings))
            {
                checks.Add(Error("ClaudeSettingsValid", "Claude settings contain an unsupported Hook structure."));
                return null;
            }

            checks.Add(Pass("ClaudeSettingsValid", "Claude settings contain valid JSON."));
            return settings;
        }
        catch (JsonException)
        {
            checks.Add(Error("ClaudeSettingsValid", "Claude settings contain malformed JSON."));
            return null;
        }
    }

    private void AddHookChecks(ICollection<DoctorCheck> checks, JsonObject? settings)
    {
        JsonNode?[] candidates = settings is null
            ? []
            : ClaudeHookManager.EnumeratePermissionHooks(settings).Where(IsCCAutoApproveHookCandidate).ToArray();
        checks.Add(candidates.Length > 0
            ? Pass("HookInstalled", "CCAutoApprove PermissionRequest Hook is installed.")
            : Error("HookInstalled", "CCAutoApprove PermissionRequest Hook is not installed."));
        checks.Add(candidates.Length == 1
            ? Pass("HookNotDuplicated", "Exactly one CCAutoApprove Hook is configured.")
            : Error("HookNotDuplicated", candidates.Length == 0
                ? "No CCAutoApprove Hook is configured."
                : "More than one CCAutoApprove Hook is configured."));
    }

    private void AddCommandPathAndDisabledChecks(ICollection<DoctorCheck> checks, JsonObject? settings)
    {
        bool commandMatches = settings is not null
            && ClaudeHookManager.EnumeratePermissionHooks(settings)
                .Any(hook => ClaudeHookManager.IsOwnedHook(hook, managedCommand));
        checks.Add(commandMatches
            ? Pass("HookCommandPathMatches", "Hook command references the current executable.")
            : Error("HookCommandPathMatches", "Hook command does not reference the current executable."));

        bool disabled = settings?["disableAllHooks"] is JsonValue value
            && value.TryGetValue(out bool isDisabled)
            && isDisabled;
        checks.Add(!disabled && settings is not null
            ? Pass("HooksNotDisabled", "Claude Hooks are enabled.")
            : Error("HooksNotDisabled", settings is null
                ? "Claude Hook status could not be determined."
                : "Claude Hooks are disabled."));
    }

    private async Task<DoctorCheck> CheckDataDirectoryAsync(CancellationToken cancellationToken)
    {
        string probePath = Path.Combine(dataDirectoryPath, $".ccautoapprove-doctor-{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(dataDirectoryPath);
            await File.WriteAllTextAsync(probePath, string.Empty, cancellationToken);
            _ = await File.ReadAllTextAsync(probePath, cancellationToken);
            File.Delete(probePath);
            return Pass("DataDirectoryWritable", "Data directory is readable and writable.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Error("DataDirectoryWritable", "Data directory is not readable and writable.");
        }
        finally
        {
            if (File.Exists(probePath))
            {
                try
                {
                    File.Delete(probePath);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }

    private async Task<DoctorCheck> CheckRuntimeStateAsync(CancellationToken cancellationToken)
    {
        try
        {
            var store = new JsonRuntimeStateStore(runtimeStatePath);
            return await store.LoadAsync(cancellationToken) is not null
                ? Pass("RuntimeStateValid", "Runtime state is valid.")
                : Warning("RuntimeStateValid", "Runtime state is missing or invalid.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Warning("RuntimeStateValid", "Runtime state could not be read.");
        }
    }

    private static bool IsCCAutoApproveHookCandidate(JsonNode? node)
    {
        if (node is not JsonObject hook
            || !string.Equals((string?)hook["type"], "command", StringComparison.OrdinalIgnoreCase)
            || hook["command"] is not JsonValue commandValue
            || !commandValue.TryGetValue(out string? command)
            || string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        const string suffix = "\" hook";
        if (!command.StartsWith('"') || !command.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string path = command[1..^suffix.Length];
        return string.Equals(Path.GetFileName(path), "CCAutoApprove.Cli.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasSupportedHookStructure(JsonObject settings)
    {
        if (settings["hooks"] is null)
        {
            return true;
        }

        if (settings["hooks"] is not JsonObject hooks)
        {
            return false;
        }

        if (hooks["PermissionRequest"] is null)
        {
            return true;
        }

        if (hooks["PermissionRequest"] is not JsonArray permissionRequests)
        {
            return false;
        }

        return permissionRequests.All(entry =>
            entry is JsonObject entryObject
            && entryObject["hooks"] is JsonArray);
    }

    private static DoctorCheck Pass(string code, string message) => new(code, DoctorSeverity.Pass, message);
    private static DoctorCheck Warning(string code, string message) => new(code, DoctorSeverity.Warning, message);
    private static DoctorCheck Error(string code, string message) => new(code, DoctorSeverity.Error, message);
}
