using System.Globalization;
using CCAutoApprove.Infrastructure.Claude;

namespace CCAutoApprove.App.Services;

public static class HookDiagnosticMessageFormatter
{
    public static string Format(IReadOnlyList<DoctorCheck> checks)
    {
        DoctorCheck? failedCheck = checks.FirstOrDefault(check => check.Severity == DoctorSeverity.Error);
        if (failedCheck is null)
        {
            return StringResources.Get("ErrorHookNotOperational");
        }

        return string.Format(
            CultureInfo.CurrentCulture,
            StringResources.Get("HookDiagnosticFailureFormat"),
            failedCheck.Code,
            failedCheck.Message);
    }
}
