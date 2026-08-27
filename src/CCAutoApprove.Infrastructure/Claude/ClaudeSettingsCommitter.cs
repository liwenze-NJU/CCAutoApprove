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
    Func<CancellationToken, Task>? afterComparison = null,
    Func<CancellationToken, Task>? afterTruncation = null,
    Func<CancellationToken, Task>? afterDeleteOwnershipReleased = null)
    : IClaudeSettingsFileOperations
{
    private const int MaximumMutationAttempts = 20;
    private static readonly TimeSpan MutationRetryDelay = TimeSpan.FromMilliseconds(5);
    private readonly Func<CancellationToken, Task>? afterTruncation = afterTruncation;
    private readonly Func<CancellationToken, Task>? afterDeleteOwnershipReleased =
        afterDeleteOwnershipReleased;
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

        byte[] replacementSource = await File.ReadAllBytesAsync(
            preparedPath,
            cancellationToken);
        for (int attempt = 0; ; attempt++)
        {
            FileStream ownershipLease;
            try
            {
                ownershipLease = OpenRewriteOwnershipLease(settingsPath);
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

                cancellationToken.ThrowIfCancellationRequested();
                await RewriteOwnedFileAsync(
                    ownershipLease,
                    replacementSource,
                    expectedSource,
                    cancellationToken);
                return true;
            }
        }
    }

    public async Task<bool> TryDeleteAsync(
        string settingsPath,
        byte[] expectedSource,
        CancellationToken cancellationToken)
    {
        string quarantinePath = CreateDeleteQuarantinePath(settingsPath);
        for (int attempt = 0; ; attempt++)
        {
            FileStream ownershipLease;
            try
            {
                ownershipLease = OpenDeleteOwnershipLease(settingsPath);
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

                cancellationToken.ThrowIfCancellationRequested();
            }

            if (afterDeleteOwnershipReleased is not null)
            {
                await afterDeleteOwnershipReleased(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
            }

            try
            {
                File.Move(settingsPath, quarantinePath, overwrite: false);
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

            return FinalizeQuarantinedDelete(
                settingsPath,
                quarantinePath,
                expectedSource);
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

    private static FileStream OpenRewriteOwnershipLease(string settingsPath) =>
        new(
            settingsPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.SequentialScan | FileOptions.WriteThrough);

    private static FileStream OpenDeleteOwnershipLease(string settingsPath) =>
        new(
            settingsPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.SequentialScan);

    private async Task RewriteOwnedFileAsync(
        FileStream destination,
        ReadOnlyMemory<byte> replacement,
        byte[] original,
        CancellationToken cancellationToken)
    {
        try
        {
            destination.Position = 0;
            destination.SetLength(0);
            if (afterTruncation is not null)
            {
                await afterTruncation(cancellationToken);
            }

            destination.Write(replacement.Span);
            destination.Flush(flushToDisk: true);
        }
        catch (Exception mutationException)
        {
            try
            {
                RestoreOwnedFile(destination, original);
            }
            catch (Exception recoveryException)
            {
                throw new IOException(
                    "Claude settings rewrite failed and the exact original bytes could not be restored while ownership remained exclusive.",
                    new AggregateException(mutationException, recoveryException));
            }

            throw;
        }
    }

    private static void RestoreOwnedFile(FileStream destination, byte[] original)
    {
        destination.Position = 0;
        destination.SetLength(0);
        destination.Write(original.AsSpan());
        destination.Flush(flushToDisk: true);
    }

    private static string CreateDeleteQuarantinePath(string settingsPath)
    {
        string directory = Path.GetDirectoryName(settingsPath)
            ?? throw new ArgumentException(
                "The Claude settings path must include a parent directory.",
                nameof(settingsPath));
        return Path.Combine(
            directory,
            $"{Path.GetFileName(settingsPath)}.{Guid.NewGuid():N}.ccautoapprove-delete-quarantine");
    }

    private static bool FinalizeQuarantinedDelete(
        string settingsPath,
        string quarantinePath,
        byte[] expectedSource)
    {
        byte[] movedSource;
        try
        {
            movedSource = File.ReadAllBytes(quarantinePath);
        }
        catch
        {
            if (!TryRestoreQuarantinedFile(settingsPath, quarantinePath, out Exception? recoveryException))
            {
                throw QuarantineRecoveryFailure(
                    settingsPath,
                    quarantinePath,
                    recoveryException!);
            }

            return false;
        }

        if (!movedSource.AsSpan().SequenceEqual(expectedSource))
        {
            if (!TryRestoreQuarantinedFile(settingsPath, quarantinePath, out Exception? recoveryException))
            {
                throw QuarantineRecoveryFailure(
                    settingsPath,
                    quarantinePath,
                    recoveryException!);
            }

            return false;
        }

        try
        {
            File.Delete(quarantinePath);
            return true;
        }
        catch
        {
            if (!TryRestoreQuarantinedFile(settingsPath, quarantinePath, out Exception? recoveryException))
            {
                throw QuarantineRecoveryFailure(
                    settingsPath,
                    quarantinePath,
                    recoveryException!);
            }

            return false;
        }
    }

    private static bool TryRestoreQuarantinedFile(
        string settingsPath,
        string quarantinePath,
        out Exception? recoveryException)
    {
        try
        {
            File.Move(quarantinePath, settingsPath, overwrite: false);
            recoveryException = null;
            return true;
        }
        catch (Exception exception)
        {
            recoveryException = exception;
            return false;
        }
    }

    private static IOException QuarantineRecoveryFailure(
        string settingsPath,
        string quarantinePath,
        Exception recoveryException) =>
        new(
            $"Claude settings bytes were preserved at '{quarantinePath}' because '{settingsPath}' changed again before safe delete recovery; no file was overwritten.",
            recoveryException);

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
