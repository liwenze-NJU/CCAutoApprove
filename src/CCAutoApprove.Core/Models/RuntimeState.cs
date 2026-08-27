namespace CCAutoApprove.Core.Models;

public sealed record RuntimeState(
    int SchemaVersion,
    bool Enabled,
    int ProcessId,
    DateTimeOffset ProcessStartUtc,
    Guid InstanceId,
    DateTimeOffset HeartbeatUtc,
    string SelectedProject)
{
    public static RuntimeState CreateForTest(bool enabled, DateTimeOffset heartbeatUtc) =>
        new(1, enabled, 1234, heartbeatUtc.AddMinutes(-1), Guid.NewGuid(), heartbeatUtc,
            @"D:\projects\my-app");
}
