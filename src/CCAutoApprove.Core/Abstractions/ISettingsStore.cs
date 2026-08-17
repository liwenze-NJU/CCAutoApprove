using CCAutoApprove.Core.Models;

namespace CCAutoApprove.Core.Abstractions;

public interface ISettingsStore
{
    Task<PersistentSettings> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(PersistentSettings settings, CancellationToken cancellationToken);
}
