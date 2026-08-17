using System.Text.Json;
using System.Text.Json.Nodes;
using CCAutoApprove.Infrastructure.Configuration;

namespace CCAutoApprove.Infrastructure.Claude;

public sealed class ClaudeHookManager
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
    private readonly string settingsPath;
    private readonly string managedCommand;
    private readonly AtomicFileWriter writer;

    public ClaudeHookManager(string settingsPath, string cliPath, AtomicFileWriter? writer = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(cliPath);
        this.settingsPath = Path.GetFullPath(settingsPath);
        managedCommand = BuildCommand(cliPath);
        this.writer = writer ?? new AtomicFileWriter();
    }

    public async Task InstallAsync(CancellationToken cancellationToken)
    {
        (JsonObject root, bool existed) = await LoadAsync(missingIsEmpty: true, cancellationToken);
        JsonObject hooks = GetOrCreateObject(root, "hooks");
        JsonArray permissionRequests = GetOrCreateArray(hooks, "PermissionRequest");

        bool found = false;
        bool changed = false;
        foreach (JsonNode? entryNode in permissionRequests.ToArray())
        {
            if (entryNode is not JsonObject entry || entry["hooks"] is not JsonArray entryHooks)
            {
                continue;
            }

            bool removedOwnedHook = false;
            foreach (JsonNode? hookNode in entryHooks.ToArray())
            {
                if (!IsOwnedHook(hookNode, managedCommand))
                {
                    continue;
                }

                if (!found)
                {
                    found = true;
                }
                else
                {
                    entryHooks.Remove(hookNode);
                    removedOwnedHook = true;
                    changed = true;
                }
            }

            if (removedOwnedHook && entryHooks.Count == 0)
            {
                permissionRequests.Remove(entry);
                changed = true;
            }
        }

        if (!found)
        {
            permissionRequests.Add(new JsonObject
            {
                ["matcher"] = string.Empty,
                ["hooks"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "command",
                        ["command"] = managedCommand
                    }
                }
            });
            changed = true;
        }

        if (!existed || changed)
        {
            await PersistAsync(root, existed, cancellationToken);
        }
    }

    public async Task UninstallAsync(CancellationToken cancellationToken)
    {
        (JsonObject root, bool existed) = await LoadAsync(missingIsEmpty: true, cancellationToken);
        if (!existed)
        {
            return;
        }

        if (root["hooks"] is null)
        {
            return;
        }

        if (root["hooks"] is not JsonObject hooks)
        {
            throw InvalidStructure();
        }

        if (hooks["PermissionRequest"] is null)
        {
            return;
        }

        if (hooks["PermissionRequest"] is not JsonArray permissionRequests)
        {
            throw InvalidStructure();
        }

        bool changed = false;
        foreach (JsonNode? entryNode in permissionRequests.ToArray())
        {
            if (entryNode is not JsonObject entry || entry["hooks"] is not JsonArray entryHooks)
            {
                continue;
            }

            bool removedOwnedHook = false;
            foreach (JsonNode? hookNode in entryHooks.ToArray())
            {
                if (IsOwnedHook(hookNode, managedCommand))
                {
                    entryHooks.Remove(hookNode);
                    removedOwnedHook = true;
                    changed = true;
                }
            }

            if (removedOwnedHook && entryHooks.Count == 0)
            {
                permissionRequests.Remove(entry);
            }
        }

        if (!changed)
        {
            return;
        }

        if (permissionRequests.Count == 0)
        {
            hooks.Remove("PermissionRequest");
        }

        if (hooks.Count == 0)
        {
            root.Remove("hooks");
        }

        await PersistAsync(root, originalExisted: true, cancellationToken);
    }

    public async Task<bool> IsInstalledAsync(CancellationToken cancellationToken)
    {
        try
        {
            (JsonObject root, bool existed) = await LoadAsync(missingIsEmpty: true, cancellationToken);
            return existed && EnumeratePermissionHooks(root).Any(hook => IsOwnedHook(hook, managedCommand));
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    internal string ManagedCommand => managedCommand;

    internal static string BuildCommand(string cliPath) => $"\"{Path.GetFullPath(cliPath)}\" hook";

    internal static bool IsOwnedHook(JsonNode? hook, string command) =>
        hook is JsonObject hookObject
        && TryReadString(hookObject["type"], out string? type)
        && TryReadString(hookObject["command"], out string? candidateCommand)
        && TryNormalizeManagedCommand(candidateCommand, out string? normalizedCandidate)
        && string.Equals(type, "command", StringComparison.OrdinalIgnoreCase)
        && string.Equals(normalizedCandidate, command, StringComparison.OrdinalIgnoreCase);

    internal static IEnumerable<JsonNode?> EnumeratePermissionHooks(JsonObject root)
    {
        if (root["hooks"] is not JsonObject hooks
            || hooks["PermissionRequest"] is not JsonArray permissionRequests)
        {
            yield break;
        }

        foreach (JsonNode? entry in permissionRequests)
        {
            if (entry is not JsonObject entryObject || entryObject["hooks"] is not JsonArray entryHooks)
            {
                continue;
            }

            foreach (JsonNode? hook in entryHooks)
            {
                yield return hook;
            }
        }
    }

    private async Task<(JsonObject Root, bool Existed)> LoadAsync(bool missingIsEmpty, CancellationToken cancellationToken)
    {
        try
        {
            string contents = await File.ReadAllTextAsync(settingsPath, cancellationToken);
            JsonNode? parsed = JsonNode.Parse(contents);
            return parsed is JsonObject root ? (root, true) : throw InvalidStructure();
        }
        catch (FileNotFoundException) when (missingIsEmpty)
        {
            return (new JsonObject(), false);
        }
        catch (DirectoryNotFoundException) when (missingIsEmpty)
        {
            return (new JsonObject(), false);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Claude settings JSON is malformed.", exception);
        }
    }

    private async Task PersistAsync(JsonObject root, bool originalExisted, CancellationToken cancellationToken)
    {
        string? backupPath = null;
        if (originalExisted)
        {
            backupPath = CreateBackupPath();
            File.Copy(settingsPath, backupPath, overwrite: false);
        }

        string json = root.ToJsonString(WriteOptions);
        try
        {
            await writer.WriteAllTextAsync(settingsPath, json, cancellationToken);
            string written = await File.ReadAllTextAsync(settingsPath, cancellationToken);
            if (JsonNode.Parse(written) is not JsonObject)
            {
                throw InvalidStructure();
            }
        }
        catch
        {
            if (backupPath is not null && File.Exists(backupPath))
            {
                await RestoreBackupAsync(backupPath);
            }
            else if (!originalExisted && File.Exists(settingsPath))
            {
                File.Delete(settingsPath);
            }

            throw;
        }
    }

    private string CreateBackupPath()
    {
        string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmssfff", System.Globalization.CultureInfo.InvariantCulture);
        string candidate = $"{settingsPath}.ccautoapprove-backup-{timestamp}";
        for (int suffix = 1; File.Exists(candidate); suffix++)
        {
            candidate = $"{settingsPath}.ccautoapprove-backup-{timestamp}-{suffix}";
        }

        return candidate;
    }

    private static JsonObject GetOrCreateObject(JsonObject parent, string propertyName)
    {
        if (parent[propertyName] is null)
        {
            var created = new JsonObject();
            parent[propertyName] = created;
            return created;
        }

        return parent[propertyName] as JsonObject ?? throw InvalidStructure();
    }

    private static JsonArray GetOrCreateArray(JsonObject parent, string propertyName)
    {
        if (parent[propertyName] is null)
        {
            var created = new JsonArray();
            parent[propertyName] = created;
            return created;
        }

        return parent[propertyName] as JsonArray ?? throw InvalidStructure();
    }

    private static InvalidDataException InvalidStructure() =>
        new("Claude settings JSON has an unsupported structure.");

    private static bool TryReadString(JsonNode? node, out string? value)
    {
        value = null;
        return node is JsonValue jsonValue && jsonValue.TryGetValue(out value);
    }

    private static bool TryNormalizeManagedCommand(string? command, out string? normalizedCommand)
    {
        normalizedCommand = null;
        const string suffix = "\" hook";
        if (string.IsNullOrEmpty(command)
            || !command.StartsWith('"')
            || !command.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string path = command[1..^suffix.Length];
        if (!Path.IsPathFullyQualified(path))
        {
            return false;
        }

        try
        {
            normalizedCommand = BuildCommand(path);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private async Task RestoreBackupAsync(string backupPath)
    {
        string directory = Path.GetDirectoryName(settingsPath)!;
        string restorePath = Path.Combine(directory, $"{Path.GetFileName(settingsPath)}.{Guid.NewGuid():N}.restore.tmp");
        try
        {
            await using (var source = new FileStream(backupPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, useAsync: true))
            await using (var destination = new FileStream(restorePath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, useAsync: true))
            {
                await source.CopyToAsync(destination, CancellationToken.None);
                await destination.FlushAsync(CancellationToken.None);
            }

            File.Move(restorePath, settingsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(restorePath))
            {
                File.Delete(restorePath);
            }
        }
    }
}
