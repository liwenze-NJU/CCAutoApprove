using CCAutoApprove.App.Services;
using CCAutoApprove.Infrastructure.Claude;

namespace CCAutoApprove.App.Tests.Services;

public sealed class HookMaintenanceServiceTests
{
    [Fact]
    public async Task InstallAsync_WhenDoctorHasError_ReturnsUnhealthyInsteadOfCompleted()
    {
        bool installed = false;
        var service = new HookMaintenanceService(
            _ =>
            {
                installed = true;
                return Task.CompletedTask;
            },
            _ => Task.CompletedTask,
            _ => Task.FromResult<IReadOnlyList<DoctorCheck>>(
            [
                new("HookInstalled", DoctorSeverity.Pass, "installed"),
                new("ClaudeStructuredDecisionSupported", DoctorSeverity.Error, "unsupported")
            ]));
        HookHealthSnapshot? observed = null;
        service.HealthChanged += (_, snapshot) => observed = snapshot;

        HookOperationResult result = await service.InstallAsync(CancellationToken.None);

        Assert.True(installed);
        Assert.Equal(HookOperationOutcome.Unhealthy, result.Outcome);
        Assert.False(result.IsOperational);
        Assert.False(observed!.IsOperational);
        Assert.Equal(2, observed.Checks.Count);
    }

    [Fact]
    public async Task DoctorAndUninstall_RefreshTheSameStructuredHealthState()
    {
        bool installed = true;
        var service = new HookMaintenanceService(
            _ => Task.CompletedTask,
            _ =>
            {
                installed = false;
                return Task.CompletedTask;
            },
            _ => Task.FromResult<IReadOnlyList<DoctorCheck>>(installed
                ? [new("HookInstalled", DoctorSeverity.Pass, "installed")]
                : [new("HookInstalled", DoctorSeverity.Error, "missing")]));

        HookOperationResult doctor = await service.DoctorAsync(CancellationToken.None);
        HookOperationResult uninstall = await service.UninstallAsync(CancellationToken.None);

        Assert.Equal(HookOperationOutcome.Healthy, doctor.Outcome);
        Assert.True(doctor.IsOperational);
        Assert.Equal(HookOperationOutcome.Uninstalled, uninstall.Outcome);
        Assert.False(uninstall.IsOperational);
        Assert.False(service.Current.IsOperational);
    }

    [Fact]
    public async Task InstallAsync_InternalFailure_ReturnsSanitizedFailureShape()
    {
        var service = new HookMaintenanceService(
            _ => throw new IOException(@"private path D:\secret\settings.json"),
            _ => Task.CompletedTask,
            _ => Task.FromResult<IReadOnlyList<DoctorCheck>>([]));

        HookOperationResult result = await service.InstallAsync(CancellationToken.None);

        Assert.Equal(HookOperationOutcome.Failed, result.Outcome);
        Assert.False(result.IsOperational);
        Assert.Empty(result.Checks);
        Assert.DoesNotContain("secret", result.ToString(), StringComparison.OrdinalIgnoreCase);
    }
}
