using System.Text.Json;

namespace CCAutoApprove.Core.Models;

public sealed record AuditRecord(
    DateTimeOffset TimeUtc,
    Guid RequestId,
    string Project,
    string Tool,
    ApprovalDecisionKind Decision,
    DecisionSource Source,
    string? SessionId = null,
    string? PermissionMode = null,
    JsonElement? ToolInput = null,
    JsonElement? PermissionSuggestions = null);
