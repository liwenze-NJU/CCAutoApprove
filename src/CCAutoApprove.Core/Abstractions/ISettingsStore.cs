using CCAutoApprove.Core.Models;

namespace CCAutoApprove.Core.Abstractions;

public interface ISettingsStore
{
    Task<PersistentSettings> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(PersistentSettings settings, CancellationToken cancellationToken);
    Task<PersistentSettings> UpdateAsync(
        Func<PersistentSettings, PersistentSettings> update,
        CancellationToken cancellationToken);
}
