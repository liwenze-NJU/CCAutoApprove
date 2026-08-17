using CCAutoApprove.Core.Abstractions;
using CCAutoApprove.Core.Models;

namespace CCAutoApprove.Core.Decisions;

public sealed class RuntimeStateValidator(IClock clock, IProcessIdentityValidator processValidator)
{
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan HeartbeatLifetime = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan FutureTolerance = TimeSpan.FromSeconds(2);

    public RuntimeValidationResult Validate(RuntimeState state)
    {
        if (!state.Enabled) return RuntimeValidationResult.Invalid("Disabled");
        if (string.IsNullOrWhiteSpace(state.SelectedProject)) return RuntimeValidationResult.Invalid("MissingProject");
        if (!processValidator.IsValid(state)) return RuntimeValidationResult.Invalid("InvalidProcess");

        TimeSpan age = clock.UtcNow - state.HeartbeatUtc;
        if (age < -FutureTolerance) return RuntimeValidationResult.Invalid("FutureHeartbeat");
        if (age >= HeartbeatLifetime) return RuntimeValidationResult.Invalid("StaleHeartbeat");
        return RuntimeValidationResult.Valid();
    }
}
