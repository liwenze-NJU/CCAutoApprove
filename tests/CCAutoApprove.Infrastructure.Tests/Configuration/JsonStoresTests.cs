using System.Text;
using CCAutoApprove.Core.Models;
using CCAutoApprove.Infrastructure.Configuration;

namespace CCAutoApprove.Infrastructure.Tests.Configuration;

public sealed class JsonStoresTests
{
    [Fact]
    public async Task RuntimeStore_RoundTripsCompleteState()
    {
        using var temp = new TemporaryDirectory();
        var store = new JsonRuntimeStateStore(Path.Combine(temp.Path, "runtime.json"));
        RuntimeState expected = RuntimeState.CreateForTest(true, DateTimeOffset.UtcNow);

        await store.SaveAsync(expected, CancellationToken.None);
        RuntimeState? actual = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task RuntimeStore_InvalidJson_ReturnsNull()
    {
        using var temp = new TemporaryDirectory();
        string path = Path.Combine(temp.Path, "runtime.json");
        await File.WriteAllTextAsync(path, "{\"enabled\":tr");
        var store = new JsonRuntimeStateStore(path);

        Assert.Null(await store.LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RuntimeStore_ConcurrentReadsAndWrites_OnlyReturnsCompleteSchemaVersionOneDocuments()
    {
        using var temp = new TemporaryDirectory();
        var store = new JsonRuntimeStateStore(Path.Combine(temp.Path, "runtime.json"));
        await store.SaveAsync(RuntimeState.CreateForTest(false, DateTimeOffset.UtcNow), CancellationToken.None);

        Task[] operations = Enumerable.Range(0, 100).Select(async index =>
        {
            if (index % 2 == 0)
            {
                await store.SaveAsync(RuntimeState.CreateForTest(index % 4 == 0, DateTimeOffset.UtcNow), CancellationToken.None);
            }
            else
            {
                RuntimeState? state = await store.LoadAsync(CancellationToken.None);
                Assert.True(state is null || state.SchemaVersion == 1);
            }
        }).ToArray();

        await Task.WhenAll(operations);
    }

    [Fact]
    public async Task RuntimeStore_Save_WritesUtf8JsonAndRemovesTemporaryFiles()
    {
        using var temp = new TemporaryDirectory();
        string path = Path.Combine(temp.Path, "nested", "runtime.json");
        var store = new JsonRuntimeStateStore(path);

        await store.SaveAsync(RuntimeState.CreateForTest(true, DateTimeOffset.UtcNow), CancellationToken.None);

        byte[] bytes = await File.ReadAllBytesAsync(path);
        Assert.Equal(Encoding.UTF8.GetString(bytes), await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    [Fact]
    public async Task SettingsStore_MissingFile_ReturnsDefaults()
    {
        using var temp = new TemporaryDirectory();
        var store = new JsonSettingsStore(Path.Combine(temp.Path, "settings.json"));

        PersistentSettings settings = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(new PersistentSettings(), settings);
    }

    [Fact]
    public async Task SettingsStore_RoundTripsCompleteSettings()
    {
        using var temp = new TemporaryDirectory();
        var store = new JsonSettingsStore(Path.Combine(temp.Path, "settings.json"));
        var expected = new PersistentSettings(SelectedProject: "D:\\work", StartWithWindows: true,
            Language: "en-US", AuditDetailLevel: AuditDetailLevel.Detailed, AuditRetentionDays: 30);

        await store.SaveAsync(expected, CancellationToken.None);

        Assert.Equal(expected, await store.LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SettingsStore_InvalidJson_ThrowsInvalidDataException()
    {
        using var temp = new TemporaryDirectory();
        string path = Path.Combine(temp.Path, "settings.json");
        await File.WriteAllTextAsync(path, "{\"language\":");
        var store = new JsonSettingsStore(path);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(CancellationToken.None));
    }

    [Fact]
    public void AppPaths_UsesProvidedBasePath()
    {
        var paths = new AppPaths("D:\\test-data");

        Assert.Equal(Path.Combine("D:\\test-data", "settings.json"), paths.SettingsPath);
        Assert.Equal(Path.Combine("D:\\test-data", "runtime.json"), paths.RuntimeStatePath);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CCAutoApprove.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
