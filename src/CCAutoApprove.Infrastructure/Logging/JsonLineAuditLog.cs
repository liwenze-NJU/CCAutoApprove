using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CCAutoApprove.Core.Abstractions;
using CCAutoApprove.Core.Models;
using CCAutoApprove.Infrastructure.Configuration;

namespace CCAutoApprove.Infrastructure.Logging;

public sealed class JsonLineAuditLog : IAuditLog
{
    private const string MutexName = @"Local\CCAutoApprove.AuditLog";
    private static readonly TimeSpan MutexTimeout = TimeSpan.FromMilliseconds(50);
    private static readonly JsonSerializerOptions LogJsonOptions = new(JsonDefaults.Options)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string logsDirectory;
    private readonly PersistentSettings settings;
    private readonly IClock clock;

    public JsonLineAuditLog(AppPaths paths, PersistentSettings settings, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(paths);
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        logsDirectory = Path.Combine(paths.BasePath, "logs");
    }

    public Task WriteAsync(
        ApprovalRequest request,
        ApprovalDecision decision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(decision);
        cancellationToken.ThrowIfCancellationRequested();

        return RunWithoutAffectingDecisionAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            DateTimeOffset timeUtc = clock.UtcNow.ToUniversalTime();
            AuditRecord record = CreateRecord(timeUtc, request, decision);
            string filePath = Path.Combine(logsDirectory, $"audit-{timeUtc:yyyy-MM-dd}.jsonl");
            byte[] line = Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(record, LogJsonOptions) + Environment.NewLine);

            TryWithMutex(cancellationToken, () =>
            {
                Directory.CreateDirectory(logsDirectory);
                using var stream = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.Read);
                stream.Write(line);
                stream.Flush();
            });
        }, cancellationToken);
    }

    public Task<IReadOnlyList<AuditRecord>> ReadRecentAsync(
        int maximumCount,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (maximumCount <= 0)
        {
            return Task.FromResult<IReadOnlyList<AuditRecord>>([]);
        }

        return Task.Run<IReadOnlyList<AuditRecord>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var records = new List<AuditRecord>();
            WithMutex(cancellationToken, () => ReadRecords(records, cancellationToken));
            return records
                .OrderByDescending(record => record.TimeUtc)
                .Take(maximumCount)
                .ToArray();
        }, CancellationToken.None);
    }

    public Task ClearAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => WithMutex(cancellationToken, () =>
        {
            if (!Directory.Exists(logsDirectory))
            {
                return;
            }

            foreach (string filePath in Directory.EnumerateFiles(logsDirectory, "audit-*.jsonl"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                File.Delete(filePath);
            }
        }), CancellationToken.None);
    }

    public Task DeleteExpiredAsync(int retentionDays, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DateOnly boundary = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime).AddDays(-Math.Max(0, retentionDays));
        return Task.Run(() => WithMutex(cancellationToken, () =>
        {
            if (!Directory.Exists(logsDirectory))
            {
                return;
            }

            foreach (string filePath in Directory.EnumerateFiles(logsDirectory, "audit-*.jsonl"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (TryGetAuditDate(filePath, out DateOnly auditDate) && auditDate < boundary)
                {
                    File.Delete(filePath);
                }
            }
        }), CancellationToken.None);
    }

    private AuditRecord CreateRecord(
        DateTimeOffset timeUtc,
        ApprovalRequest request,
        ApprovalDecision decision)
    {
        bool detailed = settings.AuditDetailLevel == AuditDetailLevel.Detailed;
        string normalizedProject = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.ProjectPath));
        return new AuditRecord(
            timeUtc,
            request.RequestId,
            normalizedProject,
            request.ToolName,
            decision.Kind,
            decision.Source,
            detailed ? request.SessionId : null,
            detailed ? request.PermissionMode : null,
            detailed ? request.ToolInput.Clone() : null,
            detailed && request.PermissionSuggestions is JsonElement suggestions
                ? suggestions.Clone()
                : null);
    }

    private void ReadRecords(List<AuditRecord> records, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(logsDirectory))
        {
            return;
        }

        foreach (string filePath in Directory.EnumerateFiles(logsDirectory, "audit-*.jsonl"))
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            while (reader.ReadLine() is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    AuditRecord? record = JsonSerializer.Deserialize<AuditRecord>(line, LogJsonOptions);
                    if (record is not null)
                    {
                        records.Add(record);
                    }
                }
                catch (JsonException)
                {
                    // A damaged record must not hide other readable local audit records.
                }
            }
        }
    }

    private static async Task RunWithoutAffectingDecisionAsync(Action action, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Run(action, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static bool TryWithMutex(CancellationToken cancellationToken, Action action)
    {
        using var mutex = new Mutex(initiallyOwned: false, MutexName);
        bool acquired = false;
        try
        {
            try
            {
                acquired = mutex.WaitOne(MutexTimeout);
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!acquired)
            {
                return false;
            }

            action();
            return true;
        }
        finally
        {
            if (acquired)
            {
                mutex.ReleaseMutex();
            }
        }
    }

    private static void WithMutex(CancellationToken cancellationToken, Action action)
    {
        using var mutex = new Mutex(initiallyOwned: false, MutexName);
        bool acquired = false;
        try
        {
            try
            {
                if (cancellationToken.CanBeCanceled)
                {
                    int signaled = WaitHandle.WaitAny([mutex, cancellationToken.WaitHandle]);
                    if (signaled != 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                }
                else
                {
                    mutex.WaitOne();
                }

                acquired = true;
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            cancellationToken.ThrowIfCancellationRequested();
            action();
        }
        finally
        {
            if (acquired)
            {
                mutex.ReleaseMutex();
            }
        }
    }

    private static bool TryGetAuditDate(string filePath, out DateOnly date)
    {
        string fileName = Path.GetFileName(filePath);
        const string prefix = "audit-";
        const string suffix = ".jsonl";
        if (!fileName.StartsWith(prefix, StringComparison.Ordinal)
            || !fileName.EndsWith(suffix, StringComparison.Ordinal)
            || fileName.Length != prefix.Length + 10 + suffix.Length)
        {
            date = default;
            return false;
        }

        return DateOnly.TryParseExact(
            fileName.AsSpan(prefix.Length, 10),
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out date);
    }
}
