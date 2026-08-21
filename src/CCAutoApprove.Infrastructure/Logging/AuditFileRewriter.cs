using System.Text;

namespace CCAutoApprove.Infrastructure.Logging;

internal interface IAuditFileRewriter
{
    void Rewrite(
        string filePath,
        IReadOnlyList<string> retainedLines,
        CancellationToken cancellationToken);
}

internal sealed class AtomicAuditFileRewriter : IAuditFileRewriter
{
    public void Rewrite(
        string filePath,
        IReadOnlyList<string> retainedLines,
        CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(filePath)!;
        string temporaryPath = Path.Combine(
            directory,
            $"{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.rewrite.tmp");
        try
        {
            File.WriteAllLines(
                temporaryPath,
                retainedLines,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
