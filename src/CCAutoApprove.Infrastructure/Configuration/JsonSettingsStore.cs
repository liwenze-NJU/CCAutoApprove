using System.IO;
using System.Text.Json;
using CCAutoApprove.Core.Abstractions;
using CCAutoApprove.Core.Models;

namespace CCAutoApprove.Infrastructure.Configuration;

public sealed class JsonSettingsStore : ISettingsStore
{
    private readonly string path;
    private readonly AtomicFileWriter writer;

    public JsonSettingsStore(string path, AtomicFileWriter? writer = null)
    {
        this.path = path;
        this.writer = writer ?? new AtomicFileWriter();
    }

    public async Task<PersistentSettings> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            string json = await File.ReadAllTextAsync(path, cancellationToken);
            using JsonDocument document = JsonDocument.Parse(json);
            PersistentSettings? settings = JsonSerializer.Deserialize<PersistentSettings>(json, JsonDefaults.Options);
            return IsValid(document.RootElement, settings)
                ? settings!
                : throw new InvalidDataException("Settings JSON is structurally invalid.");
        }
        catch (FileNotFoundException)
        {
            return new PersistentSettings();
        }
        catch (DirectoryNotFoundException)
        {
            return new PersistentSettings();
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Settings JSON is malformed.", exception);
        }
    }

    public Task SaveAsync(PersistentSettings settings, CancellationToken cancellationToken) =>
        writer.WriteAllTextAsync(path, JsonSerializer.Serialize(settings, JsonDefaults.Options), cancellationToken);

    private static bool IsValid(JsonElement document, PersistentSettings? settings) =>
        document.ValueKind == JsonValueKind.Object
        && HasProperties(document, "schemaVersion", "selectedProject", "startWithWindows", "language", "auditDetailLevel", "auditRetentionDays")
        && settings is not null
        && settings.SchemaVersion == 1
        && !string.IsNullOrWhiteSpace(settings.Language)
        && Enum.IsDefined(settings.AuditDetailLevel);

    private static bool HasProperties(JsonElement document, params string[] names) =>
        names.All(name => document.EnumerateObject().Any(property =>
            string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)));
}
