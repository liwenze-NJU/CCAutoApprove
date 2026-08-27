using CCAutoApprove.Infrastructure.Claude;

namespace CCAutoApprove.App.Services;

public enum HookOperationOutcome
{
    Installed,
    Uninstalled,
    Healthy,
    Unhealthy,
    Failed
}

public sealed class HookHealthSnapshot(
    bool? isOperational,
    IReadOnlyList<DoctorCheck> checks) : EventArgs
{
    public bool? IsOperational { get; } = isOperational;
    public IReadOnlyList<DoctorCheck> Checks { get; } = checks;
    public static HookHealthSnapshot Unknown { get; } = new(null, []);
}

public sealed record HookOperationResult(
    HookOperationOutcome Outcome,
    bool IsOperational,
    IReadOnlyList<DoctorCheck> Checks);

public interface IHookMaintenanceService
{
    event EventHandler<HookHealthSnapshot>? HealthChanged;
    HookHealthSnapshot Current { get; }
    Task<HookOperationResult> InstallAsync(CancellationToken cancellationToken);
    Task<HookOperationResult> DoctorAsync(CancellationToken cancellationToken);
    Task<HookOperationResult> UninstallAsync(CancellationToken cancellationToken);
    Task<HookHealthSnapshot> RefreshAsync(CancellationToken cancellationToken);
}

public sealed class HookMaintenanceService : IHookMaintenanceService
{
    private readonly Func<CancellationToken, Task> installAsync;
    private readonly Func<CancellationToken, Task> uninstallAsync;
    private readonly Func<CancellationToken, Task<IReadOnlyList<DoctorCheck>>> doctorAsync;

    public HookMaintenanceService(ClaudeHookManager hookManager, ClaudeDoctor doctor)
        : this(hookManager.InstallAsync, hookManager.UninstallAsync, doctor.RunAsync)
    {
        ArgumentNullException.ThrowIfNull(hookManager);
        ArgumentNullException.ThrowIfNull(doctor);
    }

    internal HookMaintenanceService(
        Func<CancellationToken, Task> installAsync,
        Func<CancellationToken, Task> uninstallAsync,
        Func<CancellationToken, Task<IReadOnlyList<DoctorCheck>>> doctorAsync)
    {
        this.installAsync = installAsync ?? throw new ArgumentNullException(nameof(installAsync));
        this.uninstallAsync = uninstallAsync ?? throw new ArgumentNullException(nameof(uninstallAsync));
        this.doctorAsync = doctorAsync ?? throw new ArgumentNullException(nameof(doctorAsync));
    }

    public event EventHandler<HookHealthSnapshot>? HealthChanged;

    public HookHealthSnapshot Current { get; private set; } = HookHealthSnapshot.Unknown;

    public Task<HookOperationResult> InstallAsync(CancellationToken cancellationToken) =>
        MutateAndRefreshAsync(installAsync, HookOperationOutcome.Installed, cancellationToken);

    public async Task<HookOperationResult> DoctorAsync(CancellationToken cancellationToken)
    {
        HookHealthSnapshot snapshot = await RefreshAsync(cancellationToken);
        return new HookOperationResult(
            snapshot.IsOperational == true
                ? HookOperationOutcome.Healthy
                : HookOperationOutcome.Unhealthy,
            snapshot.IsOperational == true,
            snapshot.Checks);
    }

    public Task<HookOperationResult> UninstallAsync(CancellationToken cancellationToken) =>
        MutateAndRefreshAsync(uninstallAsync, HookOperationOutcome.Uninstalled, cancellationToken);

    public async Task<HookHealthSnapshot> RefreshAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            IReadOnlyList<DoctorCheck> checks = await doctorAsync(cancellationToken);
            return Publish(checks);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Publish([]);
        }
    }

    private async Task<HookOperationResult> MutateAndRefreshAsync(
        Func<CancellationToken, Task> mutation,
        HookOperationOutcome successOutcome,
        CancellationToken cancellationToken)
    {
        try
        {
            await mutation(cancellationToken);
            HookHealthSnapshot snapshot = await RefreshAsync(cancellationToken);
            bool operational = snapshot.IsOperational == true;
            HookOperationOutcome outcome = successOutcome == HookOperationOutcome.Installed && !operational
                ? HookOperationOutcome.Unhealthy
                : successOutcome;
            return new HookOperationResult(outcome, operational, snapshot.Checks);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            HookHealthSnapshot snapshot = Publish([]);
            return new HookOperationResult(HookOperationOutcome.Failed, false, snapshot.Checks);
        }
    }

    private HookHealthSnapshot Publish(IReadOnlyList<DoctorCheck> checks)
    {
        bool operational = checks.Count > 0
            && !checks.Any(check => check.Severity == DoctorSeverity.Error);
        Current = new HookHealthSnapshot(operational, checks);
        HealthChanged?.Invoke(this, Current);
        return Current;
    }
}
