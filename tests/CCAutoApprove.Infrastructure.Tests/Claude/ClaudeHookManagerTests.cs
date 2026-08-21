using System.Text;
using System.Text.Json.Nodes;
using CCAutoApprove.Infrastructure.Claude;
using CCAutoApprove.Infrastructure.Configuration;

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

    public static TheoryData<string> WrongShapedSettings => new()
    {
        """
        {
          "model": "claude-sonnet-4-5",
          "permissions": { "allow": ["Bash(dotnet test:*)"], "deny": [] },
          "hooks": {
            "PermissionRequest": [
              { "matcher": "", "hooks": [{ "type": "command", "command": "\"C:\\Program Files\\CCAutoApprove\\cli\\CCAutoApprove.Cli.exe\" hook" }] },
              { "matcher": "", "hooks": "invalid" }
            ],
            "PostToolUse": [{ "matcher": "Write", "hooks": [{ "type": "command", "command": "C:\\tools\\format.exe" }] }]
          },
          "futureSetting": { "nested": true }
        }
        """,
        """
        {
          "model": "claude-sonnet-4-5",
          "permissions": { "allow": ["Bash(dotnet test:*)"], "deny": [] },
          "hooks": null,
          "futureSetting": { "nested": true }
        }
        """,
        """
        {
          "model": "claude-sonnet-4-5",
          "permissions": { "allow": ["Bash(dotnet test:*)"], "deny": [] },
          "hooks": { "PermissionRequest": null, "PostToolUse": [] },
          "futureSetting": { "nested": true }
        }
        """,
        """
        {
          "model": "claude-sonnet-4-5",
          "permissions": { "allow": ["Bash(dotnet test:*)"], "deny": [] },
          "hooks": {
            "PermissionRequest": [
              { "matcher": "", "hooks": [{ "type": "command", "command": "\"C:\\Program Files\\CCAutoApprove\\cli\\CCAutoApprove.Cli.exe\" hook" }] },
              { "matcher": "Bash", "hooks": null }
            ],
            "PostToolUse": []
          },
          "futureSetting": { "nested": true }
        }
        """,
        """
        {
          "model": "claude-sonnet-4-5",
          "permissions": { "allow": ["Bash(dotnet test:*)"], "deny": [] },
          "hooks": {
            "PermissionRequest": [
              { "matcher": 42, "hooks": [{ "type": "command", "command": "\"C:\\Program Files\\CCAutoApprove\\cli\\CCAutoApprove.Cli.exe\" hook" }] }
            ],
            "PostToolUse": []
          },
          "futureSetting": { "nested": true }
        }
        """,
        """
        {
          "model": "claude-sonnet-4-5",
          "permissions": { "allow": ["Bash(dotnet test:*)"], "deny": [] },
          "hooks": {
            "PermissionRequest": [
              { "matcher": "", "hooks": [
                { "type": "command", "command": "\"C:\\Program Files\\CCAutoApprove\\cli\\CCAutoApprove.Cli.exe\" hook" },
                { "type": 42, "command": "C:\\tools\\notify.exe" }
              ] }
            ],
            "PostToolUse": []
          },
          "futureSetting": { "nested": true }
        }
        """,
        """
        {
          "model": "claude-sonnet-4-5",
          "permissions": { "allow": ["Bash(dotnet test:*)"], "deny": [] },
          "hooks": {
            "PermissionRequest": [
              { "matcher": "", "hooks": [
                { "type": "command", "command": "\"C:\\Program Files\\CCAutoApprove\\cli\\CCAutoApprove.Cli.exe\" hook" },
                { "type": "command", "command": 42 }
              ] }
            ],
            "PostToolUse": []
          },
          "futureSetting": { "nested": true }
        }
        """
    };

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
    public async Task InstallAsync_WhenSettingsChangeBeforeCommit_RetriesMergeAndPreservesConcurrentEdit()
    {
        using var temp = new TemporaryDirectory();
        string settingsPath = await WriteSettingsAsync(temp, ExistingSettings);
        var committer = new BlockingFirstCommitter(new SnapshotClaudeSettingsCommitter());
        var manager = new ClaudeHookManager(settingsPath, CliPath, committer);

        Task install = manager.InstallAsync(CancellationToken.None);
        await committer.FirstAttemptStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        JsonObject concurrent = (JsonObject)JsonNode.Parse(ExistingSettings)!;
        concurrent["concurrentEdit"] = "preserved";
        await File.WriteAllTextAsync(settingsPath, concurrent.ToJsonString());
        committer.ReleaseFirstAttempt.SetResult();
        await install;

        JsonObject root = (JsonObject)JsonNode.Parse(await File.ReadAllTextAsync(settingsPath))!;
        Assert.Equal("preserved", (string?)root["concurrentEdit"]);
        Assert.Equal(ManagedCommand,
            (string?)root["hooks"]!["PermissionRequest"]![0]!["hooks"]![0]!["command"]);
        Assert.Equal(2, committer.AttemptCount);
        Assert.Single(Directory.EnumerateFiles(temp.Path, "settings.json.ccautoapprove-backup-*"));
    }

    [Fact]
    public async Task SnapshotCommitter_WhenSourceChangesWhileReplacementIsPrepared_DoesNotOverwriteExternalEdit()
    {
        using var temp = new TemporaryDirectory();
        string settingsPath = await WriteSettingsAsync(temp, ExistingSettings);
        byte[] original = await File.ReadAllBytesAsync(settingsPath);
        byte[] externalEdit = Encoding.UTF8.GetBytes("{\"externalEdit\":\"preserved\"}");
        using var operations = new BlockingFirstMoveOperations();
        var committer = new SnapshotClaudeSettingsCommitter(new AtomicFileWriter(operations));

        Task<bool> commit = committer.TryCommitAsync(
            settingsPath,
            original,
            "{\"replacement\":true}",
            CancellationToken.None);
        try
        {
            Assert.True(operations.FirstMoveStarted.Wait(TimeSpan.FromSeconds(2)));
            await File.WriteAllBytesAsync(settingsPath, externalEdit);
            operations.ReleaseFirstMove.Set();

            bool committed = await commit.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.False(committed);
            Assert.Equal(externalEdit, await File.ReadAllBytesAsync(settingsPath));
        }
        finally
        {
            operations.ReleaseFirstMove.Set();
        }
    }

    [Fact]
    public async Task SnapshotCommitter_AfterComparison_HoldsOwnershipThroughReplacement()
    {
        using var temp = new TemporaryDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        string settingsPath = await WriteSettingsAsync(temp, ExistingSettings);
        byte[] original = await File.ReadAllBytesAsync(settingsPath, timeout.Token);
        byte[] externalEdit = Encoding.UTF8.GetBytes("{\"externalEdit\":\"after-comparison\"}");
        var comparisonGate = new AfterComparisonGate();
        var committer = new SnapshotClaudeSettingsCommitter(
            writer: null,
            new SystemClaudeSettingsFileOperations(comparisonGate.PauseAsync));
        var firstExternalAttempt = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        Task<bool> commit = committer.TryCommitAsync(
            settingsPath,
            original,
            "{\"replacement\":true}",
            timeout.Token);
        try
        {
            await comparisonGate.ComparisonCompleted.Task.WaitAsync(timeout.Token);
            Task<bool> externalWrite = WriteExternalBytesWithRetryAsync(
                settingsPath,
                externalEdit,
                firstExternalAttempt,
                timeout.Token);
            await firstExternalAttempt.Task.WaitAsync(timeout.Token);
            Assert.False(commit.IsCompleted);

            comparisonGate.Release.SetResult();

            Assert.True(await commit.WaitAsync(timeout.Token));
            Assert.True(await externalWrite.WaitAsync(timeout.Token));
            Assert.Equal(externalEdit, await File.ReadAllBytesAsync(settingsPath, timeout.Token));
        }
        finally
        {
            comparisonGate.Release.TrySetResult();
        }
    }

    [Fact]
    public async Task SnapshotCommitter_RestoreAfterComparison_HoldsOwnershipThroughReplacement()
    {
        using var temp = new TemporaryDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        string settingsPath = Path.Combine(temp.Path, "settings.json");
        byte[] attemptBytes = Encoding.UTF8.GetBytes("{\"attempt\":true}");
        byte[] backupBytes = Encoding.UTF8.GetBytes(ExistingSettings);
        byte[] externalEdit = Encoding.UTF8.GetBytes("{\"externalEdit\":\"during-restore\"}");
        await File.WriteAllBytesAsync(settingsPath, attemptBytes, timeout.Token);
        var comparisonGate = new AfterComparisonGate();
        var committer = new SnapshotClaudeSettingsCommitter(
            writer: null,
            new SystemClaudeSettingsFileOperations(comparisonGate.PauseAsync));
        var firstExternalAttempt = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        Task<bool> restore = committer.TryCommitBytesAsync(
            settingsPath,
            attemptBytes,
            backupBytes,
            timeout.Token);
        try
        {
            await comparisonGate.ComparisonCompleted.Task.WaitAsync(timeout.Token);
            Task<bool> externalWrite = WriteExternalBytesWithRetryAsync(
                settingsPath,
                externalEdit,
                firstExternalAttempt,
                timeout.Token);
            await firstExternalAttempt.Task.WaitAsync(timeout.Token);
            Assert.False(restore.IsCompleted);

            comparisonGate.Release.SetResult();

            Assert.True(await restore.WaitAsync(timeout.Token));
            Assert.True(await externalWrite.WaitAsync(timeout.Token));
            Assert.Equal(externalEdit, await File.ReadAllBytesAsync(settingsPath, timeout.Token));
        }
        finally
        {
            comparisonGate.Release.TrySetResult();
        }
    }

    [Fact]
    public async Task SnapshotCommitter_DeleteAfterComparison_HoldsOwnershipThroughDeletion()
    {
        using var temp = new TemporaryDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        string settingsPath = Path.Combine(temp.Path, "settings.json");
        byte[] attemptBytes = Encoding.UTF8.GetBytes("{\"attempt\":true}");
        byte[] externalEdit = Encoding.UTF8.GetBytes("{\"externalEdit\":\"during-delete\"}");
        await File.WriteAllBytesAsync(settingsPath, attemptBytes, timeout.Token);
        var comparisonGate = new AfterComparisonGate();
        var committer = new SnapshotClaudeSettingsCommitter(
            writer: null,
            new SystemClaudeSettingsFileOperations(comparisonGate.PauseAsync));
        var firstExternalAttempt = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        Task<bool> delete = committer.TryDeleteAsync(
            settingsPath,
            attemptBytes,
            timeout.Token);
        try
        {
            await comparisonGate.ComparisonCompleted.Task.WaitAsync(timeout.Token);
            Task<bool> externalWrite = WriteExternalBytesWithRetryAsync(
                settingsPath,
                externalEdit,
                firstExternalAttempt,
                timeout.Token);
            await firstExternalAttempt.Task.WaitAsync(timeout.Token);
            Assert.False(delete.IsCompleted);

            comparisonGate.Release.SetResult();

            Assert.True(await delete.WaitAsync(timeout.Token));
            Assert.True(await externalWrite.WaitAsync(timeout.Token));
            Assert.Equal(externalEdit, await File.ReadAllBytesAsync(settingsPath, timeout.Token));
        }
        finally
        {
            comparisonGate.Release.TrySetResult();
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InstallAsync_WhenExternalEditFollowsThisAttemptsWrite_PreservesExternalBytesInsteadOfRollingBack(
        bool settingsExisted)
    {
        using var temp = new TemporaryDirectory();
        string settingsPath = Path.Combine(temp.Path, "settings.json");
        if (settingsExisted)
        {
            await File.WriteAllTextAsync(settingsPath, ExistingSettings);
        }

        byte[] externalEdit = Encoding.UTF8.GetBytes("{\"externalEdit\":\"preserved\"}");
        var committer = new WriteThenPauseAndThrowCommitter();
        var manager = new ClaudeHookManager(settingsPath, CliPath, committer);

        Task install = manager.InstallAsync(CancellationToken.None);
        try
        {
            await committer.ReplacementWritten.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await File.WriteAllBytesAsync(settingsPath, externalEdit);
            committer.ReleaseFailure.SetResult();

            IOException exception = await Assert.ThrowsAsync<IOException>(() => install);

            Assert.Equal("post-write failure", exception.Message);
            Assert.Equal(externalEdit, await File.ReadAllBytesAsync(settingsPath));
        }
        finally
        {
            committer.ReleaseFailure.TrySetResult();
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InstallAsync_WhenNoExternalEditFollowsThisAttemptsWrite_RollsBackOwnedBytes(
        bool settingsExisted)
    {
        using var temp = new TemporaryDirectory();
        string settingsPath = Path.Combine(temp.Path, "settings.json");
        byte[]? original = null;
        if (settingsExisted)
        {
            await File.WriteAllTextAsync(settingsPath, ExistingSettings);
            original = await File.ReadAllBytesAsync(settingsPath);
        }

        var committer = new WriteThenPauseAndThrowCommitter();
        var manager = new ClaudeHookManager(settingsPath, CliPath, committer);

        Task install = manager.InstallAsync(CancellationToken.None);
        try
        {
            await committer.ReplacementWritten.Task.WaitAsync(TimeSpan.FromSeconds(2));
            committer.ReleaseFailure.SetResult();

            IOException exception = await Assert.ThrowsAsync<IOException>(() => install);

            Assert.Equal("post-write failure", exception.Message);
            if (settingsExisted)
            {
                Assert.Equal(original, await File.ReadAllBytesAsync(settingsPath));
            }
            else
            {
                Assert.False(File.Exists(settingsPath));
            }
        }
        finally
        {
            committer.ReleaseFailure.TrySetResult();
        }
    }

    [Fact]
    public async Task InstallAsync_ConcurrentManagers_SerializeOneMergedCommit()
    {
        using var temp = new TemporaryDirectory();
        string settingsPath = await WriteSettingsAsync(temp, ExistingSettings);
        var committer = new BlockingFirstCommitter(new SnapshotClaudeSettingsCommitter());
        var firstManager = new ClaudeHookManager(settingsPath, CliPath, committer);
        var secondManager = new ClaudeHookManager(settingsPath, CliPath);

        Task firstInstall = firstManager.InstallAsync(CancellationToken.None);
        await committer.FirstAttemptStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task secondInstall = secondManager.InstallAsync(CancellationToken.None);
        Assert.False(secondInstall.IsCompleted);

        committer.ReleaseFirstAttempt.SetResult();
        await Task.WhenAll(firstInstall, secondInstall);

        JsonArray wrappers = JsonNode.Parse(await File.ReadAllTextAsync(settingsPath))!
            ["hooks"]!["PermissionRequest"]!.AsArray();
        Assert.Single(wrappers);
        Assert.Equal(ManagedCommand, (string?)wrappers[0]!["hooks"]![0]!["command"]);
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

    [Theory]
    [MemberData(nameof(WrongShapedSettings))]
    public async Task InstallAsync_WhenNestedHookShapeIsInvalid_ThrowsAndPreservesOriginalBytes(string settings)
    {
        using var temp = new TemporaryDirectory();
        string settingsPath = await WriteSettingsAsync(temp, settings);
        byte[] original = await File.ReadAllBytesAsync(settingsPath);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => new ClaudeHookManager(settingsPath, CliPath).InstallAsync(CancellationToken.None));

        Assert.Equal(original, await File.ReadAllBytesAsync(settingsPath));
        Assert.Empty(Directory.EnumerateFiles(temp.Path, "settings.json.ccautoapprove-backup-*"));
    }

    [Theory]
    [MemberData(nameof(WrongShapedSettings))]
    public async Task UninstallAsync_WhenNestedHookShapeIsInvalid_ThrowsAndPreservesOriginalBytes(string settings)
    {
        using var temp = new TemporaryDirectory();
        string settingsPath = await WriteSettingsAsync(temp, settings);
        byte[] original = await File.ReadAllBytesAsync(settingsPath);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => new ClaudeHookManager(settingsPath, CliPath).UninstallAsync(CancellationToken.None));

        Assert.Equal(original, await File.ReadAllBytesAsync(settingsPath));
        Assert.Empty(Directory.EnumerateFiles(temp.Path, "settings.json.ccautoapprove-backup-*"));
    }

    [Fact]
    public async Task InstallAsync_OwnedCommandUnderNonemptyMatcher_AddsExactWrapperAndPreservesOriginalWrapper()
    {
        using var temp = new TemporaryDirectory();
        const string settings = """
            {
              "model": "claude-sonnet-4-5",
              "permissions": { "allow": ["Bash(dotnet test:*)"], "deny": [] },
              "hooks": {
                "PermissionRequest": [
                  {
                    "matcher": "Bash",
                    "hooks": [
                      { "type": "command", "command": "\"C:\\Program Files\\CCAutoApprove\\cli\\CCAutoApprove.Cli.exe\" hook" },
                      { "type": "command", "command": "C:\\tools\\notify.exe" }
                    ]
                  }
                ],
                "PostToolUse": []
              },
              "futureSetting": { "nested": true }
            }
            """;
        string settingsPath = await WriteSettingsAsync(temp, settings);
        var manager = new ClaudeHookManager(settingsPath, CliPath);
        Assert.False(await manager.IsInstalledAsync(CancellationToken.None));

        await manager.InstallAsync(CancellationToken.None);

        JsonArray wrappers = JsonNode.Parse(await File.ReadAllTextAsync(settingsPath))!
            ["hooks"]!["PermissionRequest"]!.AsArray();
        Assert.Equal(2, wrappers.Count);
        Assert.Equal("Bash", (string?)wrappers[0]!["matcher"]);
        Assert.Equal(ManagedCommand, (string?)wrappers[0]!["hooks"]![0]!["command"]);
        Assert.Equal("C:\\tools\\notify.exe", (string?)wrappers[0]!["hooks"]![1]!["command"]);
        Assert.Equal("", (string?)wrappers[1]!["matcher"]);
        Assert.Equal(ManagedCommand, (string?)wrappers[1]!["hooks"]![0]!["command"]);
        Assert.True(await manager.IsInstalledAsync(CancellationToken.None));
    }

    [Fact]
    public async Task InstallAndUninstall_UnrelatedWrapperWithoutMatcher_IsAcceptedAndPreserved()
    {
        using var temp = new TemporaryDirectory();
        const string settings = """
            {
              "hooks": {
                "PermissionRequest": [
                  {
                    "hooks": [
                      { "type": "command", "command": "C:\\tools\\user-approval.exe" }
                    ]
                  }
                ]
              },
              "futureSetting": { "preserve": true }
            }
            """;
        string settingsPath = await WriteSettingsAsync(temp, settings);
        var manager = new ClaudeHookManager(settingsPath, CliPath);

        await manager.InstallAsync(CancellationToken.None);
        Assert.True(await manager.IsInstalledAsync(CancellationToken.None));
        await manager.UninstallAsync(CancellationToken.None);

        JsonObject root = (JsonObject)JsonNode.Parse(await File.ReadAllTextAsync(settingsPath))!;
        JsonObject userWrapper = (JsonObject)root["hooks"]!["PermissionRequest"]![0]!;
        Assert.False(userWrapper.ContainsKey("matcher"));
        Assert.Equal("C:\\tools\\user-approval.exe",
            (string?)userWrapper["hooks"]![0]!["command"]);
        Assert.True((bool)root["futureSetting"]!["preserve"]!);
        Assert.False(await manager.IsInstalledAsync(CancellationToken.None));
    }

    [Fact]
    public async Task UninstallAsync_OwnedCommandUnderNonemptyMatcher_PreservesOriginalBytes()
    {
        using var temp = new TemporaryDirectory();
        const string settings = """
            {
              "model": "claude-sonnet-4-5",
              "permissions": { "allow": ["Bash(dotnet test:*)"], "deny": [] },
              "hooks": {
                "PermissionRequest": [
                  { "matcher": "Bash", "hooks": [{ "type": "command", "command": "\"C:\\Program Files\\CCAutoApprove\\cli\\CCAutoApprove.Cli.exe\" hook" }] }
                ],
                "PostToolUse": []
              },
              "futureSetting": { "nested": true }
            }
            """;
        string settingsPath = await WriteSettingsAsync(temp, settings);
        byte[] original = await File.ReadAllBytesAsync(settingsPath);

        await new ClaudeHookManager(settingsPath, CliPath).UninstallAsync(CancellationToken.None);

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
                      { "type": "future", "command": "\"C:\\Program Files\\CCAutoApprove\\cli\\CCAutoApprove.Cli.exe\" hook" }
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
        Assert.Equal("future", (string?)permissionRequests[0]!["hooks"]![2]!["type"]);
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

    private static async Task<bool> WriteExternalBytesWithRetryAsync(
        string settingsPath,
        byte[] bytes,
        TaskCompletionSource firstAttemptCompleted,
        CancellationToken cancellationToken)
    {
        bool ownershipBoundaryObserved = false;
        while (true)
        {
            try
            {
                await File.WriteAllBytesAsync(settingsPath, bytes, cancellationToken);
                firstAttemptCompleted.TrySetResult();
                return ownershipBoundaryObserved;
            }
            catch (IOException)
            {
                ownershipBoundaryObserved = true;
                firstAttemptCompleted.TrySetResult();
                await Task.Delay(TimeSpan.FromMilliseconds(5), cancellationToken);
            }
            catch (UnauthorizedAccessException)
            {
                ownershipBoundaryObserved = true;
                firstAttemptCompleted.TrySetResult();
                await Task.Delay(TimeSpan.FromMilliseconds(5), cancellationToken);
            }
        }
    }

    private sealed class AfterComparisonGate
    {
        public TaskCompletionSource ComparisonCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task PauseAsync(CancellationToken cancellationToken)
        {
            ComparisonCompleted.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class BlockingFirstCommitter(IClaudeSettingsCommitter inner)
        : IClaudeSettingsCommitter
    {
        private int attemptCount;

        public TaskCompletionSource FirstAttemptStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstAttempt { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int AttemptCount => Volatile.Read(ref attemptCount);

        public async Task<bool> TryCommitAsync(
            string settingsPath,
            byte[]? expectedSource,
            string replacement,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref attemptCount) == 1)
            {
                FirstAttemptStarted.SetResult();
                await ReleaseFirstAttempt.Task.WaitAsync(cancellationToken);
            }

            return await inner.TryCommitAsync(
                settingsPath,
                expectedSource,
                replacement,
                cancellationToken);
        }
    }

    private sealed class WriteThenPauseAndThrowCommitter : IClaudeSettingsCommitter
    {
        public TaskCompletionSource ReplacementWritten { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFailure { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<bool> TryCommitAsync(
            string settingsPath,
            byte[]? expectedSource,
            string replacement,
            CancellationToken cancellationToken)
        {
            await File.WriteAllTextAsync(settingsPath, replacement, cancellationToken);
            ReplacementWritten.SetResult();
            await ReleaseFailure.Task.WaitAsync(cancellationToken);
            throw new IOException("post-write failure");
        }
    }

    private sealed class BlockingFirstMoveOperations : IAtomicFileOperations, IDisposable
    {
        private int moveAttempts;

        public ManualResetEventSlim FirstMoveStarted { get; } = new();
        public ManualResetEventSlim ReleaseFirstMove { get; } = new();

        public bool Exists(string path) => File.Exists(path);
        public void Delete(string path) => File.Delete(path);

        public void Move(string sourcePath, string targetPath)
        {
            if (Interlocked.Increment(ref moveAttempts) == 1)
            {
                FirstMoveStarted.Set();
                Assert.True(ReleaseFirstMove.Wait(TimeSpan.FromSeconds(2)));
            }

            File.Move(sourcePath, targetPath, overwrite: true);
        }

        public void Dispose()
        {
            FirstMoveStarted.Dispose();
            ReleaseFirstMove.Dispose();
        }
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
