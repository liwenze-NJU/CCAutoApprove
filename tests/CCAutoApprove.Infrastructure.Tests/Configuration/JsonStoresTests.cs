using System.Text;
using CCAutoApprove.Core.Models;
using CCAutoApprove.Infrastructure.Configuration;
using System.Text.Json;

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

    [Theory]
    [InlineData("{\"schemaVersion\":1,\"enabled\":true,\"processId\":42,\"processStartUtc\":\"2026-08-17T00:00:00+00:00\",\"heartbeatUtc\":\"2026-08-17T00:01:00+00:00\",\"selectedProject\":\"D:\\\\work\"}")]
    [InlineData("{\"schemaVersion\":2,\"enabled\":true,\"processId\":42,\"processStartUtc\":\"2026-08-17T00:00:00+00:00\",\"instanceId\":\"11111111-1111-1111-1111-111111111111\",\"heartbeatUtc\":\"2026-08-17T00:01:00+00:00\",\"selectedProject\":\"D:\\\\work\"}")]
    [InlineData("{\"schemaVersion\":1,\"enabled\":true,\"processId\":42,\"processStartUtc\":\"2026-08-17T00:00:00+00:00\",\"instanceId\":\"11111111-1111-1111-1111-111111111111\",\"heartbeatUtc\":\"2026-08-17T00:01:00+00:00\",\"selectedProject\":null}")]
    [InlineData("{\"schemaVersion\":1,\"enabled\":true,\"processId\":0,\"processStartUtc\":\"2026-08-17T00:00:00+00:00\",\"instanceId\":\"00000000-0000-0000-0000-000000000000\",\"heartbeatUtc\":\"2026-08-17T00:01:00+00:00\",\"selectedProject\":\"D:\\\\work\"}")]
    public async Task RuntimeStore_StructurallyInvalidJson_ReturnsNull(string json)
    {
        using var temp = new TemporaryDirectory();
        string path = Path.Combine(temp.Path, "runtime.json");
        await File.WriteAllTextAsync(path, json);

        Assert.Null(await new JsonRuntimeStateStore(path).LoadAsync(CancellationToken.None));
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
    public async Task SettingsStore_Save_PersistsAuditDetailLevelAsBindingString()
    {
        using var temp = new TemporaryDirectory();
        string path = Path.Combine(temp.Path, "settings.json");
        var store = new JsonSettingsStore(path);

        await store.SaveAsync(
            new PersistentSettings(AuditDetailLevel: AuditDetailLevel.Detailed),
            CancellationToken.None);

        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.Equal("Detailed",
            document.RootElement.GetProperty("auditDetailLevel").GetString());
    }

    [Fact]
    public async Task SettingsStore_Load_AcceptsLegacyNumericAuditLevelAndMigratesOnSave()
    {
        using var temp = new TemporaryDirectory();
        string path = Path.Combine(temp.Path, "settings.json");
        await File.WriteAllTextAsync(path,
            "{\"schemaVersion\":1,\"selectedProject\":null,\"startWithWindows\":false,"
            + "\"language\":\"zh-CN\",\"auditDetailLevel\":2,\"auditRetentionDays\":7}");
        var store = new JsonSettingsStore(path);

        PersistentSettings settings = await store.LoadAsync(CancellationToken.None);
        await store.SaveAsync(settings, CancellationToken.None);

        Assert.Equal(AuditDetailLevel.Detailed, settings.AuditDetailLevel);
        using JsonDocument migrated = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.Equal("Detailed", migrated.RootElement.GetProperty("auditDetailLevel").GetString());
    }

    [Theory]
    [InlineData("null", 0)]
    [InlineData("null", 3651)]
    [InlineData("\"\"", 7)]
    [InlineData("\"relative\\\\project\"", 7)]
    public async Task SettingsStore_Load_RejectsInvalidRetentionAndSelectedProject(
        string selectedProjectJson,
        int retentionDays)
    {
        using var temp = new TemporaryDirectory();
        string path = Path.Combine(temp.Path, "settings.json");
        await File.WriteAllTextAsync(path,
            "{\"schemaVersion\":1,\"selectedProject\":" + selectedProjectJson
            + ",\"startWithWindows\":false,\"language\":\"zh-CN\","
            + "\"auditDetailLevel\":\"PrivacySafe\",\"auditRetentionDays\":"
            + retentionDays.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}");

        await Assert.ThrowsAsync<InvalidDataException>(
            () => new JsonSettingsStore(path).LoadAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("", 7)]
    [InlineData("relative\\project", 7)]
    [InlineData(null, 0)]
    [InlineData(null, 3651)]
    public async Task SettingsStore_Save_RejectsInvalidRetentionAndSelectedProject(
        string? selectedProject,
        int retentionDays)
    {
        using var temp = new TemporaryDirectory();
        string path = Path.Combine(temp.Path, "settings.json");
        var store = new JsonSettingsStore(path);

        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(
            new PersistentSettings(
                SelectedProject: selectedProject,
                AuditRetentionDays: retentionDays),
            CancellationToken.None));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task SettingsStore_UpdateAsync_SerializesInterleavedFieldTransformsWithoutLoss()
    {
        using var temp = new TemporaryDirectory();
        string path = Path.Combine(temp.Path, "settings.json");
        var store = new JsonSettingsStore(path);
        await store.SaveAsync(new PersistentSettings(), CancellationToken.None);
        using var firstTransformEntered = new ManualResetEventSlim();
        using var releaseFirstTransform = new ManualResetEventSlim();
        using var laterTransformEntered = new ManualResetEventSlim();

        Task projectUpdate = Task.Run(() => store.UpdateAsync(current =>
        {
            firstTransformEntered.Set();
            Assert.True(releaseFirstTransform.Wait(TimeSpan.FromSeconds(2)));
            return current with { SelectedProject = @"D:\projects\latest" };
        }, CancellationToken.None));
        Assert.True(firstTransformEntered.Wait(TimeSpan.FromSeconds(2)));

        Task auditUpdate = store.UpdateAsync(current =>
        {
            laterTransformEntered.Set();
            return current with { AuditDetailLevel = AuditDetailLevel.Detailed };
        }, CancellationToken.None);
        Task startupUpdate = store.UpdateAsync(
            current => current with { StartWithWindows = true },
            CancellationToken.None);

        Assert.False(laterTransformEntered.IsSet);
        releaseFirstTransform.Set();
        await Task.WhenAll(projectUpdate, auditUpdate, startupUpdate);

        PersistentSettings settings = await store.LoadAsync(CancellationToken.None);
        Assert.Equal(@"D:\projects\latest", settings.SelectedProject);
        Assert.Equal(AuditDetailLevel.Detailed, settings.AuditDetailLevel);
        Assert.True(settings.StartWithWindows);
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

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"schemaVersion\":2,\"selectedProject\":null,\"startWithWindows\":false,\"language\":\"zh-CN\",\"auditDetailLevel\":1,\"auditRetentionDays\":7}")]
    [InlineData("{\"schemaVersion\":1,\"selectedProject\":null,\"startWithWindows\":false,\"language\":null,\"auditDetailLevel\":1,\"auditRetentionDays\":7}")]
    public async Task SettingsStore_StructurallyInvalidJson_ThrowsInvalidDataException(string json)
    {
        using var temp = new TemporaryDirectory();
        string path = Path.Combine(temp.Path, "settings.json");
        await File.WriteAllTextAsync(path, json);

        await Assert.ThrowsAsync<InvalidDataException>(() => new JsonSettingsStore(path).LoadAsync(CancellationToken.None));
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
