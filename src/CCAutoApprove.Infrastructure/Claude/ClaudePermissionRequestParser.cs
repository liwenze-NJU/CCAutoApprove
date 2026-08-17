using System.Text.Json;
using System.Text.Json.Serialization;
using CCAutoApprove.Core.Abstractions;
using CCAutoApprove.Core.Models;
using CCAutoApprove.Infrastructure.Configuration;

namespace CCAutoApprove.Infrastructure.Claude;

public sealed class ClaudePermissionRequestParser
{
    private const int BufferSize = 81_920;
    private const int MaximumInputBytes = 1_048_576;

    public async Task<ApprovalRequest> ParseAsync(
        Stream input,
        IClock clock,
        CancellationToken cancellationToken)
    {
        using var contents = new MemoryStream();
        byte[] buffer = new byte[BufferSize];
        int totalBytes = 0;

        while (true)
        {
            int bytesRead = await input.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (bytesRead == 0)
            {
                break;
            }

            totalBytes += bytesRead;
            if (totalBytes > MaximumInputBytes)
            {
                throw new InvalidDataException("HookInputTooLarge");
            }

            await contents.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
        }

        PermissionRequestDto request;
        try
        {
            request = JsonSerializer.Deserialize<PermissionRequestDto>(
                    contents.GetBuffer().AsSpan(0, checked((int)contents.Length)),
                    JsonDefaults.Options)
                ?? throw new InvalidDataException("InvalidHookInput");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("InvalidHookInput", exception);
        }

        if (!string.Equals(request.HookEventName, "PermissionRequest", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(request.SessionId)
            || string.IsNullOrWhiteSpace(request.WorkingDirectory)
            || !Path.IsPathFullyQualified(request.WorkingDirectory)
            || string.IsNullOrWhiteSpace(request.ToolName)
            || request.ToolInput.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("InvalidHookInput");
        }

        return new ApprovalRequest(
            Guid.NewGuid(),
            request.SessionId,
            request.WorkingDirectory,
            request.ToolName,
            request.ToolInput.Clone(),
            request.PermissionMode ?? string.Empty,
            request.PermissionSuggestions?.Clone(),
            clock.UtcNow);
    }

    private sealed class PermissionRequestDto
    {
        [JsonPropertyName("session_id")]
        public string? SessionId { get; init; }

        [JsonPropertyName("cwd")]
        public string? WorkingDirectory { get; init; }

        [JsonPropertyName("permission_mode")]
        public string? PermissionMode { get; init; }

        [JsonPropertyName("hook_event_name")]
        public string? HookEventName { get; init; }

        [JsonPropertyName("tool_name")]
        public string? ToolName { get; init; }

        [JsonPropertyName("tool_input")]
        public JsonElement ToolInput { get; init; }

        [JsonPropertyName("permission_suggestions")]
        public JsonElement? PermissionSuggestions { get; init; }
    }
}
