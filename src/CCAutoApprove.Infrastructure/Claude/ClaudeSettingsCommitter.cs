using System.Text;
using CCAutoApprove.Infrastructure.Configuration;

namespace CCAutoApprove.Infrastructure.Claude;

internal interface IClaudeSettingsCommitter
{
    Task<bool> TryCommitAsync(
        string settingsPath,
        byte[]? expectedSource,
        string replacement,
        CancellationToken cancellationToken);
}

internal interface IClaudeSettingsFileOperations
{
    Task<bool> TryReplaceAsync(
        string settingsPath,
        byte[]? expectedSource,
        string preparedPath,
        CancellationToken cancellationToken);

    Task<bool> TryDeleteAsync(
        string settingsPath,
        byte[] expectedSource,
        CancellationToken cancellationToken);
}

internal sealed class SnapshotClaudeSettingsCommitter : IClaudeSettingsCommitter
{
    private readonly AtomicFileWriter writer;
    private readonly IClaudeSettingsFileOperations fileOperations;

    public SnapshotClaudeSettingsCommitter(AtomicFileWriter? writer = null)
        : this(writer, new SystemClaudeSettingsFileOperations())
    {
    }

    internal SnapshotClaudeSettingsCommitter(
        AtomicFileWriter? writer,
        IClaudeSettingsFileOperations fileOperations)
    {
        this.writer = writer ?? new AtomicFileWriter();
        this.fileOperations = fileOperations ?? throw new ArgumentNullException(nameof(fileOperations));
    }

    public Task<bool> TryCommitAsync(
        string settingsPath,
        byte[]? expectedSource,
        string replacement,
        CancellationToken cancellationToken) =>
        TryCommitBytesAsync(
            settingsPath,
            expectedSource,
            Encoding.UTF8.GetBytes(replacement),
            cancellationToken);

    internal async Task<bool> TryCommitBytesAsync(
        string settingsPath,
        byte[]? expectedSource,
        ReadOnlyMemory<byte> replacement,
        CancellationToken cancellationToken)
    {
        string preparedPath = CreatePreparedPath(settingsPath);
        Exception? primaryException = null;
        try
        {
            await writer.WriteAllBytesAsync(preparedPath, replacement, cancellationToken);
            return await fileOperations.TryReplaceAsync(
                settingsPath,
                expectedSource,
                preparedPath,
                cancellationToken);
        }
        catch (Exception exception)
        {
            primaryException = exception;
            throw;
        }
        finally
        {
            try
            {
                if (File.Exists(preparedPath))
                {
                    File.Delete(preparedPath);
                }
            }
            catch when (primaryException is not null)
            {
                // Cleanup must never replace the commit failure.
            }
        }
    }

    internal async Task<bool> TryDeleteAsync(
        string settingsPath,
        byte[] expectedSource,
        CancellationToken cancellationToken) =>
        await fileOperations.TryDeleteAsync(
            settingsPath,
            expectedSource,
            cancellationToken);

    private static string CreatePreparedPath(string settingsPath)
    {
        string directory = Path.GetDirectoryName(settingsPath)
            ?? throw new ArgumentException(
                "The Claude settings path must include a parent directory.",
                nameof(settingsPath));
        return Path.Combine(
            directory,
            $"{Path.GetFileName(settingsPath)}.{Guid.NewGuid():N}.replacement.tmp");
    }
}

internal sealed class SystemClaudeSettingsFileOperations(
    Func<CancellationToken, Task>? afterComparison = null)
    : IClaudeSettingsFileOperations
{
    private const int MaximumMutationAttempts = 20;
    private static readonly TimeSpan MutationRetryDelay = TimeSpan.FromMilliseconds(5);
    private readonly Func<CancellationToken, Task>? afterComparison = afterComparison;

    public async Task<bool> TryReplaceAsync(
        string settingsPath,
        byte[]? expectedSource,
        string preparedPath,
        CancellationToken cancellationToken)
    {
        if (expectedSource is null)
        {
            return await TryCreateAsync(
                settingsPath,
                preparedPath,
                cancellationToken);
        }

        for (int attempt = 0; ; attempt++)
        {
            FileStream ownershipLease;
            try
            {
                ownershipLease = OpenOwnershipLease(settingsPath);
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }
            catch (Exception exception) when (
                IsRetryableContention(exception) && attempt < MaximumMutationAttempts)
            {
                await Task.Delay(MutationRetryDelay, cancellationToken);
                continue;
            }

            await using (ownershipLease)
            {
                byte[] currentSource = await ReadSourceAsync(
                    ownershipLease,
                    cancellationToken);
                if (!currentSource.AsSpan().SequenceEqual(expectedSource))
                {
                    return false;
                }

                if (afterComparison is not null)
                {
                    await afterComparison(cancellationToken);
                }

                try
                {
                    File.Replace(preparedPath, settingsPath, destinationBackupFileName: null);
                    return true;
                }
                catch (Exception exception) when (
                    IsRetryableContention(exception) && attempt < MaximumMutationAttempts)
                {
                    // Reopen and compare again so a retry never relies on stale ownership.
                }
            }

            await Task.Delay(MutationRetryDelay, cancellationToken);
        }
    }

    public async Task<bool> TryDeleteAsync(
        string settingsPath,
        byte[] expectedSource,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            FileStream ownershipLease;
            try
            {
                ownershipLease = OpenOwnershipLease(settingsPath);
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }
            catch (Exception exception) when (
                IsRetryableContention(exception) && attempt < MaximumMutationAttempts)
            {
                await Task.Delay(MutationRetryDelay, cancellationToken);
                continue;
            }

            await using (ownershipLease)
            {
                byte[] currentSource = await ReadSourceAsync(
                    ownershipLease,
                    cancellationToken);
                if (!currentSource.AsSpan().SequenceEqual(expectedSource))
                {
                    return false;
                }

                if (afterComparison is not null)
                {
                    await afterComparison(cancellationToken);
                }

                try
                {
                    File.Delete(settingsPath);
                    return true;
                }
                catch (Exception exception) when (
                    IsRetryableContention(exception) && attempt < MaximumMutationAttempts)
                {
                    // Reopen and compare again so a retry never relies on stale ownership.
                }
            }

            await Task.Delay(MutationRetryDelay, cancellationToken);
        }
    }

    private async Task<bool> TryCreateAsync(
        string settingsPath,
        string preparedPath,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(settingsPath))
            {
                return false;
            }

            if (afterComparison is not null)
            {
                await afterComparison(cancellationToken);
            }

            try
            {
                File.Move(preparedPath, settingsPath, overwrite: false);
                return true;
            }
            catch (IOException) when (File.Exists(settingsPath))
            {
                return false;
            }
            catch (Exception exception) when (
                IsRetryableContention(exception) && attempt < MaximumMutationAttempts)
            {
                await Task.Delay(MutationRetryDelay, cancellationToken);
            }
        }
    }

    private static FileStream OpenOwnershipLease(string settingsPath) =>
        new(
            settingsPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static async Task<byte[]> ReadSourceAsync(
        FileStream source,
        CancellationToken cancellationToken)
    {
        using var buffer = source.Length <= int.MaxValue
            ? new MemoryStream(capacity: (int)source.Length)
            : new MemoryStream();
        await source.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }

    private static bool IsRetryableContention(Exception exception) =>
        exception is IOException or UnauthorizedAccessException;
}
