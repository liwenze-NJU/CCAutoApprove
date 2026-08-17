using CCAutoApprove.Core.Abstractions;

namespace CCAutoApprove.Infrastructure.Windows;

public sealed class WindowsDirectoryService : IDirectoryService
{
    public bool Exists(string path) => Directory.Exists(path);
}
