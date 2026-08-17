using System.Text.Json.Nodes;
using CCAutoApprove.Infrastructure.Claude;

namespace CCAutoApprove.Infrastructure.Tests.Claude;

public sealed class ClaudeHookManagerTests
{
    private const string CliPath = @"C:\Program Files\CCAutoApprove\cli\CCAutoApprove.Cli.exe";
    private const string ManagedCommand = "\"C:\\Program Files\\CCAutoApprove\\cli\\CCAutoApprove.Cli.exe\" hook";
    private const string ExistingSettings = """
        {
          "model": "claude-sonnet-4-5",
          "permissions": {
            "allow": ["Bash(dotnet test:*)"],
            "deny": ["Read(./secrets/**)"]
          },
          "hooks": {
            "PostToolUse": [
              {
                "matcher": "Write|Edit",
                "hooks": [
                  {
                    "type": "command",
                    "command": "powershell.exe -File C:\\tools\\format.ps1"
                  }
                ]
              }
            ]
          },
          "futureSetting": {
            "nested": true
          }
        }
        """;

    [Fact]
    public async Task InstallAsync_PreservesCompleteExistingSettingsAndAddsPermissionRequestHandler()
    {
        using var temp = new TemporaryDirectory();
        string settingsPath = await WriteSettingsAsync(temp, ExistingSettings);
        var manager = new ClaudeHookManager(settingsPath, CliPath);

        await manager.InstallAsync(CancellationToken.None);

        JsonObject root = (JsonObject)JsonNode.Parse(await File.ReadAllTextAsync(settingsPath))!;
        Assert.Equal("claude-sonnet-4-5", (string?)root["model"]);
        Assert.Equal("Bash(dotnet test:*)", (string?)root["permissions"]!["allow"]![0]);
        Assert.Equal("Read(./secrets/**)", (string?)root["permissions"]!["deny"]![0]);
        Assert.True((bool)root["futureSetting"]!["nested"]!);
        Assert.Equal("Write|Edit", (string?)root["hooks"]!["PostToolUse"]![0]!["matcher"]);
        Assert.Equal("powershell.exe -File C:\\tools\\format.ps1",
            (string?)root["hooks"]!["PostToolUse"]![0]!["hooks"]![0]!["command"]);
        JsonNode handler = root["hooks"]!["PermissionRequest"]![0]!;
        Assert.Equal("", (string?)handler["matcher"]);
        Assert.Equal("command", (string?)handler["hooks"]![0]!["type"]);
        Assert.Equal(ManagedCommand, (string?)handler["hooks"]![0]!["command"]);
        Assert.True(await manager.IsInstalledAsync(CancellationToken.None));
    }

    [Fact]
    public async Task InstallAsync_WhenRepeated_LeavesExactlyOneManagedCommandAndCreatesNoSecondBackup()
    {
        using var temp = new TemporaryDirectory();
        string settingsPath = await WriteSettingsAsync(temp, ExistingSettings);
        var manager = new ClaudeHookManager(settingsPath, CliPath);

        await manager.InstallAsync(CancellationToken.None);
        byte[] afterFirstInstall = await File.ReadAllBytesAsync(settingsPath);
        await manager.InstallAsync(CancellationToken.None);

        JsonNode root = JsonNode.Parse(await File.ReadAllTextAsync(settingsPath))!;
        string[] commands = root["hooks"]!["PermissionRequest"]!.AsArray()
            .SelectMany(entry => entry!["hooks"]!.AsArray())
            .Select(hook => (string?)hook!["command"])
            .Where(command => string.Equals(command, ManagedCommand, StringComparison.OrdinalIgnoreCase))
            .Cast<string>()
            .ToArray();
        Assert.Single(commands);
        Assert.Equal(afterFirstInstall, await File.ReadAllBytesAsync(settingsPath));
        Assert.Single(Directory.EnumerateFiles(temp.Path, "settings.json.ccautoapprove-backup-*"));
    }

    [Fact]
    public async Task InstallAsync_WhenSettingsMissing_CreatesSettingsWithoutBackup()
    {
        using var temp = new TemporaryDirectory();
        string settingsPath = Path.Combine(temp.Path, "settings.json");

        await new ClaudeHookManager(settingsPath, CliPath).InstallAsync(CancellationToken.None);

        Assert.True(File.Exists(settingsPath));
        Assert.Empty(Directory.EnumerateFiles(temp.Path, "settings.json.ccautoapprove-backup-*"));
    }

    [Fact]
    public async Task InstallAsync_WhenSettingsMalformed_ThrowsAndLeavesOriginalBytesUnchanged()
    {
        using var temp = new TemporaryDirectory();
        byte[] original = [0x7B, 0x22, 0x68, 0x6F, 0x6F, 0x6B, 0x73, 0x22, 0x3A];
        string settingsPath = Path.Combine(temp.Path, "settings.json");
        await File.WriteAllBytesAsync(settingsPath, original);

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => new ClaudeHookManager(settingsPath, CliPath).InstallAsync(CancellationToken.None));

        Assert.DoesNotContain("hooks", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(original, await File.ReadAllBytesAsync(settingsPath));
        Assert.Empty(Directory.EnumerateFiles(temp.Path, "settings.json.ccautoapprove-backup-*"));
    }

    [Fact]
    public async Task UninstallAsync_RemovesOnlyExactManagedHookAndPrunesOnlyEmptyManagedContainers()
    {
        using var temp = new TemporaryDirectory();
        const string installedSettings = """
            {
              "model": "claude-sonnet-4-5",
              "permissions": { "allow": ["Bash(dotnet test:*)"], "deny": [] },
              "hooks": {
                "PermissionRequest": [
                  {
                    "matcher": "",
                    "hooks": [
                      { "type": "COMMAND", "command": "\"c:\\program files\\ccautoapprove\\cli\\..\\cli\\ccautoapprove.cli.exe\" HOOK" },
                      { "type": "command", "command": "C:\\tools\\notify.exe CCAutoApprove" },
                      { "type": "prompt", "command": "\"C:\\Program Files\\CCAutoApprove\\cli\\CCAutoApprove.Cli.exe\" hook" },
                      { "type": 42, "command": "\"C:\\Program Files\\CCAutoApprove\\cli\\CCAutoApprove.Cli.exe\" hook" }
                    ]
                  },
                  {
                    "matcher": "Bash",
                    "hooks": [
                      { "type": "command", "command": "C:\\tools\\review.exe" }
                    ]
                  },
                  {
                    "matcher": "EmptyUserHook",
                    "hooks": []
                  }
                ],
                "PostToolUse": [
                  { "matcher": "Write", "hooks": [{ "type": "command", "command": "C:\\tools\\format.exe" }] }
                ]
              },
              "futureSetting": 42
            }
            """;
        string settingsPath = await WriteSettingsAsync(temp, installedSettings);
        var manager = new ClaudeHookManager(settingsPath, CliPath);

        await manager.UninstallAsync(CancellationToken.None);

        JsonNode root = JsonNode.Parse(await File.ReadAllTextAsync(settingsPath))!;
        JsonArray permissionRequests = root["hooks"]!["PermissionRequest"]!.AsArray();
        Assert.Equal(3, permissionRequests.Count);
        Assert.Equal("C:\\tools\\notify.exe CCAutoApprove", (string?)permissionRequests[0]!["hooks"]![0]!["command"]);
        Assert.Equal("prompt", (string?)permissionRequests[0]!["hooks"]![1]!["type"]);
        Assert.Equal(42, (int)permissionRequests[0]!["hooks"]![2]!["type"]!);
        Assert.Equal("C:\\tools\\review.exe", (string?)permissionRequests[1]!["hooks"]![0]!["command"]);
        Assert.Equal("EmptyUserHook", (string?)permissionRequests[2]!["matcher"]);
        Assert.Empty(permissionRequests[2]!["hooks"]!.AsArray());
        Assert.Equal("C:\\tools\\format.exe", (string?)root["hooks"]!["PostToolUse"]![0]!["hooks"]![0]!["command"]);
        Assert.Equal(42, (int)root["futureSetting"]!);
        Assert.False(await manager.IsInstalledAsync(CancellationToken.None));
        Assert.Single(Directory.EnumerateFiles(temp.Path, "settings.json.ccautoapprove-backup-*"));
    }

    [Fact]
    public async Task UninstallAsync_WhenRepeated_SucceedsWithoutModificationOrBackup()
    {
        using var temp = new TemporaryDirectory();
        string settingsPath = await WriteSettingsAsync(temp, ExistingSettings);
        var manager = new ClaudeHookManager(settingsPath, CliPath);
        byte[] original = await File.ReadAllBytesAsync(settingsPath);

        await manager.UninstallAsync(CancellationToken.None);
        await manager.UninstallAsync(CancellationToken.None);

        Assert.Equal(original, await File.ReadAllBytesAsync(settingsPath));
        Assert.Empty(Directory.EnumerateFiles(temp.Path, "settings.json.ccautoapprove-backup-*"));
    }

    private static async Task<string> WriteSettingsAsync(TemporaryDirectory temp, string contents)
    {
        string path = Path.Combine(temp.Path, "settings.json");
        await File.WriteAllTextAsync(path, contents);
        return path;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CCAutoApprove.HookTests", Guid.NewGuid().ToString("N"));
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
