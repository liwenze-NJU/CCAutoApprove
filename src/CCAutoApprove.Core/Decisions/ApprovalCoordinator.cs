using CCAutoApprove.Core.Abstractions;
using CCAutoApprove.Core.Models;

namespace CCAutoApprove.Core.Decisions;

public sealed class ApprovalCoordinator(
    IRuntimeStateStore stateStore,
    RuntimeStateValidator stateValidator,
    IProjectMatcher projectMatcher,
    IDecisionProvider decisionProvider)
{
    private static readonly TimeSpan ProviderTimeout = TimeSpan.FromMilliseconds(750);

    public async Task<ApprovalDecision> DecideAsync(
        ApprovalRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            RuntimeState? state = await stateStore.LoadAsync(cancellationToken);
            if (state is null) return ApprovalDecision.Ask("MissingRuntimeState");

            RuntimeValidationResult validation = stateValidator.Validate(state);
            if (!validation.IsValid) return ApprovalDecision.Ask(validation.ErrorCode!);

            if (!projectMatcher.IsMatch(state.SelectedProject, request.ProjectPath))
                return ApprovalDecision.Ask("ProjectMismatch");

            return await decisionProvider
                .DecideAsync(request, cancellationToken)
                .WaitAsync(ProviderTimeout, cancellationToken);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return ApprovalDecision.Ask(
                exception is TimeoutException ? "DecisionTimeout" : "DecisionError");
        }
        catch (OperationCanceledException)
        {
            return ApprovalDecision.Ask("DecisionCanceled");
        }
    }
}
