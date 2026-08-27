using CCAutoApprove.Core.Abstractions;
using CCAutoApprove.Core.Decisions;
using CCAutoApprove.Core.Models;

namespace CCAutoApprove.Core.Tests.Decisions;

public sealed class ApprovalCoordinatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 2, 30, 10, TimeSpan.Zero);
    private static readonly ApprovalRequest Request =
        ApprovalRequest.CreateForTest(@"D:\projects\my-app", "Bash");

    // Catches mutations that skip a guard or evaluate the guards in a fail-open manner.
    [Theory]
    [InlineData(false, true, true, ApprovalDecisionKind.Ask)]
    [InlineData(true, false, true, ApprovalDecisionKind.Ask)]
    [InlineData(true, true, false, ApprovalDecisionKind.Ask)]
    [InlineData(true, true, true, ApprovalDecisionKind.Allow)]
    public async Task DecideAsync_RequiresAllGuards(
        bool stateExists, bool runtimeValid, bool projectMatches, ApprovalDecisionKind expected)
    {
        var coordinator = CreateCoordinator(stateExists, runtimeValid, projectMatches,
            new AlwaysAllowDecisionProvider());

        ApprovalDecision decision = await coordinator.DecideAsync(Request, CancellationToken.None);

        Assert.Equal(expected, decision.Kind);
        Assert.Equal(ExpectedGuardReason(stateExists, runtimeValid, projectMatches), decision.Reason);
    }

    // Catches mutations that leak provider exception details or allow an exception to escape.
    [Fact]
    public async Task DecideAsync_WhenProviderThrows_ReturnsSanitizedAsk()
    {
        var coordinator = CreateCoordinator(true, true, true, new ThrowingDecisionProvider());

        ApprovalDecision decision = await coordinator.DecideAsync(Request, CancellationToken.None);

        Assert.Equal(ApprovalDecisionKind.Ask, decision.Kind);
        Assert.Equal("DecisionError", decision.Reason);
    }

    // Catches mutations that remove or lengthen the 750 millisecond provider timeout.
    [Fact]
    public async Task DecideAsync_WhenProviderExceedsTimeout_ReturnsAsk()
    {
        var coordinator = CreateCoordinator(true, true, true, new DelayedAllowDecisionProvider());

        ApprovalDecision decision = await coordinator.DecideAsync(Request, CancellationToken.None);

        Assert.Equal(ApprovalDecisionKind.Ask, decision.Kind);
        Assert.Equal("DecisionTimeout", decision.Reason);
    }

    // Catches mutations that replace a provider Deny with a fail-open or fallback result.
    [Fact]
    public async Task DecideAsync_WhenProviderDenies_PreservesDecision()
    {
        var expected = new ApprovalDecision(ApprovalDecisionKind.Deny, DecisionSource.LocalRule, "BlockedByRule");
        var coordinator = CreateCoordinator(true, true, true, new FixedDecisionProvider(expected));

        ApprovalDecision decision = await coordinator.DecideAsync(Request, CancellationToken.None);

        Assert.Equal(expected, decision);
    }

    // Catches mutations that replace a provider Ask with Allow or discard its reason and source.
    [Fact]
    public async Task DecideAsync_WhenProviderAsks_PreservesDecision()
    {
        var expected = new ApprovalDecision(ApprovalDecisionKind.Ask, DecisionSource.AI, "NeedsContext");
        var coordinator = CreateCoordinator(true, true, true, new FixedDecisionProvider(expected));

        ApprovalDecision decision = await coordinator.DecideAsync(Request, CancellationToken.None);

        Assert.Equal(expected, decision);
    }

    // Catches mutations that propagate caller cancellation instead of failing closed.
    [Fact]
    public async Task DecideAsync_WhenCanceled_ReturnsSanitizedAsk()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var coordinator = CreateCoordinator(true, true, true, new CancellableDecisionProvider());

        ApprovalDecision decision = await coordinator.DecideAsync(Request, cancellation.Token);

        Assert.Equal(ApprovalDecisionKind.Ask, decision.Kind);
        Assert.Equal("DecisionCanceled", decision.Reason);
    }

    // Catches mutations that only fail closed for provider faults while leaking guard dependency faults.
    [Fact]
    public async Task DecideAsync_WhenStateStoreThrows_ReturnsSanitizedAsk()
    {
        var coordinator = CreateCoordinator(
            new ThrowingRuntimeStateStore(),
            runtimeValid: true,
            projectMatches: true,
            new AlwaysAllowDecisionProvider());

        ApprovalDecision decision = await coordinator.DecideAsync(Request, CancellationToken.None);

        Assert.Equal(ApprovalDecisionKind.Ask, decision.Kind);
        Assert.Equal("DecisionError", decision.Reason);
    }

    // Catches mutations that allow project-matcher faults to escape the fail-safe boundary.
    [Fact]
    public async Task DecideAsync_WhenProjectMatcherThrows_ReturnsSanitizedAsk()
    {
        var coordinator = new ApprovalCoordinator(
            new FakeRuntimeStateStore(RuntimeState.CreateForTest(true, Now)),
            CreateValidator(runtimeValid: true),
            new ThrowingProjectMatcher(),
            new AlwaysAllowDecisionProvider());

        ApprovalDecision decision = await coordinator.DecideAsync(Request, CancellationToken.None);

        Assert.Equal(ApprovalDecisionKind.Ask, decision.Kind);
        Assert.Equal("DecisionError", decision.Reason);
    }

    private static ApprovalCoordinator CreateCoordinator(
        bool stateExists,
        bool runtimeValid,
        bool projectMatches,
        IDecisionProvider decisionProvider) =>
        CreateCoordinator(
            new FakeRuntimeStateStore(stateExists ? RuntimeState.CreateForTest(true, Now) : null),
            runtimeValid,
            projectMatches,
            decisionProvider);

    private static ApprovalCoordinator CreateCoordinator(
        IRuntimeStateStore stateStore,
        bool runtimeValid,
        bool projectMatches,
        IDecisionProvider decisionProvider) =>
        new(
            stateStore,
            CreateValidator(runtimeValid),
            new FakeProjectMatcher(projectMatches),
            decisionProvider);

    private static RuntimeStateValidator CreateValidator(bool runtimeValid) =>
        new(new FakeClock(Now), new FakeProcessIdentityValidator(runtimeValid));

    private static string? ExpectedGuardReason(
        bool stateExists,
        bool runtimeValid,
        bool projectMatches)
    {
        if (!stateExists) return "MissingRuntimeState";
        if (!runtimeValid) return "InvalidProcess";
        if (!projectMatches) return "ProjectMismatch";
        return null;
    }

    private sealed record FakeClock(DateTimeOffset UtcNow) : IClock;

    private sealed record FakeProcessIdentityValidator(bool Result) : IProcessIdentityValidator
    {
        public bool IsValid(RuntimeState state) => Result;
    }

    private sealed record FakeProjectMatcher(bool Result) : IProjectMatcher
    {
        public bool IsMatch(string selectedProject, string requestDirectory) => Result;
    }

    private sealed class ThrowingProjectMatcher : IProjectMatcher
    {
        public bool IsMatch(string selectedProject, string requestDirectory) =>
            throw new InvalidOperationException("sensitive matcher detail");
    }

    private sealed record FakeRuntimeStateStore(RuntimeState? State) : IRuntimeStateStore
    {
        public Task<RuntimeState?> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(State);

        public Task SaveAsync(RuntimeState state, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ThrowingRuntimeStateStore : IRuntimeStateStore
    {
        public Task<RuntimeState?> LoadAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("sensitive storage detail");

        public Task SaveAsync(RuntimeState state, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ThrowingDecisionProvider : IDecisionProvider
    {
        public Task<ApprovalDecision> DecideAsync(
            ApprovalRequest request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("sensitive provider detail");
    }

    private sealed class DelayedAllowDecisionProvider : IDecisionProvider
    {
        public async Task<ApprovalDecision> DecideAsync(
            ApprovalRequest request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(900), cancellationToken);
            return ApprovalDecision.Allow(DecisionSource.AI);
        }
    }

    private sealed record FixedDecisionProvider(ApprovalDecision Decision) : IDecisionProvider
    {
        public Task<ApprovalDecision> DecideAsync(
            ApprovalRequest request,
            CancellationToken cancellationToken) => Task.FromResult(Decision);
    }

    private sealed class CancellableDecisionProvider : IDecisionProvider
    {
        public async Task<ApprovalDecision> DecideAsync(
            ApprovalRequest request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return ApprovalDecision.Allow(DecisionSource.AI);
        }
    }
}
