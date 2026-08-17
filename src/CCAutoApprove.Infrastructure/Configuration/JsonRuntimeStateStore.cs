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
            string json = await File.ReadAllTextAsync(path, cancellationToken);
            return JsonSerializer.Deserialize<RuntimeState>(json, JsonDefaults.Options);
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
}
