using CCAutoApprove.Core.Models;

namespace CCAutoApprove.Core.Abstractions;

public interface IRuntimeStateStore
{
    Task<RuntimeState?> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(RuntimeState state, CancellationToken cancellationToken);
}
