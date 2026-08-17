using System.Diagnostics;
using CCAutoApprove.Core.Models;
using CCAutoApprove.Infrastructure.Windows;

namespace CCAutoApprove.Infrastructure.Tests.Windows;

public sealed class WindowsProcessIdentityValidatorTests
{
    [Fact]
    public void IsValid_CurrentProcessWithMatchingStartTime_ReturnsTrue()
    {
        using Process process = Process.GetCurrentProcess();
        var state = RuntimeState.CreateForTest(true, DateTimeOffset.UtcNow) with
        {
            ProcessId = process.Id,
            ProcessStartUtc = process.StartTime.ToUniversalTime()
        };

        Assert.True(new WindowsProcessIdentityValidator().IsValid(state));
    }

    [Fact]
    public void IsValid_MissingProcess_ReturnsFalse()
    {
        var state = RuntimeState.CreateForTest(true, DateTimeOffset.UtcNow) with { ProcessId = int.MaxValue };

        Assert.False(new WindowsProcessIdentityValidator().IsValid(state));
    }
}
