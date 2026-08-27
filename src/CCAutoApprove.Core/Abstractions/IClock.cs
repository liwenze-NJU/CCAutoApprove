namespace CCAutoApprove.Core.Abstractions;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
