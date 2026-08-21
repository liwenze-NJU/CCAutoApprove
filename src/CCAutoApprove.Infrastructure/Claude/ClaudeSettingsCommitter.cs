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
    private readonly AtomicFileWriter writer = writer ?? new AtomicFileWriter();

    public async Task<bool> TryCommitAsync(
        string settingsPath,
        byte[]? expectedSource,
        string replacement,
        CancellationToken cancellationToken)
    {
        byte[]? currentSource = await ReadSourceAsync(settingsPath, cancellationToken);
        if (!SourcesEqual(currentSource, expectedSource))
        {
            return false;
        }

        await writer.WriteAllTextAsync(settingsPath, replacement, cancellationToken);
        return true;
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
}
