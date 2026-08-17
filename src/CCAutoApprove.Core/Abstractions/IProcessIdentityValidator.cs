using CCAutoApprove.Core.Models;

namespace CCAutoApprove.Core.Abstractions;

public interface IProcessIdentityValidator
{
    bool IsValid(RuntimeState state);
}
