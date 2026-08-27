namespace CCAutoApprove.Core.Models;

public enum ApprovalDecisionKind { Allow, Deny, Ask }
public enum DecisionSource { LocalAlwaysAllow, LocalRule, AI, RemotePhone, HumanDesktop }

public sealed record ApprovalDecision(
    ApprovalDecisionKind Kind,
    DecisionSource Source,
    string? Reason = null)
{
    public static ApprovalDecision Allow(DecisionSource source) => new(ApprovalDecisionKind.Allow, source);
    public static ApprovalDecision Ask(string reason) => new(ApprovalDecisionKind.Ask, DecisionSource.HumanDesktop, reason);
}
