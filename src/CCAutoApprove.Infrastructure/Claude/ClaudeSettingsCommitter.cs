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

internal sealed class SnapshotClaudeSettingsCommitter(AtomicFileWriter? writer = null)
    : IClaudeSettingsCommitter
{
    private const int MaximumMoveAttempts = 20;
    private static readonly TimeSpan MoveRetryDelay = TimeSpan.FromMilliseconds(5);
    private readonly AtomicFileWriter writer = writer ?? new AtomicFileWriter();

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
            for (int attempt = 0; ; attempt++)
            {
                byte[]? currentSource = await ReadSourceAsync(settingsPath, cancellationToken);
                if (!SourcesEqual(currentSource, expectedSource))
                {
                    return false;
                }

                try
                {
                    File.Move(preparedPath, settingsPath, overwrite: true);
                    return true;
                }
                catch (UnauthorizedAccessException) when (attempt < MaximumMoveAttempts)
                {
                    await Task.Delay(MoveRetryDelay, cancellationToken);
                }
            }
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
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            byte[]? currentSource = await ReadSourceAsync(settingsPath, cancellationToken);
            if (!SourcesEqual(currentSource, expectedSource))
            {
                return false;
            }

            try
            {
                File.Delete(settingsPath);
                return true;
            }
            catch (UnauthorizedAccessException) when (attempt < MaximumMoveAttempts)
            {
                await Task.Delay(MoveRetryDelay, cancellationToken);
            }
        }
    }

    private static async Task<byte[]?> ReadSourceAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            return await File.ReadAllBytesAsync(path, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static bool SourcesEqual(byte[]? left, byte[]? right) =>
        left is null ? right is null : right is not null && left.AsSpan().SequenceEqual(right);

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
