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
            return JsonSerializer.Deserialize<PersistentSettings>(json, JsonDefaults.Options)
                ?? throw new InvalidDataException("Settings JSON must contain an object.");
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
}
