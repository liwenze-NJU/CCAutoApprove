using CCAutoApprove.Infrastructure.Claude;

namespace CCAutoApprove.Infrastructure.Tests.Claude;

public sealed class ClaudeVersionCapabilityProbeTests
{
    [Fact]
    public void Locator_FindsCmdVisibleThroughCmdStylePathAndPathExt()
    {
        using var temp = new TemporaryDirectory();
        string command = Path.Combine(temp.Path, "claude.cmd");
        File.WriteAllText(command, "@echo off");
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = temp.Path,
            ["PATHEXT"] = ".EXE;.CMD;.BAT",
            ["CCAA_CLAUDE_PATH"] = null
        };
        var locator = new SystemClaudeExecutableLocator(
            name => environment.GetValueOrDefault(name),
            Path.Combine(temp.Path, "user"),
            Path.Combine(temp.Path, "appdata"),
            Path.Combine(temp.Path, "localappdata"));

        string candidate = Assert.Single(locator.FindCandidates());

        Assert.Equal(command, candidate, ignoreCase: true);
    }

    [Theory]
    [InlineData("2.0.44 (Claude Code)", false)]
    [InlineData("2.0.45 (Claude Code)", true)]
    [InlineData("claude version unavailable", false)]
    public async Task Probe_RequiresAtLeastPermissionRequestIntroductionVersion(
        string output,
        bool expectedSupported)
    {
        var probe = new ClaudeVersionCapabilityProbe(
            new StubLocator(@"C:\tools\claude.exe"),
            new StubRunner(new ClaudeVersionCommandResult(0, output)));

        ClaudeVersionCapability capability = await probe.CheckAsync(CancellationToken.None);

        Assert.Equal(expectedSupported, capability.IsSupported);
        Assert.Equal(expectedSupported ? output[..6] : output.StartsWith('2') ? output[..6] : null,
            capability.Version);
    }

    [Fact]
    public async Task Probe_WhenExistingCandidateCannotExecute_ReportsUnverifiableInsteadOfThrowing()
    {
        var probe = new ClaudeVersionCapabilityProbe(
            new StubLocator(@"C:\tools\broken-claude.exe"),
            new ThrowingRunner(new System.ComponentModel.Win32Exception("not executable")));

        ClaudeVersionCapability capability = await probe.CheckAsync(CancellationToken.None);

        Assert.False(capability.IsSupported);
        Assert.Null(capability.Version);
    }

    private sealed class StubLocator(params string[] candidates) : IClaudeExecutableLocator
    {
        public IReadOnlyList<string> FindCandidates() => candidates;
    }

    private sealed class StubRunner(ClaudeVersionCommandResult result) : IClaudeVersionCommandRunner
    {
        public Task<ClaudeVersionCommandResult> RunAsync(
            string executablePath,
            CancellationToken cancellationToken) => Task.FromResult(result);
    }

    private sealed class ThrowingRunner(Exception exception) : IClaudeVersionCommandRunner
    {
        public Task<ClaudeVersionCommandResult> RunAsync(
            string executablePath,
            CancellationToken cancellationToken) => Task.FromException<ClaudeVersionCommandResult>(exception);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "CCAutoApprove.Tests",
                Guid.NewGuid().ToString("N"));
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
