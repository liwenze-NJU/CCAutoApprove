using System.Text.Json;

namespace CCAutoApprove.Infrastructure.Claude;

public sealed class ClaudePermissionResponseWriter
{
    public async Task WriteAllowAsync(Stream output, CancellationToken cancellationToken)
    {
        using var writer = new Utf8JsonWriter(output);
        writer.WriteStartObject();
        writer.WritePropertyName("hookSpecificOutput");
        writer.WriteStartObject();
        writer.WriteString("hookEventName", "PermissionRequest");
        writer.WritePropertyName("decision");
        writer.WriteStartObject();
        writer.WriteString("behavior", "allow");
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
        await writer.FlushAsync(cancellationToken);
    }
}
