using CCAutoApprove.Infrastructure.Configuration;

namespace CCAutoApprove.Infrastructure.Tests.Configuration;

public sealed class AtomicFileWriterTests
{
    [Fact]
    public async Task WriteAllTextAsync_CleanupFailureAfterPrimaryFailure_PreservesPrimaryAndReleasesLock()
    {
        using var temp = new TemporaryDirectory();
        string target = Path.Combine(temp.Path, "state.json");
        var operations = new PrimaryAndCleanupFailureOperations();
        var writer = new AtomicFileWriter(operations);

        IOException exception = await Assert.ThrowsAsync<IOException>(() =>
            writer.WriteAllTextAsync(target, "first", CancellationToken.None));
        await writer.WriteAllTextAsync(target, "second", CancellationToken.None);

        Assert.Equal("primary move failure", exception.Message);
        Assert.Equal("second", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task WriteAllTextAsync_CancellationDuringMoveRetry_ReleasesLockForNextWrite()
    {
        using var temp = new TemporaryDirectory();
        string target = Path.Combine(temp.Path, "state.json");
        using var cancellation = new CancellationTokenSource();
        var operations = new CancelFirstRetryOperations(cancellation);
        var writer = new AtomicFileWriter(operations);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            writer.WriteAllTextAsync(target, "canceled", cancellation.Token));
        await writer.WriteAllTextAsync(target, "completed", CancellationToken.None);

        Assert.Equal(2, operations.MoveAttempts);
        Assert.Equal("completed", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task WriteAllTextAsync_CanceledContendingWaiter_DoesNotConsumeLock()
    {
        using var temp = new TemporaryDirectory();
        string target = Path.Combine(temp.Path, "state.json");
        using var operations = new BlockingFirstMoveOperations();
        var writer = new AtomicFileWriter(operations);

        Task first = writer.WriteAllTextAsync(target, "first", CancellationToken.None);
        Assert.True(operations.FirstMoveStarted.Wait(TimeSpan.FromSeconds(2)));
        using var cancellation = new CancellationTokenSource();
        Task contending = writer.WriteAllTextAsync(target, "discarded", cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => contending);
        operations.ReleaseFirstMove.Set();
        await first;
        await writer.WriteAllTextAsync(target, "third", CancellationToken.None);

        Assert.Equal("third", await File.ReadAllTextAsync(target));
    }

    private sealed class PrimaryAndCleanupFailureOperations : IAtomicFileOperations
    {
        private int moveAttempts;
        private int deleteAttempts;

        public bool Exists(string path) => File.Exists(path);

        public void Delete(string path)
        {
            if (Interlocked.Increment(ref deleteAttempts) == 1)
            {
                throw new UnauthorizedAccessException("cleanup failure");
            }

            File.Delete(path);
        }

        public void Move(string sourcePath, string targetPath)
        {
            if (Interlocked.Increment(ref moveAttempts) == 1)
            {
                throw new IOException("primary move failure");
            }

            File.Move(sourcePath, targetPath, overwrite: true);
        }
    }

    private sealed class CancelFirstRetryOperations(CancellationTokenSource cancellation)
        : IAtomicFileOperations
    {
        private int moveAttempts;

        public int MoveAttempts => Volatile.Read(ref moveAttempts);
        public bool Exists(string path) => File.Exists(path);
        public void Delete(string path) => File.Delete(path);

        public void Move(string sourcePath, string targetPath)
        {
            if (Interlocked.Increment(ref moveAttempts) == 1)
            {
                cancellation.Cancel();
                throw new UnauthorizedAccessException("retry after cancellation");
            }

            File.Move(sourcePath, targetPath, overwrite: true);
        }
    }

    private sealed class BlockingFirstMoveOperations : IAtomicFileOperations, IDisposable
    {
        private int moveAttempts;

        public ManualResetEventSlim FirstMoveStarted { get; } = new();
        public ManualResetEventSlim ReleaseFirstMove { get; } = new();
        public bool Exists(string path) => File.Exists(path);
        public void Delete(string path) => File.Delete(path);

        public void Move(string sourcePath, string targetPath)
        {
            if (Interlocked.Increment(ref moveAttempts) == 1)
            {
                FirstMoveStarted.Set();
                Assert.True(ReleaseFirstMove.Wait(TimeSpan.FromSeconds(2)));
            }

            File.Move(sourcePath, targetPath, overwrite: true);
        }

        public void Dispose()
        {
            FirstMoveStarted.Dispose();
            ReleaseFirstMove.Dispose();
        }
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
