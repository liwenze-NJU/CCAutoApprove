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
        Assert.Contains("ArchitecturesAllowed=x64compatible", script);
        Assert.Contains("DefaultDirName={localappdata}\\Programs\\CCAutoApprove", script);
        Assert.Contains(
            "Source: \"..\\artifacts\\publish\\win-x64\\app\\*\"; DestDir: \"{app}\\app\"",
            script);
        Assert.Contains(
            "Source: \"..\\artifacts\\publish\\win-x64\\cli\\*\"; DestDir: \"{app}\\cli\"",
            script);
        Assert.DoesNotContain("[UninstallRun]", script, StringComparison.Ordinal);
        Assert.Contains("function StopInstalledApplication: Boolean;", script);
        Assert.Contains("{app}\\app\\CCAutoApprove.App.exe", script);
        Assert.Contains("Get-CimInstance Win32_Process", script);
        Assert.Contains("$_.ExecutablePath", script);
        Assert.Contains("Stop-Process", script);
        Assert.Contains("if not StopInstalledApplication then", script);
        Assert.Contains("Abort;", script);
        Assert.Contains("function RunHookCleanup: Boolean;", script);
        Assert.Contains("Exec(CliPath, 'uninstall'", script);
        Assert.Contains("ResultCode <> 0", script);
        Assert.Contains("%USERPROFILE%\\.claude\\settings.json", script);
        Assert.Contains(
            "RegDeleteValue(HKCU, 'Software\\Microsoft\\Windows\\CurrentVersion\\Run', "
            + "'CCAutoApprove')",
            script);
        Assert.Contains("DeleteUserData := MsgBox(", script);
        Assert.Contains("= IDYES;", script);
        Assert.Contains("if DeleteUserData then", script);
        Assert.Contains("if not DelTree(ExpandConstant('{localappdata}\\CCAutoApprove')", script);

        int prepareStart = script.IndexOf("procedure PrepareUninstall;", StringComparison.Ordinal);
        int prepareEnd = script.IndexOf("procedure CurUninstallStepChanged", StringComparison.Ordinal);
        Assert.True(prepareStart >= 0 && prepareEnd > prepareStart);
        string prepare = script[prepareStart..prepareEnd];
        int stopApp = prepare.IndexOf("StopInstalledApplication", StringComparison.Ordinal);
        int removeHook = prepare.IndexOf("RunHookCleanup", StringComparison.Ordinal);
        int removeStartup = prepare.IndexOf("RegDeleteValue", StringComparison.Ordinal);
        Assert.True(stopApp >= 0 && stopApp < removeHook && removeHook < removeStartup,
            "Shutdown, Hook cleanup, and startup removal must run in that order before file deletion.");

        int postUninstall = script.IndexOf("CurUninstallStep = usPostUninstall", StringComparison.Ordinal);
        int dataPrompt = script.IndexOf("DeleteUserData := MsgBox(", StringComparison.Ordinal);
        int deleteData = script.IndexOf("if not DelTree", StringComparison.Ordinal);
        Assert.True(postUninstall >= 0 && postUninstall < dataPrompt && dataPrompt < deleteData,
            "The user-data choice and checked deletion must happen after program-file uninstall.");
    }

    [Fact]
    public void InstallerScript_StringEscapeHelpersMutateMutableResultsWithoutAssigningMatchCounts()
    {
        string repositoryRoot = FindRepositoryRoot();
        string script = File.ReadAllText(
                Path.Combine(repositoryRoot, "installer", "CCAutoApprove.iss"))
            .ReplaceLineEndings("\n");

        Assert.DoesNotContain("Result := StringChangeEx(", script, StringComparison.Ordinal);
        Assert.Contains(
            """
            function EscapePercent(const Value: String): String;
            begin
              Result := Value;
              StringChangeEx(Result, '%', '%%', True);
            end;
            """,
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            """
            function EscapePowerShellSingleQuoted(const Value: String): String;
            begin
              Result := Value;
              StringChangeEx(Result, '''', '''''', True);
            end;
            """,
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void KnownLimitations_HasCurrentReadmeBacklinkWithoutFutureWorkPromise()
    {
        string repositoryRoot = FindRepositoryRoot();
        string content = File.ReadAllText(
            Path.Combine(repositoryRoot, "docs", "known-limitations.md"));

        Assert.Contains("[README](../README.md)", content, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "README 将在后续文档整理任务中链接到本页。",
            content,
            StringComparison.Ordinal);
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
