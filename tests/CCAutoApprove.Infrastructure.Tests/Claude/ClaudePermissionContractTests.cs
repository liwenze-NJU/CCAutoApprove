using System.Text;
using System.Text.Json;
using CCAutoApprove.Core.Abstractions;
using CCAutoApprove.Core.Models;
using CCAutoApprove.Infrastructure.Claude;

namespace CCAutoApprove.Infrastructure.Tests.Claude;

public sealed class ClaudePermissionContractTests
{
    private static readonly DateTimeOffset ReceivedAtUtc =
        new(2026, 8, 17, 1, 2, 3, TimeSpan.Zero);

    [Theory]
    [InlineData("permission-two-option.json", false)]
    [InlineData("permission-three-option.json", true)]
    public async Task ParseAsync_OfficialPromptShapes_MapToApprovalRequest(
        string fixtureName,
        bool hasPermissionSuggestions)
    {
        using var input = new MemoryStream(await ReadFixtureBytesAsync(fixtureName));
        var parser = new ClaudePermissionRequestParser();

        ApprovalRequest request = await parser.ParseAsync(
            input,
            new FixedClock(ReceivedAtUtc),
            CancellationToken.None);

        Assert.NotEqual(Guid.Empty, request.RequestId);
        Assert.Equal("abc123", request.SessionId);
        Assert.Equal("D:\\projects\\my-app", request.ProjectPath);
        Assert.Equal("Bash", request.ToolName);
        Assert.Equal("default", request.PermissionMode);
        Assert.Equal(ReceivedAtUtc, request.ReceivedAtUtc);
        Assert.Equal(JsonValueKind.Object, request.ToolInput.ValueKind);
        Assert.Equal("dotnet test", request.ToolInput.GetProperty("command").GetString());
        Assert.Equal("Run tests", request.ToolInput.GetProperty("description").GetString());
        Assert.Equal(hasPermissionSuggestions, request.PermissionSuggestions.HasValue);
        if (hasPermissionSuggestions)
        {
            Assert.Equal(JsonValueKind.Array, request.PermissionSuggestions!.Value.ValueKind);
            Assert.Equal("addRules", request.PermissionSuggestions.Value[0].GetProperty("type").GetString());
        }
    }

    [Fact]
    public async Task ParseAsync_PreservesJsonElementsBeyondJsonDocumentLifetime()
    {
        using var input = new MemoryStream(await ReadFixtureBytesAsync("permission-three-option.json"));
        ApprovalRequest request = await new ClaudePermissionRequestParser().ParseAsync(
            input,
            new FixedClock(ReceivedAtUtc),
            CancellationToken.None);

        GC.Collect();
        GC.WaitForPendingFinalizers();

        Assert.Equal("dotnet test", request.ToolInput.GetProperty("command").GetString());
        Assert.Equal("allow", request.PermissionSuggestions!.Value[0].GetProperty("behavior").GetString());
    }

    [Fact]
    public async Task ParseAsync_UnknownFields_AreIgnored()
    {
        const string json = """
            {
              "session_id":"abc123",
              "cwd":"D:\\projects\\my-app",
              "permission_mode":"default",
              "hook_event_name":"PermissionRequest",
              "tool_name":"Bash",
              "tool_input":{"command":"dotnet test"},
              "future_field":{"nested":true}
            }
            """;

        ApprovalRequest request = await ParseAsync(json);

        Assert.Equal("abc123", request.SessionId);
        Assert.Equal("dotnet test", request.ToolInput.GetProperty("command").GetString());
    }

    [Theory]
    [InlineData("{\"session_id\":\"abc123\",\"hook_event_name\":\"PermissionRequest\",\"tool_name\":\"Bash\",\"tool_input\":{}}")]
    [InlineData("{\"session_id\":\"abc123\",\"cwd\":\"D:\\\\projects\\\\my-app\",\"hook_event_name\":\"PreToolUse\",\"tool_name\":\"Bash\",\"tool_input\":{}}")]
    [InlineData("{\"session_id\":\"\",\"cwd\":\"D:\\\\projects\\\\my-app\",\"hook_event_name\":\"PermissionRequest\",\"tool_name\":\"Bash\",\"tool_input\":{}}")]
    [InlineData("{\"session_id\":\"abc123\",\"cwd\":\"relative\\\\path\",\"hook_event_name\":\"PermissionRequest\",\"tool_name\":\"Bash\",\"tool_input\":{}}")]
    [InlineData("{\"session_id\":\"abc123\",\"cwd\":\"D:\\\\projects\\\\my-app\",\"hook_event_name\":\"PermissionRequest\",\"tool_name\":\" \",\"tool_input\":{}}")]
    [InlineData("{\"session_id\":\"abc123\",\"cwd\":\"D:\\\\projects\\\\my-app\",\"hook_event_name\":\"PermissionRequest\",\"tool_name\":\"Bash\",\"tool_input\":[]}")]
    public async Task ParseAsync_InvalidRequiredFields_ThrowsInvalidDataException(string json)
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => ParseAsync(json));
    }

    [Fact]
    public async Task ParseAsync_MalformedJson_ThrowsInvalidDataException()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => ParseAsync("{\"session_id\":"));
    }

    [Fact]
    public async Task ParseAsync_InputLargerThanOneMebibyte_FailsBeforeJsonParsing()
    {
        byte[] bytes = new byte[1_048_577];
        Array.Fill(bytes, (byte)'x');
        using var input = new MemoryStream(bytes);

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => new ClaudePermissionRequestParser().ParseAsync(
                input,
                new FixedClock(ReceivedAtUtc),
                CancellationToken.None));

        Assert.Equal("HookInputTooLarge", exception.Message);
    }

    [Fact]
    public async Task WriteAllowAsync_WritesOnlyExactMinifiedAllowObjectBytes()
    {
        byte[] expected = Encoding.UTF8.GetBytes(
            "{\"hookSpecificOutput\":{\"hookEventName\":\"PermissionRequest\",\"decision\":{\"behavior\":\"allow\"}}}");
        using var output = new MemoryStream();

        await new ClaudePermissionResponseWriter().WriteAllowAsync(output, CancellationToken.None);

        Assert.Equal(expected, output.ToArray());
        using JsonDocument document = JsonDocument.Parse(output.ToArray());
        JsonElement root = document.RootElement;
        Assert.Equal("PermissionRequest", root.GetProperty("hookSpecificOutput").GetProperty("hookEventName").GetString());
        Assert.Equal("allow", root.GetProperty("hookSpecificOutput").GetProperty("decision").GetProperty("behavior").GetString());
        Assert.False(root.GetProperty("hookSpecificOutput").GetProperty("decision").TryGetProperty("permissionSuggestions", out _));
    }

    private static async Task<ApprovalRequest> ParseAsync(string json)
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return await new ClaudePermissionRequestParser().ParseAsync(
            input,
            new FixedClock(ReceivedAtUtc),
            CancellationToken.None);
    }

    private static Task<byte[]> ReadFixtureBytesAsync(string fixtureName) =>
        File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixtureName));

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
