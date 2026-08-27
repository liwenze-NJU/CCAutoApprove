using CCAutoApprove.Cli;

try
{
    Environment.ExitCode = await CliApplication.RunProductionAsync(
        args,
        Console.OpenStandardInput(),
        Console.OpenStandardOutput(),
        Console.Error,
        CancellationToken.None);
}
catch
{
    bool isHook = string.Equals(
        args.FirstOrDefault(),
        "hook",
        StringComparison.OrdinalIgnoreCase);
    Environment.ExitCode = isHook ? 0 : 1;
    if (!isHook)
    {
        await Console.Error.WriteLineAsync("CCAutoApprove: command initialization failed.");
    }
}
