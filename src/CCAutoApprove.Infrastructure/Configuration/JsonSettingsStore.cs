using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using CCAutoApprove.Core.Abstractions;
using CCAutoApprove.Core.Models;

namespace CCAutoApprove.Infrastructure.Configuration;

public sealed class JsonSettingsStore : ISettingsStore
{
    internal const int MinimumAuditRetentionDays = 1;
    internal const int MaximumAuditRetentionDays = 3_650;
    private static readonly JsonSerializerOptions SettingsJsonOptions = CreateSettingsJsonOptions();
    private readonly string path;
    private readonly AtomicFileWriter writer;
    private readonly SemaphoreSlim updateLock = new(1, 1);

    public JsonSettingsStore(string path, AtomicFileWriter? writer = null)
    {
        this.path = path;
        this.writer = writer ?? new AtomicFileWriter();
    }

    public Task<PersistentSettings> LoadAsync(CancellationToken cancellationToken) =>
        LoadCoreAsync(useDefaultsWhenMissing: true, cancellationToken);

    public Task<PersistentSettings> LoadRequiredAsync(CancellationToken cancellationToken) =>
        LoadCoreAsync(useDefaultsWhenMissing: false, cancellationToken);

    private async Task<PersistentSettings> LoadCoreAsync(
        bool useDefaultsWhenMissing,
        CancellationToken cancellationToken)
    {
        try
        {
            string json = await File.ReadAllTextAsync(path, cancellationToken);
            using JsonDocument document = JsonDocument.Parse(json);
            PersistentSettings? settings = JsonSerializer.Deserialize<PersistentSettings>(
                json,
                SettingsJsonOptions);
            return IsValid(document.RootElement, settings)
                ? settings!
                : throw new InvalidDataException("Settings JSON is structurally invalid.");
        }
        catch (FileNotFoundException) when (useDefaultsWhenMissing)
        {
            return new PersistentSettings();
        }
        catch (DirectoryNotFoundException) when (useDefaultsWhenMissing)
        {
            return new PersistentSettings();
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Settings JSON is malformed.", exception);
        }
    }

    public async Task SaveAsync(PersistentSettings settings, CancellationToken cancellationToken)
    {
        await updateLock.WaitAsync(cancellationToken);
        try
        {
            await SaveCoreAsync(settings, cancellationToken);
        }
        finally
        {
            updateLock.Release();
        }
    }

    public async Task<PersistentSettings> UpdateAsync(
        Func<PersistentSettings, PersistentSettings> update,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        await updateLock.WaitAsync(cancellationToken);
        try
        {
            PersistentSettings current = await LoadAsync(cancellationToken);
            PersistentSettings updated = update(current)
                ?? throw new InvalidOperationException("The settings update returned null.");
            await SaveCoreAsync(updated, cancellationToken);
            return updated;
        }
        finally
        {
            updateLock.Release();
        }
    }

    private Task SaveCoreAsync(
        PersistentSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!IsValid(settings))
        {
            throw new ArgumentException("Settings contain invalid values.", nameof(settings));
        }

        return writer.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(settings, SettingsJsonOptions),
            cancellationToken);
    }

    private static bool IsValid(JsonElement document, PersistentSettings? settings) =>
        document.ValueKind == JsonValueKind.Object
        && HasProperties(document, "schemaVersion", "selectedProject", "startWithWindows", "language", "auditDetailLevel", "auditRetentionDays")
        && settings is not null
        && IsValid(settings);

    private static bool IsValid(PersistentSettings settings) =>
        settings.SchemaVersion == 1
        && !string.IsNullOrWhiteSpace(settings.Language)
        && Enum.IsDefined(settings.AuditDetailLevel)
        && settings.AuditRetentionDays is >= MinimumAuditRetentionDays and <= MaximumAuditRetentionDays
        && IsValidSelectedProject(settings.SelectedProject);

    private static bool IsValidSelectedProject(string? selectedProject)
    {
        if (selectedProject is null)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(selectedProject))
        {
            return false;
        }

        try
        {
            return Path.IsPathFullyQualified(selectedProject);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static JsonSerializerOptions CreateSettingsJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonDefaults.Options);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static bool HasProperties(JsonElement document, params string[] names) =>
        names.All(name => document.EnumerateObject().Any(property =>
            string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)));
}
