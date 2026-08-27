using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CCAutoApprove.Core.Abstractions;
using CCAutoApprove.Core.Models;
using CCAutoApprove.Infrastructure.Configuration;

namespace CCAutoApprove.Infrastructure.Logging;

public sealed class JsonLineAuditLog : IAuditLog
{
    private const string MutexNamePrefix = @"Local\CCAutoApprove.AuditLog.";
    private static readonly TimeSpan MutexTimeout = TimeSpan.FromMilliseconds(50);
    private static readonly JsonSerializerOptions LogJsonOptions = new(JsonDefaults.Options)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string logsDirectory;
    private readonly string mutexName;
    private readonly PersistentSettings settings;
    private readonly IClock clock;
    private readonly IAuditFileRewriter auditFileRewriter;

    public JsonLineAuditLog(AppPaths paths, PersistentSettings settings, IClock clock)
        : this(paths, settings, clock, new AtomicAuditFileRewriter())
    {
    }

    internal JsonLineAuditLog(
        AppPaths paths,
        PersistentSettings settings,
        IClock clock,
        IAuditFileRewriter auditFileRewriter)
    {
        ArgumentNullException.ThrowIfNull(paths);
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.auditFileRewriter = auditFileRewriter
            ?? throw new ArgumentNullException(nameof(auditFileRewriter));
        logsDirectory = Path.Combine(paths.BasePath, "logs");
        mutexName = CreateMutexName(logsDirectory);
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
            var newest = new PriorityQueue<AuditRecord, DateTimeOffset>();
            WithMutex(cancellationToken, () =>
                ReadNewestRecords(newest, maximumCount, cancellationToken));
            return newest.UnorderedItems
                .Select(item => item.Element)
                .OrderByDescending(record => record.TimeUtc)
                .ToArray();
        }, CancellationToken.None);
    }

    public Task<int> CountAllowedAsync(
        DateTimeOffset startUtcInclusive,
        DateTimeOffset endUtcExclusive,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DateTimeOffset startUtc = startUtcInclusive.ToUniversalTime();
        DateTimeOffset endUtc = endUtcExclusive.ToUniversalTime();
        if (endUtc <= startUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(endUtcExclusive),
                "The end of the audit interval must be later than its start.");
        }

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = 0;
            WithMutex(cancellationToken, () =>
                count = CountAllowedRecords(startUtc, endUtc, cancellationToken));
            return count;
        }, cancellationToken);
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

    public Task DeleteDetailedAsync(CancellationToken cancellationToken)
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
                RemoveDetailedRecords(filePath, cancellationToken);
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

    private void ReadNewestRecords(
        PriorityQueue<AuditRecord, DateTimeOffset> newest,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(logsDirectory))
        {
            return;
        }

        (string Path, DateOnly Date)[] partitions = Directory
            .EnumerateFiles(logsDirectory, "audit-*.jsonl")
            .Select(path => (Path: path, HasDate: TryGetAuditDate(path, out DateOnly date), Date: date))
            .Where(partition => partition.HasDate)
            .OrderByDescending(partition => partition.Date)
            .Select(partition => (partition.Path, partition.Date))
            .ToArray();

        foreach ((string filePath, DateOnly partitionDate) in partitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (newest.Count == maximumCount
                && newest.TryPeek(out _, out DateTimeOffset oldestRetained)
                && oldestRetained >= PartitionEndUtc(partitionDate))
            {
                break;
            }

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
                        AddToBoundedNewest(newest, record, maximumCount);
                    }
                }
                catch (JsonException)
                {
                    // A damaged record must not hide other readable local audit records.
                }
            }
        }
    }

    private static void AddToBoundedNewest(
        PriorityQueue<AuditRecord, DateTimeOffset> newest,
        AuditRecord record,
        int maximumCount)
    {
        if (newest.Count < maximumCount)
        {
            newest.Enqueue(record, record.TimeUtc);
            return;
        }

        if (newest.TryPeek(out _, out DateTimeOffset oldestRetained)
            && record.TimeUtc > oldestRetained)
        {
            newest.Dequeue();
            newest.Enqueue(record, record.TimeUtc);
        }
    }

    private static DateTimeOffset PartitionEndUtc(DateOnly partitionDate) =>
        new(partitionDate.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));

    private int CountAllowedRecords(
        DateTimeOffset startUtcInclusive,
        DateTimeOffset endUtcExclusive,
        CancellationToken cancellationToken)
    {
        int count = 0;
        DateOnly firstDate = DateOnly.FromDateTime(startUtcInclusive.UtcDateTime);
        DateOnly lastDate = DateOnly.FromDateTime(endUtcExclusive.AddTicks(-1).UtcDateTime);

        for (DateOnly date = firstDate; date <= lastDate; date = date.AddDays(1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string filePath = Path.Combine(logsDirectory, $"audit-{date:yyyy-MM-dd}.jsonl");
            if (!File.Exists(filePath))
            {
                continue;
            }

            using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            while (reader.ReadLine() is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    AuditRecord? record = JsonSerializer.Deserialize<AuditRecord>(line, LogJsonOptions);
                    if (record is not null
                        && record.Decision == ApprovalDecisionKind.Allow
                        && record.TimeUtc >= startUtcInclusive
                        && record.TimeUtc < endUtcExclusive)
                    {
                        count++;
                    }
                }
                catch (JsonException)
                {
                    // A damaged record must not hide other readable records from the count.
                }
            }
        }

        return count;
    }

    private void RemoveDetailedRecords(
        string filePath,
        CancellationToken cancellationToken)
    {
        string[] lines = File.ReadAllLines(filePath, Encoding.UTF8);
        string[] retained = lines.Where(line =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return !ContainsDetailedFields(line);
        }).ToArray();
        if (retained.Length == lines.Length)
        {
            return;
        }

        auditFileRewriter.Rewrite(filePath, retained, cancellationToken);
    }

    private static bool ContainsDetailedFields(string line)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            return document.RootElement.TryGetProperty("sessionId", out _)
                || document.RootElement.TryGetProperty("permissionMode", out _)
                || document.RootElement.TryGetProperty("toolInput", out _)
                || document.RootElement.TryGetProperty("permissionSuggestions", out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async Task RunWithoutAffectingDecisionAsync(Action action, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Run(action, CancellationToken.None);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    internal static string CreateMutexName(string logsDirectory)
    {
        string normalizedPath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(logsDirectory)).ToUpperInvariant();
        byte[] pathHash = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath));
        return MutexNamePrefix + Convert.ToHexString(pathHash);
    }

    private bool TryWithMutex(CancellationToken cancellationToken, Action action)
    {
        using var mutex = new Mutex(initiallyOwned: false, mutexName);
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

    private void WithMutex(CancellationToken cancellationToken, Action action)
    {
        using var mutex = new Mutex(initiallyOwned: false, mutexName);
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
