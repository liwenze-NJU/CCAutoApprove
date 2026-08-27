using CCAutoApprove.Core.Abstractions;
using CCAutoApprove.Core.Models;

namespace CCAutoApprove.Core.Decisions;

public sealed class AlwaysAllowDecisionProvider : IDecisionProvider
{
    public Task<ApprovalDecision> DecideAsync(ApprovalRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(ApprovalDecision.Allow(DecisionSource.LocalAlwaysAllow));
}
