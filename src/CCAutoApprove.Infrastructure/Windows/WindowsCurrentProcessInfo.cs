using System.Diagnostics;
using CCAutoApprove.Core.Abstractions;

namespace CCAutoApprove.Infrastructure.Windows;

public sealed class WindowsCurrentProcessInfo : ICurrentProcessInfo
{
    public WindowsCurrentProcessInfo()
    {
        using Process process = Process.GetCurrentProcess();
        ProcessId = process.Id;
        ProcessStartUtc = process.StartTime.ToUniversalTime();
    }

    public int ProcessId { get; }
    public DateTimeOffset ProcessStartUtc { get; }
}
