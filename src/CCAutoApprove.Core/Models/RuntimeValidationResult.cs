namespace CCAutoApprove.Core.Models;

public sealed record RuntimeValidationResult(bool IsValid, string? ErrorCode)
{
    public static RuntimeValidationResult Valid() => new(true, null);

    public static RuntimeValidationResult Invalid(string code) => new(false, code);
}
