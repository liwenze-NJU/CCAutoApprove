namespace CCAutoApprove.Core.Abstractions;

public interface IDirectoryService
{
    bool Exists(string path);
}
