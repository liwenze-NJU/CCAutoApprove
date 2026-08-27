using System.Text.Json;

namespace CCAutoApprove.Core.Models;

public sealed record ApprovalRequest(
    Guid RequestId,
    string SessionId,
    string ProjectPath,
    string ToolName,
    JsonElement ToolInput,
    string PermissionMode,
    JsonElement? PermissionSuggestions,
    DateTimeOffset ReceivedAtUtc)
{
    public static ApprovalRequest CreateForTest(string projectPath, string toolName)
    {
        using JsonDocument document = JsonDocument.Parse("{}");
        return new(Guid.NewGuid(), "test-session", projectPath, toolName,
            document.RootElement.Clone(), "default", null, DateTimeOffset.UtcNow);
    }
}
