using System.Diagnostics;
using System.Text.Json;

namespace CCAutoApprove.Infrastructure.Tests.Packaging;

public sealed class PublishedLayoutTests
{
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(2);

    [PublishedLayoutFact]
    public async Task PublishedLayout_ContainsRunnableCliAndVersionedApp()
    {
        string publishRoot = GetPublishRootOrSkip();
        string appPath = Path.Combine(publishRoot, "app", "CCAutoApprove.App.exe");
        string cliPath = Path.Combine(publishRoot, "cli", "CCAutoApprove.Cli.exe");

        Assert.True(File.Exists(appPath), $"Published App was not found at {appPath}.");
        Assert.True(File.Exists(cliPath), $"Published CLI was not found at {cliPath}.");

        string expectedVersion = Environment.GetEnvironmentVariable("CCAA_PUBLISH_VERSION")
            ?? "0.1.0";
        FileVersionInfo appVersion = FileVersionInfo.GetVersionInfo(appPath);
        Assert.Equal("CCAutoApprove", appVersion.ProductName);
        Assert.Equal(expectedVersion, appVersion.ProductVersion);

        await using var sandbox = PublishedCliSandbox.Create();
        ProcessResult result = await sandbox.RunStatusAsync(cliPath);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StandardError);
        using JsonDocument status = JsonDocument.Parse(result.StandardOutput);
        Assert.False(status.RootElement.GetProperty("hookInstalled").GetBoolean());
        Assert.Equal("Missing", status.RootElement.GetProperty("runtimeState").GetString());
        Assert.False(status.RootElement.GetProperty("autoApproveEnabled").GetBoolean());
    }

    [Fact]
    public void InstallerScript_DefinesPerUserInstallAndFailSafeUninstall()
    {
        string repositoryRoot = FindRepositoryRoot();
        string installerPath = Path.Combine(repositoryRoot, "installer", "CCAutoApprove.iss");

        Assert.True(File.Exists(installerPath), $"Installer script was not found at {installerPath}.");
        string script = File.ReadAllText(installerPath);

        Assert.Contains("AppId={{6B4F8851-B57E-48C8-B922-CEAA7C0AC5AD}", script);
        Assert.Contains("PrivilegesRequired=lowest", script);
        Assert.Contains("DefaultDirName={localappdata}\\Programs\\CCAutoApprove", script);
        Assert.Contains(
            "Source: \"..\\artifacts\\publish\\win-x64\\app\\*\"; DestDir: \"{app}\\app\"",
            script);
        Assert.Contains(
            "Source: \"..\\artifacts\\publish\\win-x64\\cli\\*\"; DestDir: \"{app}\\cli\"",
            script);
        Assert.Contains(
            "Filename: \"{app}\\cli\\CCAutoApprove.Cli.exe\"; Parameters: \"uninstall\"",
            script);
        Assert.Contains(
            "RegDeleteValue(HKCU, 'Software\\Microsoft\\Windows\\CurrentVersion\\Run', "
            + "'CCAutoApprove')",
            script);
        Assert.Contains("DeleteUserData := MsgBox(", script);
        Assert.Contains("= IDYES;", script);
        Assert.Contains("if DeleteUserData then", script);
        Assert.Contains("DelTree(ExpandConstant('{localappdata}\\CCAutoApprove')", script);

        int removeStartup = script.IndexOf("RegDeleteValue", StringComparison.Ordinal);
        int deleteFiles = script.IndexOf("usPostUninstall", StringComparison.Ordinal);
        Assert.True(removeStartup >= 0 && removeStartup < deleteFiles,
            "The HKCU startup value must be removed before post-uninstall data/file cleanup.");
    }

    [Fact]
    public void PublishedLayoutFact_WhenPublishRootIsAbsent_Skips()
    {
        var attribute = new PublishedLayoutFactAttribute(configuredPublishRoot: null);

        Assert.Equal(
            "CCAA_PUBLISH_ROOT is not set; run this test against a published layout.",
            attribute.Skip);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void PublishedLayoutFact_WhenPublishRootIsBlank_RunsAndRejectsRoot(string configuredPublishRoot)
    {
        var attribute = new PublishedLayoutFactAttribute(configuredPublishRoot);

        Assert.Null(attribute.Skip);
        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => GetPublishRoot(configuredPublishRoot));
        Assert.Contains("CCAA_PUBLISH_ROOT", exception.Message, StringComparison.Ordinal);
    }

    private static string GetPublishRootOrSkip()
    {
        return GetPublishRoot(Environment.GetEnvironmentVariable("CCAA_PUBLISH_ROOT"));
    }

    internal static string GetPublishRoot(string? configuredPublishRoot)
    {
        if (configuredPublishRoot is null)
        {
            throw new InvalidOperationException(
                "CCAA_PUBLISH_ROOT was removed after test discovery.");
        }

        if (string.IsNullOrWhiteSpace(configuredPublishRoot))
        {
            throw new ArgumentException(
                "CCAA_PUBLISH_ROOT must not be empty or whitespace.",
                nameof(configuredPublishRoot));
        }

        return Path.GetFullPath(configuredPublishRoot);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CCAutoApprove.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private sealed class PublishedCliSandbox : IAsyncDisposable
    {
        private readonly string rootDirectory;
        private readonly string dataDirectory;
        private readonly string claudeSettingsPath;

        private PublishedCliSandbox(
            string rootDirectory,
            string dataDirectory,
            string claudeSettingsPath)
        {
            this.rootDirectory = rootDirectory;
            this.dataDirectory = dataDirectory;
            this.claudeSettingsPath = claudeSettingsPath;
        }

        public static PublishedCliSandbox Create()
        {
            string root = Path.Combine(
                Path.GetTempPath(), $"ccautoapprove-published-{Guid.NewGuid():N}");
            string data = Path.Combine(root, "data");
            string claudeSettings = Path.Combine(root, "claude", "settings.json");
            Directory.CreateDirectory(data);
            Directory.CreateDirectory(Path.GetDirectoryName(claudeSettings)!);
            return new PublishedCliSandbox(root, data, claudeSettings);
        }

        public async Task<ProcessResult> RunStatusAsync(string cliPath)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = cliPath,
                Arguments = "status",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = rootDirectory
            };
            startInfo.Environment["CCAA_DATA_DIR"] = dataDirectory;
            startInfo.Environment["CCAA_CLAUDE_SETTINGS_PATH"] = claudeSettingsPath;

            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The published CLI could not be started.");
            Task<string> outputDrain = process.StandardOutput.ReadToEndAsync();
            Task<string> errorDrain = process.StandardError.ReadToEndAsync();

            try
            {
                using var timeout = new CancellationTokenSource(ProcessTimeout);
                await process.WaitForExitAsync(timeout.Token);
                await Task.WhenAll(outputDrain, errorDrain).WaitAsync(timeout.Token);
            }
            catch
            {
                await KillAndWaitAsync(process);
                throw;
            }
            finally
            {
                if (!process.HasExited)
                {
                    await KillAndWaitAsync(process);
                }
            }

            return new ProcessResult(
                process.ExitCode,
                await outputDrain,
                await errorDrain);
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(rootDirectory))
            {
                string temporaryPrefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string resolvedRoot = Path.GetFullPath(rootDirectory);
                string leaf = Path.GetFileName(
                    Path.TrimEndingDirectorySeparator(resolvedRoot));
                if (!resolvedRoot.StartsWith(temporaryPrefix, StringComparison.OrdinalIgnoreCase)
                    || !leaf.StartsWith("ccautoapprove-published-", StringComparison.Ordinal)
                    || (File.GetAttributes(resolvedRoot) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException(
                        $"Refusing to delete unexpected test directory {resolvedRoot}.");
                }

                Directory.Delete(rootDirectory, recursive: true);
            }

            return ValueTask.CompletedTask;
        }

        private static async Task KillAndWaitAsync(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }

            try
            {
                using var cleanup = new CancellationTokenSource(CleanupTimeout);
                await process.WaitForExitAsync(cleanup.Token);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private sealed record ProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}

public sealed class PublishedLayoutFactAttribute : FactAttribute
{
    public PublishedLayoutFactAttribute()
        : this(Environment.GetEnvironmentVariable("CCAA_PUBLISH_ROOT"))
    {
    }

    internal PublishedLayoutFactAttribute(string? configuredPublishRoot)
    {
        if (configuredPublishRoot is null)
        {
            Skip = "CCAA_PUBLISH_ROOT is not set; run this test against a published layout.";
        }
    }
}
