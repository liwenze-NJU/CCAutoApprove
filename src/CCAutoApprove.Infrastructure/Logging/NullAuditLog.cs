using CCAutoApprove.Core.Abstractions;
using CCAutoApprove.Core.Models;

namespace CCAutoApprove.Infrastructure.Logging;

public sealed class NullAuditLog : IAuditLog
{
    public Task WriteAsync(
        ApprovalRequest request,
        ApprovalDecision decision,
        CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<IReadOnlyList<AuditRecord>> ReadRecentAsync(
        int maximumCount,
        CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<AuditRecord>>([]);

    public Task ClearAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task DeleteExpiredAsync(int retentionDays, CancellationToken cancellationToken) => Task.CompletedTask;
}
