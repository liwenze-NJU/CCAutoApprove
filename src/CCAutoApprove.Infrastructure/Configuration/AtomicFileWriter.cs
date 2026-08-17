using System.Text;

namespace CCAutoApprove.Infrastructure.Configuration;

public sealed class AtomicFileWriter
{
    private readonly SemaphoreSlim writeLock = new(1, 1);

    public async Task WriteAllTextAsync(string targetPath, string contents, CancellationToken cancellationToken)
    {
        await writeLock.WaitAsync(cancellationToken);
        string? tempPath = null;

        try
        {
            string directory = Path.GetDirectoryName(targetPath)
                ?? throw new ArgumentException("A target path must include a parent directory.", nameof(targetPath));
            Directory.CreateDirectory(directory);
            tempPath = Path.Combine(directory, $"{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");

            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                await writer.WriteAsync(contents.AsMemory(), cancellationToken);
                await writer.FlushAsync(cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            await MoveIntoPlaceAsync(tempPath, targetPath, cancellationToken);
        }
        finally
        {
            if (tempPath is not null && File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            writeLock.Release();
        }
    }

    private static async Task MoveIntoPlaceAsync(string tempPath, string targetPath, CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(tempPath, targetPath, overwrite: true);
                return;
            }
            catch (UnauthorizedAccessException) when (attempt < 20)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(5), cancellationToken);
            }
        }
    }
}
