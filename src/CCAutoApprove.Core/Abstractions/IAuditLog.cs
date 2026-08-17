using CCAutoApprove.Core.Models;

namespace CCAutoApprove.Core.Abstractions;

public interface IAuditLog
{
    Task WriteAsync(ApprovalRequest request, ApprovalDecision decision, CancellationToken cancellationToken);
    Task<IReadOnlyList<AuditRecord>> ReadRecentAsync(int maximumCount, CancellationToken cancellationToken);
    Task ClearAsync(CancellationToken cancellationToken);
    Task DeleteExpiredAsync(int retentionDays, CancellationToken cancellationToken);
}
