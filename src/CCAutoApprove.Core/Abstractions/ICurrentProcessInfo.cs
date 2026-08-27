namespace CCAutoApprove.Core.Abstractions;

public interface ICurrentProcessInfo
{
    int ProcessId { get; }
    DateTimeOffset ProcessStartUtc { get; }
}
