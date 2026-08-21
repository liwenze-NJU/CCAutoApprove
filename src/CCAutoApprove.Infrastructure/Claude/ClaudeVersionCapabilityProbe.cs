using System.Diagnostics;
using System.Text.RegularExpressions;

namespace CCAutoApprove.Infrastructure.Claude;

internal sealed record ClaudeVersionCapability(bool IsSupported, string? Version);

internal interface IClaudeVersionCapabilityProbe
{
    Task<ClaudeVersionCapability> CheckAsync(CancellationToken cancellationToken);
}

internal interface IClaudeExecutableLocator
{
    IReadOnlyList<string> FindCandidates();
}

internal interface IClaudeVersionCommandRunner
{
    Task<ClaudeVersionCommandResult> RunAsync(
        string executablePath,
        CancellationToken cancellationToken);
}

internal sealed record ClaudeVersionCommandResult(int ExitCode, string Output);

internal sealed class ClaudeVersionCapabilityProbe : IClaudeVersionCapabilityProbe
{
    private static readonly Version MinimumSupportedVersion = new(2, 0, 45);
    private static readonly Regex VersionPattern = new(
        @"(?<!\d)(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)(?!\d)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly IClaudeExecutableLocator locator;
    private readonly IClaudeVersionCommandRunner runner;

    public ClaudeVersionCapabilityProbe()
        : this(new SystemClaudeExecutableLocator(), new ProcessClaudeVersionCommandRunner())
    {
    }

    internal ClaudeVersionCapabilityProbe(
        IClaudeExecutableLocator locator,
        IClaudeVersionCommandRunner runner)
    {
        this.locator = locator ?? throw new ArgumentNullException(nameof(locator));
        this.runner = runner ?? throw new ArgumentNullException(nameof(runner));
    }

    public async Task<ClaudeVersionCapability> CheckAsync(CancellationToken cancellationToken)
    {
        foreach (string candidate in locator.FindCandidates())
        {
            cancellationToken.ThrowIfCancellationRequested();
            ClaudeVersionCommandResult result;
            try
            {
                result = await runner.RunAsync(candidate, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or InvalidOperationException
                                              or System.ComponentModel.Win32Exception)
            {
                continue;
            }

            if (result.ExitCode != 0 || !TryParseVersion(result.Output, out Version version))
            {
                continue;
            }

            return new ClaudeVersionCapability(
                version >= MinimumSupportedVersion,
                version.ToString(3));
        }

        return new ClaudeVersionCapability(false, null);
    }

    private static bool TryParseVersion(string output, out Version version)
    {
        Match match = VersionPattern.Match(output ?? string.Empty);
        if (match.Success
            && int.TryParse(match.Groups["major"].Value, out int major)
            && int.TryParse(match.Groups["minor"].Value, out int minor)
            && int.TryParse(match.Groups["patch"].Value, out int patch))
        {
            version = new Version(major, minor, patch);
            return true;
        }

        version = null!;
        return false;
    }
}

internal sealed class SystemClaudeExecutableLocator : IClaudeExecutableLocator
{
    private readonly Func<string, string?> getEnvironmentVariable;
    private readonly string userProfile;
    private readonly string appData;
    private readonly string localAppData;

    public SystemClaudeExecutableLocator()
        : this(
            Environment.GetEnvironmentVariable,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
    {
    }

    internal SystemClaudeExecutableLocator(
        Func<string, string?> getEnvironmentVariable,
        string userProfile,
        string appData,
        string localAppData)
    {
        this.getEnvironmentVariable = getEnvironmentVariable
            ?? throw new ArgumentNullException(nameof(getEnvironmentVariable));
        this.userProfile = userProfile;
        this.appData = appData;
        this.localAppData = localAppData;
    }

    public IReadOnlyList<string> FindCandidates()
    {
        var candidates = new List<string>();
        AddIfFile(candidates, getEnvironmentVariable("CCAA_CLAUDE_PATH"));

        string[] extensions = GetExecutableExtensions();
        string? searchPath = getEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(searchPath))
        {
            foreach (string directory in searchPath.Split(
                         Path.PathSeparator,
                         StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (string extension in extensions)
                {
                    AddIfFile(candidates, Path.Combine(directory, "claude" + extension));
                }
            }
        }

        AddIfFile(candidates, Path.Combine(userProfile, ".local", "bin", "claude.exe"));
        AddIfFile(candidates, Path.Combine(appData, "npm", "claude.cmd"));
        AddIfFile(candidates, Path.Combine(localAppData, "Programs", "Claude", "claude.exe"));
        return candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private string[] GetExecutableExtensions()
    {
        string? configured = getEnvironmentVariable("PATHEXT");
        string[] extensions = string.IsNullOrWhiteSpace(configured)
            ? [".EXE", ".CMD", ".BAT", ".COM"]
            : configured.Split(
                ';',
                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return extensions
            .Select(extension => extension.StartsWith('.') ? extension : "." + extension)
            .ToArray();
    }

    private static void AddIfFile(ICollection<string> candidates, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            string fullPath = Path.GetFullPath(path.Trim('"'));
            if (File.Exists(fullPath))
            {
                candidates.Add(fullPath);
            }
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or NotSupportedException
                                          or PathTooLongException)
        {
        }
    }
}

internal sealed class ProcessClaudeVersionCommandRunner : IClaudeVersionCommandRunner
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(1);

    public async Task<ClaudeVersionCommandResult> RunAsync(
        string executablePath,
        CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = CreateStartInfo(executablePath) };
        if (!process.Start())
        {
            return new ClaudeVersionCommandResult(-1, string.Empty);
        }

        Task<string> stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        Task<string> stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CommandTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            string[] output = await Task.WhenAll(stdout, stderr).WaitAsync(ShutdownTimeout);
            return new ClaudeVersionCommandResult(
                process.ExitCode,
                string.Join(Environment.NewLine, output));
        }
        catch (OperationCanceledException)
        {
            TryKillTree(process);
            _ = process.WaitForExit((int)ShutdownTimeout.TotalMilliseconds);
            await DrainPipesAsync(stdout, stderr);
            cancellationToken.ThrowIfCancellationRequested();
            return new ClaudeVersionCommandResult(-1, string.Empty);
        }
        catch (TimeoutException)
        {
            TryKillTree(process);
            _ = process.WaitForExit((int)ShutdownTimeout.TotalMilliseconds);
            await DrainPipesAsync(stdout, stderr);
            return new ClaudeVersionCommandResult(-1, string.Empty);
        }
    }

    private static ProcessStartInfo CreateStartInfo(string executablePath)
    {
        bool isCommandScript = string.Equals(Path.GetExtension(executablePath), ".cmd", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetExtension(executablePath), ".bat", StringComparison.OrdinalIgnoreCase);
        var startInfo = new ProcessStartInfo
        {
            FileName = isCommandScript
                ? Environment.GetEnvironmentVariable("ComSpec")
                    ?? Path.Combine(Environment.SystemDirectory, "cmd.exe")
                : executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (isCommandScript)
        {
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/s");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add($"\"\"{executablePath}\" --version\"");
        }
        else
        {
            startInfo.ArgumentList.Add("--version");
        }

        return startInfo;
    }

    private static void TryKillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                          or System.ComponentModel.Win32Exception
                                          or NotSupportedException)
        {
        }
    }

    private static async Task DrainPipesAsync(Task<string> stdout, Task<string> stderr)
    {
        try
        {
            await Task.WhenAll(stdout, stderr).WaitAsync(ShutdownTimeout);
        }
        catch (Exception exception) when (exception is TimeoutException
                                          or IOException
                                          or ObjectDisposedException)
        {
        }
    }
}
