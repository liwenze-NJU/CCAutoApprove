using CCAutoApprove.Core.Abstractions;
using CCAutoApprove.Core.Decisions;
using CCAutoApprove.Core.Models;

namespace CCAutoApprove.Core.Tests.Decisions;

public sealed class RuntimeStateValidatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 2, 30, 10, TimeSpan.Zero);

    // Catches mutations that ignore Enabled or use an incorrect heartbeat-expiry comparison.
    [Theory]
    [InlineData(true, -2.0, true)]
    [InlineData(true, 0.0, true)]
    [InlineData(true, 9.999, true)]
    [InlineData(true, 10.0, false)]
    [InlineData(false, 0.0, false)]
    public void Validate_EnforcesEnabledAndHeartbeatAge(bool enabled, double ageSeconds, bool expected)
    {
        var state = RuntimeState.CreateForTest(enabled, Now.AddSeconds(-ageSeconds));
        var validator = new RuntimeStateValidator(new FakeClock(Now), new FakeProcessValidator(true));

        Assert.Equal(expected, validator.Validate(state).IsValid);
    }

    // Catches mutations that accept heartbeats more than two seconds ahead of the clock.
    [Fact]
    public void Validate_RejectsHeartbeatMoreThanTwoSecondsInFuture()
    {
        var state = RuntimeState.CreateForTest(true, Now.AddSeconds(2.001));
        var validator = new RuntimeStateValidator(new FakeClock(Now), new FakeProcessValidator(true));

        Assert.False(validator.Validate(state).IsValid);
    }

    // Catches mutations that omit process identity validation after a PID is reused or exits.
    [Fact]
    public void Validate_RejectsDeadOrReusedProcess()
    {
        var state = RuntimeState.CreateForTest(true, Now);
        var validator = new RuntimeStateValidator(new FakeClock(Now), new FakeProcessValidator(false));

        Assert.False(validator.Validate(state).IsValid);
    }

    private sealed record FakeClock(DateTimeOffset UtcNow) : IClock;

    private sealed record FakeProcessValidator(bool Result) : IProcessIdentityValidator
    {
        public bool IsValid(RuntimeState state) => Result;
    }
}
