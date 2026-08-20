using CCAutoApprove.App.Services;
using CCAutoApprove.Core.Models;

namespace CCAutoApprove.App.ViewModels;

public sealed class AuditRecordItemViewModel
{
    private readonly AuditRecord record;

    public AuditRecordItemViewModel(AuditRecord record)
    {
        this.record = record ?? throw new ArgumentNullException(nameof(record));
    }

    public DateTimeOffset TimeUtc => record.TimeUtc;
    public string Tool => record.Tool;
    public string? SessionId => record.SessionId;
    public string? PermissionMode => record.PermissionMode;
    public string? ToolInput => record.ToolInput?.GetRawText();
    public string? PermissionSuggestions => record.PermissionSuggestions?.GetRawText();

    public string DecisionText => record.Decision switch
    {
        ApprovalDecisionKind.Allow => StringResources.Get("DecisionAllow"),
        ApprovalDecisionKind.Deny => StringResources.Get("DecisionDeny"),
        ApprovalDecisionKind.Ask => StringResources.Get("DecisionAsk"),
        _ => StringResources.Get("ValueUnknown")
    };

    public string SourceText => record.Source switch
    {
        DecisionSource.LocalAlwaysAllow => StringResources.Get("SourceLocalAlwaysAllow"),
        DecisionSource.LocalRule => StringResources.Get("SourceLocalRule"),
        DecisionSource.AI => StringResources.Get("SourceAI"),
        DecisionSource.RemotePhone => StringResources.Get("SourceRemotePhone"),
        DecisionSource.HumanDesktop => StringResources.Get("SourceHumanDesktop"),
        _ => StringResources.Get("ValueUnknown")
    };
}
