using System.Text.Json;
using CCAutoApprove.Core.Abstractions;
using CCAutoApprove.Core.Decisions;
using CCAutoApprove.Core.Models;
using CCAutoApprove.Infrastructure.Claude;

namespace CCAutoApprove.Cli;

public sealed class HookCommand(
    ClaudePermissionRequestParser parser,
    ApprovalCoordinator coordinator,
    IAuditLog auditLog,
    ClaudePermissionResponseWriter responseWriter,
    IClock clock)
{
    private static readonly TimeSpan TotalTimeout = TimeSpan.FromMilliseconds(1_000);

    public async Task<int> ExecuteAsync(
        Stream input,
        Stream output,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TotalTimeout);

        try
        {
            ApprovalRequest request = await parser.ParseAsync(input, clock, timeout.Token);
            ApprovalDecision decision = await coordinator.DecideAsync(request, timeout.Token);
            await auditLog.WriteAsync(request, decision, timeout.Token);
            if (decision.Kind == ApprovalDecisionKind.Allow)
            {
                using var response = new MemoryStream();
                await responseWriter.WriteAllowAsync(response, timeout.Token);
                response.Position = 0;
                await response.CopyToAsync(output, timeout.Token);
            }

            return 0;
        }
        catch
        {
            if (!timeout.IsCancellationRequested)
            {
                await TryWriteSanitizedFailureAsync(timeout.Token);
            }

            return 0;
        }
    }

    private async Task TryWriteSanitizedFailureAsync(CancellationToken cancellationToken)
    {
        try
        {
            using JsonDocument emptyInput = JsonDocument.Parse("{}");
            var request = new ApprovalRequest(
                Guid.NewGuid(),
                string.Empty,
                Environment.CurrentDirectory,
                "HookFailure",
                emptyInput.RootElement.Clone(),
                string.Empty,
                null,
                clock.UtcNow);
            await auditLog.WriteAsync(
                request,
                ApprovalDecision.Ask("HookFailure"),
                cancellationToken);
        }
        catch
        {
            // Hook failures must always degrade to Claude's normal interactive prompt.
        }
    }
}
