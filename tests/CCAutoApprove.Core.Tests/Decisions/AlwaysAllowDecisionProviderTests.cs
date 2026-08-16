using CCAutoApprove.Core.Decisions;
using CCAutoApprove.Core.Models;

namespace CCAutoApprove.Core.Tests.Decisions;

public sealed class AlwaysAllowDecisionProviderTests
{
    [Fact]
    public async Task DecideAsync_ReturnsLocalAlwaysAllow()
    {
        var provider = new AlwaysAllowDecisionProvider();
        var request = ApprovalRequest.CreateForTest(@"D:\projects\my-app", "Bash");

        ApprovalDecision decision = await provider.DecideAsync(request, CancellationToken.None);

        Assert.Equal(ApprovalDecisionKind.Allow, decision.Kind);
        Assert.Equal(DecisionSource.LocalAlwaysAllow, decision.Source);
    }
}
