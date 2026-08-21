using System.Text;

namespace CCAutoApprove.Infrastructure.Configuration;

internal interface IAtomicFileOperations
{
    bool Exists(string path);
    void Delete(string path);
    void Move(string sourcePath, string targetPath);
}

internal sealed class SystemAtomicFileOperations : IAtomicFileOperations
{
    public bool Exists(string path) => File.Exists(path);
    public void Delete(string path) => File.Delete(path);
    public void Move(string sourcePath, string targetPath) =>
        File.Move(sourcePath, targetPath, overwrite: true);
}

public sealed class AtomicFileWriter
{
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private readonly IAtomicFileOperations fileOperations;

    public AtomicFileWriter()
        : this(new SystemAtomicFileOperations())
    {
    }

    internal AtomicFileWriter(IAtomicFileOperations fileOperations)
    {
        this.fileOperations = fileOperations
            ?? throw new ArgumentNullException(nameof(fileOperations));
    }

    public async Task WriteAllTextAsync(string targetPath, string contents, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(contents);
        await WriteAllBytesAsync(targetPath, Encoding.UTF8.GetBytes(contents), cancellationToken);
    }

    internal async Task WriteAllBytesAsync(
        string targetPath,
        ReadOnlyMemory<byte> contents,
        CancellationToken cancellationToken)
    {
        await writeLock.WaitAsync(cancellationToken);
        string? tempPath = null;
        Exception? primaryException = null;

        try
        {
            string directory = Path.GetDirectoryName(targetPath)
                ?? throw new ArgumentException("A target path must include a parent directory.", nameof(targetPath));
            Directory.CreateDirectory(directory);
            tempPath = Path.Combine(directory, $"{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");

            await using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                useAsync: true))
            {
                await stream.WriteAsync(contents, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            await MoveIntoPlaceAsync(tempPath, targetPath, cancellationToken);
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
                if (tempPath is not null && fileOperations.Exists(tempPath))
                {
                    fileOperations.Delete(tempPath);
                }
            }
            catch when (primaryException is not null)
            {
                // Cleanup must never replace the failure that made the write unsuccessful.
            }
            finally
            {
                writeLock.Release();
            }
        }
    }

    private async Task MoveIntoPlaceAsync(
        string tempPath,
        string targetPath,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                fileOperations.Move(tempPath, targetPath);
                return;
            }
            catch (UnauthorizedAccessException) when (attempt < 20)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(5), cancellationToken);
            }
        }
    }
}
