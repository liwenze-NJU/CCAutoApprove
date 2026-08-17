using CCAutoApprove.Core.Abstractions;
using CCAutoApprove.Infrastructure.Claude;

namespace CCAutoApprove.Infrastructure.Tests.Claude;

public sealed class ClaudeDoctorTests
{
    [Fact]
    public async Task RunAsync_HealthyInstallation_ReturnsExactlyAllPassingChecks()
    {
        using var environment = await DoctorEnvironment.CreateAsync();

        IReadOnlyList<DoctorCheck> checks = await environment.CreateDoctor().RunAsync(CancellationToken.None);

        Assert.Equal(
            [
                "ClaudeSettingsReadable", "ClaudeSettingsValid", "HookInstalled", "HookNotDuplicated",
                "HookExecutableExists", "HookCommandPathMatches", "HooksNotDisabled", "DataDirectoryWritable",
                "RuntimeStateValid", "SelectedProjectExists"
            ],
            checks.Select(check => check.Code));
        Assert.All(checks, check => Assert.Equal(DoctorSeverity.Pass, check.Severity));
        Assert.True(await ((IHookHealthService)environment.CreateDoctor()).IsOperationalAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RunAsync_DuplicateManagedHooks_ReportsErrorAndIsNotOperational()
    {
        using var environment = await DoctorEnvironment.CreateAsync();
        await File.WriteAllTextAsync(environment.ClaudeSettingsPath, environment.CreateClaudeSettings(hookCopies: 2));
        ClaudeDoctor doctor = environment.CreateDoctor();

        IReadOnlyDictionary<string, DoctorCheck> checks = (await doctor.RunAsync(CancellationToken.None))
            .ToDictionary(check => check.Code);

        Assert.Equal(DoctorSeverity.Error, checks["HookNotDuplicated"].Severity);
        Assert.False(await doctor.IsOperationalAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RunAsync_DisabledHooksAndMismatchedCliPath_ReportErrorsWithoutLeakingSettings()
    {
        using var environment = await DoctorEnvironment.CreateAsync();
        const string secretMarker = "PRIVATE-PERMISSION-RULE";
        await File.WriteAllTextAsync(environment.ClaudeSettingsPath,
            environment.CreateClaudeSettings(commandPath: Path.Combine(environment.RootPath, "old", "CCAutoApprove.Cli.exe"),
                disableAllHooks: true, secretMarker: secretMarker));

        IReadOnlyDictionary<string, DoctorCheck> checks = (await environment.CreateDoctor().RunAsync(CancellationToken.None))
            .ToDictionary(check => check.Code);

        Assert.Equal(DoctorSeverity.Error, checks["HookCommandPathMatches"].Severity);
        Assert.Equal(DoctorSeverity.Error, checks["HooksNotDisabled"].Severity);
        Assert.All(checks.Values, check => Assert.DoesNotContain(secretMarker, check.Message, StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_MalformedClaudeSettings_ReportsReadPassAndDependentErrorsWithoutThrowing()
    {
        using var environment = await DoctorEnvironment.CreateAsync();
        await File.WriteAllTextAsync(environment.ClaudeSettingsPath, "{\"permissions\":\"SECRET\",\"hooks\":");

        IReadOnlyDictionary<string, DoctorCheck> checks = (await environment.CreateDoctor().RunAsync(CancellationToken.None))
            .ToDictionary(check => check.Code);

        Assert.Equal(DoctorSeverity.Pass, checks["ClaudeSettingsReadable"].Severity);
        Assert.Equal(DoctorSeverity.Error, checks["ClaudeSettingsValid"].Severity);
        Assert.Equal(DoctorSeverity.Error, checks["HookInstalled"].Severity);
        Assert.DoesNotContain("SECRET", string.Join(' ', checks.Values.Select(check => check.Message)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WrongShapedClaudeSettings_ReturnsAllChecksWithoutThrowingOrLeakingContents()
    {
        using var environment = await DoctorEnvironment.CreateAsync();
        const string secretMarker = "PRIVATE-WRONG-SHAPE";
        await File.WriteAllTextAsync(environment.ClaudeSettingsPath,
            $$"""{"permissions":"{{secretMarker}}","hooks":[],"disableAllHooks":false}""");

        IReadOnlyList<DoctorCheck> checks = await environment.CreateDoctor().RunAsync(CancellationToken.None);

        Assert.Equal(10, checks.Count);
        Assert.Equal(DoctorSeverity.Error,
            Assert.Single(checks, check => check.Code == "ClaudeSettingsValid").Severity);
        Assert.All(checks, check => Assert.DoesNotContain(secretMarker, check.Message, StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(ClaudeHookManagerTests.WrongShapedSettings), MemberType = typeof(ClaudeHookManagerTests))]
    public async Task RunAsync_NestedHookShapeIsInvalid_ReturnsTenStableChecksAndIsNotOperational(string settings)
    {
        using var environment = await DoctorEnvironment.CreateAsync();
        await File.WriteAllTextAsync(environment.ClaudeSettingsPath, settings);
        ClaudeDoctor doctor = environment.CreateDoctor();

        IReadOnlyList<DoctorCheck> checks = await doctor.RunAsync(CancellationToken.None);

        Assert.Equal(
            [
                "ClaudeSettingsReadable", "ClaudeSettingsValid", "HookInstalled", "HookNotDuplicated",
                "HookExecutableExists", "HookCommandPathMatches", "HooksNotDisabled", "DataDirectoryWritable",
                "RuntimeStateValid", "SelectedProjectExists"
            ],
            checks.Select(check => check.Code));
        Assert.Equal(DoctorSeverity.Error,
            Assert.Single(checks, check => check.Code == "ClaudeSettingsValid").Severity);
        Assert.False(await doctor.IsOperationalAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RunAsync_OwnedCommandUnderNonemptyMatcher_ReportsHookNotInstalled()
    {
        using var environment = await DoctorEnvironment.CreateAsync();
        await File.WriteAllTextAsync(environment.ClaudeSettingsPath, $$"""
            {
              "model": "claude-sonnet-4-5",
              "permissions": { "allow": ["Bash(dotnet test:*)"], "deny": [] },
              "disableAllHooks": false,
              "hooks": {
                "PermissionRequest": [
                  {
                    "matcher": "Bash",
                    "hooks": [
                      { "type": "command", "command": {{System.Text.Json.JsonSerializer.Serialize($"\"{environment.CliPath}\" hook")}} }
                    ]
                  }
                ],
                "PostToolUse": []
              }
            }
            """);
        ClaudeDoctor doctor = environment.CreateDoctor();

        IReadOnlyDictionary<string, DoctorCheck> checks = (await doctor.RunAsync(CancellationToken.None))
            .ToDictionary(check => check.Code);

        Assert.Equal(DoctorSeverity.Error, checks["HookInstalled"].Severity);
        Assert.Equal(DoctorSeverity.Error, checks["HookNotDuplicated"].Severity);
        Assert.Equal(DoctorSeverity.Error, checks["HookCommandPathMatches"].Severity);
        Assert.False(await doctor.IsOperationalAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RunAsync_MissingRuntimeAndSelectedProject_AreWarningsAndDoNotMakeHookInoperable()
    {
        using var environment = await DoctorEnvironment.CreateAsync();
        File.Delete(environment.RuntimeStatePath);
        Directory.Delete(environment.SelectedProjectPath);
        ClaudeDoctor doctor = environment.CreateDoctor();

        IReadOnlyDictionary<string, DoctorCheck> checks = (await doctor.RunAsync(CancellationToken.None))
            .ToDictionary(check => check.Code);

        Assert.Equal(DoctorSeverity.Warning, checks["RuntimeStateValid"].Severity);
        Assert.Equal(DoctorSeverity.Warning, checks["SelectedProjectExists"].Severity);
        Assert.True(await doctor.IsOperationalAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RunAsync_MissingExecutable_IsAnError()
    {
        using var environment = await DoctorEnvironment.CreateAsync();
        File.Delete(environment.CliPath);

        DoctorCheck check = Assert.Single(
            await environment.CreateDoctor().RunAsync(CancellationToken.None),
            candidate => candidate.Code == "HookExecutableExists");

        Assert.Equal(DoctorSeverity.Error, check.Severity);
    }

    private sealed class DoctorEnvironment : IDisposable
    {
        private DoctorEnvironment(string rootPath)
        {
            RootPath = rootPath;
            ClaudeSettingsPath = Path.Combine(rootPath, ".claude", "settings.json");
            CliPath = Path.Combine(rootPath, "install", "CCAutoApprove.Cli.exe");
            DataDirectoryPath = Path.Combine(rootPath, "data");
            RuntimeStatePath = Path.Combine(DataDirectoryPath, "runtime.json");
            SelectedProjectPath = Path.Combine(rootPath, "selected-project");
        }

        public string RootPath { get; }
        public string ClaudeSettingsPath { get; }
        public string CliPath { get; }
        public string DataDirectoryPath { get; }
        public string RuntimeStatePath { get; }
        public string SelectedProjectPath { get; }

        public static async Task<DoctorEnvironment> CreateAsync()
        {
            string rootPath = Path.Combine(Path.GetTempPath(), "CCAutoApprove.DoctorTests", Guid.NewGuid().ToString("N"));
            var environment = new DoctorEnvironment(rootPath);
            Directory.CreateDirectory(Path.GetDirectoryName(environment.ClaudeSettingsPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(environment.CliPath)!);
            Directory.CreateDirectory(environment.DataDirectoryPath);
            Directory.CreateDirectory(environment.SelectedProjectPath);
            await File.WriteAllBytesAsync(environment.CliPath, [0x4D, 0x5A]);
            await File.WriteAllTextAsync(environment.ClaudeSettingsPath, environment.CreateClaudeSettings());
            await File.WriteAllTextAsync(environment.RuntimeStatePath, $$"""
                {
                  "schemaVersion": 1,
                  "enabled": true,
                  "processId": 1234,
                  "processStartUtc": "2026-08-17T00:00:00+00:00",
                  "instanceId": "11111111-1111-1111-1111-111111111111",
                  "heartbeatUtc": "2026-08-17T00:01:00+00:00",
                  "selectedProject": {{System.Text.Json.JsonSerializer.Serialize(environment.SelectedProjectPath)}}
                }
                """);
            return environment;
        }

        public ClaudeDoctor CreateDoctor() => new(
            ClaudeSettingsPath,
            CliPath,
            DataDirectoryPath,
            RuntimeStatePath,
            SelectedProjectPath);

        public string CreateClaudeSettings(
            int hookCopies = 1,
            string? commandPath = null,
            bool disableAllHooks = false,
            string secretMarker = "Bash(dotnet test:*)")
        {
            string command = $"\"{commandPath ?? CliPath}\" hook";
            string hook = $$"""
                {
                  "matcher": "",
                  "hooks": [
                    { "type": "command", "command": {{System.Text.Json.JsonSerializer.Serialize(command)}} }
                  ]
                }
                """;
            return $$"""
                {
                  "model": "claude-sonnet-4-5",
                  "permissions": { "allow": [{{System.Text.Json.JsonSerializer.Serialize(secretMarker)}}], "deny": [] },
                  "disableAllHooks": {{disableAllHooks.ToString().ToLowerInvariant()}},
                  "hooks": {
                    "PermissionRequest": [{{string.Join(',', Enumerable.Repeat(hook, hookCopies))}}],
                    "PostToolUse": [
                      { "matcher": "Write", "hooks": [{ "type": "command", "command": "C:\\tools\\format.exe" }] }
                    ]
                  }
                }
                """;
        }

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
    }
}
