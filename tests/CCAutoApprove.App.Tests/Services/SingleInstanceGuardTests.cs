using CCAutoApprove.App.Services;

namespace CCAutoApprove.App.Tests.Services;

public sealed class SingleInstanceGuardTests
{
    [Fact]
    public void TryAcquire_WhenNameIsAlreadyOwned_ReturnsFalse()
    {
        string name = $@"Local\CCAutoApprove.App.Tests.{Guid.NewGuid():N}";
        using var first = new SingleInstanceGuard(name);
        using var second = new SingleInstanceGuard(name);

        Assert.True(first.TryAcquire());
        Assert.False(second.TryAcquire());
    }
}
