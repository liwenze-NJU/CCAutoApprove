namespace CCAutoApprove.Infrastructure.Claude;

public enum DoctorSeverity
{
    Pass,
    Warning,
    Error
}

public sealed record DoctorCheck(string Code, DoctorSeverity Severity, string Message);
