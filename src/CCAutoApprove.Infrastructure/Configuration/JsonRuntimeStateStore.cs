using System.Text.Json;
using CCAutoApprove.Core.Abstractions;
using CCAutoApprove.Core.Models;

namespace CCAutoApprove.Infrastructure.Configuration;

public sealed class JsonRuntimeStateStore : IRuntimeStateStore
{
    private readonly string path;
    private readonly AtomicFileWriter writer;

    public JsonRuntimeStateStore(string path, AtomicFileWriter? writer = null)
    {
        this.path = path;
        this.writer = writer ?? new AtomicFileWriter();
    }

    public async Task<RuntimeState?> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            string json = await ReadJsonAsync(cancellationToken);
            using JsonDocument document = JsonDocument.Parse(json);
            RuntimeState? state = JsonSerializer.Deserialize<RuntimeState>(json, JsonDefaults.Options);
            return IsValid(document.RootElement, state) ? state : null;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public Task SaveAsync(RuntimeState state, CancellationToken cancellationToken) =>
        writer.WriteAllTextAsync(path, JsonSerializer.Serialize(state, JsonDefaults.Options), cancellationToken);

    private static bool IsValid(JsonElement document, RuntimeState? state) =>
        document.ValueKind == JsonValueKind.Object
        && HasProperties(document, "schemaVersion", "enabled", "processId", "processStartUtc", "instanceId", "heartbeatUtc", "selectedProject")
        && state is not null
        && state.SchemaVersion == 1
        && state.ProcessId > 0
        && state.ProcessStartUtc != default
        && state.InstanceId != Guid.Empty
        && state.HeartbeatUtc != default
        && !string.IsNullOrWhiteSpace(state.SelectedProject);

    private static bool HasProperties(JsonElement document, params string[] names) =>
        names.All(name => document.EnumerateObject().Any(property =>
            string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)));

    private async Task<string> ReadJsonAsync(CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken);
    }
}
