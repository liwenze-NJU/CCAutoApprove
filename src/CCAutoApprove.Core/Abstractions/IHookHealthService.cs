namespace CCAutoApprove.Core.Abstractions;

public interface IHookHealthService
{
    Task<bool> IsOperationalAsync(CancellationToken cancellationToken);
}
