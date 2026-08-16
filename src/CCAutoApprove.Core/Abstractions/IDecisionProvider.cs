using CCAutoApprove.Core.Models;

namespace CCAutoApprove.Core.Abstractions;

public interface IDecisionProvider
{
    Task<ApprovalDecision> DecideAsync(ApprovalRequest request, CancellationToken cancellationToken);
}
