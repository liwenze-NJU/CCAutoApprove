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
        byte[]? allowPayload = null;

        try
        {
            ApprovalRequest request = await parser.ParseAsync(input, clock, timeout.Token);
            ApprovalDecision decision = await coordinator.DecideAsync(request, timeout.Token);
            await WriteAuditWithinDeadlineAsync(request, decision, timeout.Token);
            if (decision.Kind == ApprovalDecisionKind.Allow)
            {
                using var response = new MemoryStream();
                await responseWriter.WriteAllowAsync(response, timeout.Token);
                allowPayload = response.ToArray();
            }
        }
        catch
        {
            if (!timeout.IsCancellationRequested)
            {
                await TryWriteSanitizedFailureAsync(timeout.Token);
            }

            return 0;
        }

        if (allowPayload is not null)
        {
            try
            {
                timeout.Token.ThrowIfCancellationRequested();
                // This is the sole stdout commit point, after all application work succeeds.
                // A pipe write cannot be rolled back if the OS accepts bytes and then fails;
                // any truncated prefix is not a complete, valid Allow JSON object.
                output.Write(allowPayload, 0, allowPayload.Length);
            }
            catch
            {
                // Claude receives either the complete object or a transport-level invalid prefix.
            }
        }

        return 0;
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
            await WriteAuditWithinDeadlineAsync(
                request,
                ApprovalDecision.Ask("HookFailure"),
                cancellationToken);
        }
        catch
        {
            // Hook failures must always degrade to Claude's normal interactive prompt.
        }
    }

    private async Task WriteAuditWithinDeadlineAsync(
        ApprovalRequest request,
        ApprovalDecision decision,
        CancellationToken cancellationToken)
    {
        Task write = Task.Factory.StartNew(
            () => auditLog.WriteAsync(request, decision, cancellationToken),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default)
            .Unwrap();
        ObserveFault(write);
        await write.WaitAsync(cancellationToken);
    }

    private static void ObserveFault(Task task)
    {
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
