using System.ComponentModel;
using System.Diagnostics;
using CCAutoApprove.Core.Abstractions;
using CCAutoApprove.Core.Models;

namespace CCAutoApprove.Infrastructure.Windows;

public sealed class WindowsProcessIdentityValidator : IProcessIdentityValidator
{
    public bool IsValid(RuntimeState state)
    {
        try
        {
            using Process process = Process.GetProcessById(state.ProcessId);
            DateTimeOffset actualStart = process.StartTime.ToUniversalTime();
            return Math.Abs((actualStart - state.ProcessStartUtc).TotalSeconds) <= 1;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }
}
