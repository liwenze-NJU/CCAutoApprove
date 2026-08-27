using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;
using CCAutoApprove.Infrastructure.Configuration;

namespace CCAutoApprove.Infrastructure.Claude;

public sealed class ClaudeHookManager
{
    private const int MaximumMergeAttempts = 8;
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
    private readonly string settingsPath;
    private readonly string managedCommand;
    private readonly IClaudeSettingsCommitter committer;
    private readonly SnapshotClaudeSettingsCommitter rollbackCommitter = new();
    private readonly Semaphore mutationSemaphore;

    public ClaudeHookManager(string settingsPath, string cliPath, AtomicFileWriter? writer = null)
        : this(settingsPath, cliPath, new SnapshotClaudeSettingsCommitter(writer))
    {
    }

    internal ClaudeHookManager(
        string settingsPath,
        string cliPath,
        IClaudeSettingsCommitter committer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(cliPath);
        this.settingsPath = Path.GetFullPath(settingsPath);
        managedCommand = BuildCommand(cliPath);
        this.committer = committer ?? throw new ArgumentNullException(nameof(committer));
        mutationSemaphore = new Semaphore(1, 1, BuildMutationSemaphoreName(this.settingsPath));
    }

    public async Task InstallAsync(CancellationToken cancellationToken)
    {
        using IDisposable lease = await AcquireMutationLeaseAsync(cancellationToken);
        for (int attempt = 0; attempt < MaximumMergeAttempts; attempt++)
        {
            SettingsSnapshot snapshot = await LoadAsync(missingIsEmpty: true, cancellationToken);
            bool changed = MergeInstall(snapshot.Root);
            if (snapshot.Existed && !changed)
            {
                return;
            }

            if (await TryPersistAsync(snapshot.Root, snapshot, cancellationToken))
            {
                return;
            }
        }

        throw new IOException("Claude settings changed repeatedly while the Hook update was being committed.");
    }

    private bool MergeInstall(JsonObject root)
    {
        EnsureSupportedHookStructure(root);
        JsonObject hooks = GetOrCreateObject(root, "hooks");
        JsonArray permissionRequests = GetOrCreateArray(hooks, "PermissionRequest");

        bool found = false;
        bool changed = false;
        foreach (JsonNode? entryNode in permissionRequests.ToArray())
        {
            if (entryNode is not JsonObject entry || entry["hooks"] is not JsonArray entryHooks)
            {
                throw InvalidStructure();
            }

            bool isManagedWrapper = TryReadString(entry["matcher"], out string? matcher)
                && string.Equals(matcher, string.Empty, StringComparison.Ordinal);
            bool removedOwnedHook = false;
            foreach (JsonNode? hookNode in entryHooks.ToArray())
            {
                if (!isManagedWrapper || !IsOwnedHook(hookNode, managedCommand))
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

        return changed;
    }

    public async Task UninstallAsync(CancellationToken cancellationToken)
    {
        using IDisposable lease = await AcquireMutationLeaseAsync(cancellationToken);
        for (int attempt = 0; attempt < MaximumMergeAttempts; attempt++)
        {
            SettingsSnapshot snapshot = await LoadAsync(missingIsEmpty: true, cancellationToken);
            if (!snapshot.Existed || !MergeUninstall(snapshot.Root))
            {
                return;
            }

            if (await TryPersistAsync(snapshot.Root, snapshot, cancellationToken))
            {
                return;
            }
        }

        throw new IOException("Claude settings changed repeatedly while the Hook update was being committed.");
    }

    private bool MergeUninstall(JsonObject root)
    {
        EnsureSupportedHookStructure(root);
        if (root["hooks"] is null)
        {
            return false;
        }

        if (root["hooks"] is not JsonObject hooks)
        {
            throw InvalidStructure();
        }

        if (hooks["PermissionRequest"] is null)
        {
            return false;
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
                throw InvalidStructure();
            }

            bool isManagedWrapper = TryReadString(entry["matcher"], out string? matcher)
                && string.Equals(matcher, string.Empty, StringComparison.Ordinal);
            bool removedOwnedHook = false;
            foreach (JsonNode? hookNode in entryHooks.ToArray())
            {
                if (isManagedWrapper && IsOwnedHook(hookNode, managedCommand))
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
            return false;
        }

        if (permissionRequests.Count == 0)
        {
            hooks.Remove("PermissionRequest");
        }

        if (hooks.Count == 0)
        {
            root.Remove("hooks");
        }

        return true;
    }

    public async Task<bool> IsInstalledAsync(CancellationToken cancellationToken)
    {
        try
        {
            SettingsSnapshot snapshot = await LoadAsync(missingIsEmpty: true, cancellationToken);
            EnsureSupportedHookStructure(snapshot.Root);
            return snapshot.Existed
                && EnumerateManagedWrapperHooks(snapshot.Root).Any(hook => IsOwnedHook(hook, managedCommand));
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

    internal static IEnumerable<JsonNode?> EnumerateManagedWrapperHooks(JsonObject root)
    {
        if (!TryGetProperty(root, "hooks", out JsonNode? hooksNode)
            || hooksNode is not JsonObject hooks
            || !TryGetProperty(hooks, "PermissionRequest", out JsonNode? permissionRequestNode)
            || permissionRequestNode is not JsonArray permissionRequests)
        {
            yield break;
        }

        foreach (JsonNode? entry in permissionRequests)
        {
            if (entry is not JsonObject entryObject
                || !TryReadString(entryObject["matcher"], out string? matcher)
                || !string.Equals(matcher, string.Empty, StringComparison.Ordinal)
                || entryObject["hooks"] is not JsonArray entryHooks)
            {
                continue;
            }

            foreach (JsonNode? hook in entryHooks)
            {
                yield return hook;
            }
        }
    }

    private async Task<SettingsSnapshot> LoadAsync(bool missingIsEmpty, CancellationToken cancellationToken)
    {
        try
        {
            byte[] source = await File.ReadAllBytesAsync(settingsPath, cancellationToken);
            JsonNode? parsed = JsonNode.Parse(source);
            return parsed is JsonObject root
                ? new SettingsSnapshot(root, Existed: true, source)
                : throw InvalidStructure();
        }
        catch (FileNotFoundException) when (missingIsEmpty)
        {
            return new SettingsSnapshot(new JsonObject(), Existed: false, Source: null);
        }
        catch (DirectoryNotFoundException) when (missingIsEmpty)
        {
            return new SettingsSnapshot(new JsonObject(), Existed: false, Source: null);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Claude settings JSON is malformed.", exception);
        }
    }

    private async Task<bool> TryPersistAsync(
        JsonObject root,
        SettingsSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        string? backupPath = null;
        if (snapshot.Existed)
        {
            backupPath = await CreateBackupAsync(snapshot.Source!, cancellationToken);
        }

        string json = root.ToJsonString(WriteOptions);
        byte[] attemptSource = Encoding.UTF8.GetBytes(json);
        try
        {
            bool committed = await committer.TryCommitAsync(
                settingsPath,
                snapshot.Source,
                json,
                cancellationToken);
            if (!committed)
            {
                DeleteBackup(backupPath);
                return false;
            }

            byte[] currentSource = await File.ReadAllBytesAsync(settingsPath, cancellationToken);
            JsonNode? written = JsonNode.Parse(currentSource);
            if (written is not JsonObject writtenRoot || !JsonNode.DeepEquals(root, writtenRoot))
            {
                return false;
            }

            return true;
        }
        catch
        {
            if (backupPath is not null && File.Exists(backupPath))
            {
                byte[] backupSource = await File.ReadAllBytesAsync(
                    backupPath,
                    CancellationToken.None);
                await rollbackCommitter.TryCommitBytesAsync(
                    settingsPath,
                    attemptSource,
                    backupSource,
                    CancellationToken.None);
            }
            else if (!snapshot.Existed)
            {
                await rollbackCommitter.TryDeleteAsync(
                    settingsPath,
                    attemptSource,
                    CancellationToken.None);
            }

            throw;
        }
    }

    private async Task<string> CreateBackupAsync(byte[] source, CancellationToken cancellationToken)
    {
        string backupPath = CreateBackupPath();
        await using var backup = new FileStream(
            backupPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await backup.WriteAsync(source, cancellationToken);
        await backup.FlushAsync(cancellationToken);
        return backupPath;
    }

    private static void DeleteBackup(string? backupPath)
    {
        if (backupPath is not null && File.Exists(backupPath))
        {
            File.Delete(backupPath);
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

    private async Task<IDisposable> AcquireMutationLeaseAsync(CancellationToken cancellationToken)
    {
        await Task.Run(() =>
        {
            int signaled = WaitHandle.WaitAny([mutationSemaphore, cancellationToken.WaitHandle]);
            if (signaled != 0)
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }, CancellationToken.None);
        return new SemaphoreLease(mutationSemaphore);
    }

    private static string BuildMutationSemaphoreName(string path)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant()));
        return $"Local\\CCAutoApprove.ClaudeSettings.{Convert.ToHexString(hash)}";
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

    internal static bool HasSupportedHookStructure(JsonObject root)
    {
        if (!TryGetProperty(root, "hooks", out JsonNode? hooksNode))
        {
            return true;
        }

        if (hooksNode is not JsonObject hooks)
        {
            return false;
        }

        if (!TryGetProperty(hooks, "PermissionRequest", out JsonNode? permissionRequestNode))
        {
            return true;
        }

        if (permissionRequestNode is not JsonArray permissionRequests)
        {
            return false;
        }

        foreach (JsonNode? entryNode in permissionRequests)
        {
            if (entryNode is not JsonObject entry
                || !TryGetProperty(entry, "hooks", out JsonNode? entryHooksNode)
                || entryHooksNode is not JsonArray entryHooks)
            {
                return false;
            }

            bool hasMatcher = TryGetProperty(entry, "matcher", out JsonNode? matcherNode);
            if (hasMatcher && !TryReadString(matcherNode, out _))
            {
                return false;
            }

            foreach (JsonNode? hookNode in entryHooks)
            {
                if (hookNode is not JsonObject hook
                    || !TryGetRequiredString(hook, "type", out string? type))
                {
                    return false;
                }

                bool hasCommand = TryGetProperty(hook, "command", out JsonNode? commandNode);
                if ((hasCommand && !TryReadString(commandNode, out _))
                    || (string.Equals(type, "command", StringComparison.OrdinalIgnoreCase) && !hasCommand))
                {
                    return false;
                }
            }
        }

        return true;
    }

    internal static bool TryReadString(JsonNode? node, out string? value)
    {
        value = null;
        return node is JsonValue jsonValue && jsonValue.TryGetValue(out value);
    }

    private static void EnsureSupportedHookStructure(JsonObject root)
    {
        if (!HasSupportedHookStructure(root))
        {
            throw InvalidStructure();
        }
    }

    private static bool TryGetRequiredString(JsonObject parent, string propertyName, out string? value)
    {
        value = null;
        return TryGetProperty(parent, propertyName, out JsonNode? node)
            && TryReadString(node, out value);
    }

    private static bool TryGetProperty(JsonObject parent, string propertyName, out JsonNode? value) =>
        parent.TryGetPropertyValue(propertyName, out value);

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

    private readonly record struct SettingsSnapshot(JsonObject Root, bool Existed, byte[]? Source);

    private sealed class SemaphoreLease(Semaphore semaphore) : IDisposable
    {
        private Semaphore? semaphore = semaphore;

        public void Dispose()
        {
            Interlocked.Exchange(ref semaphore, null)?.Release();
        }
    }
}
