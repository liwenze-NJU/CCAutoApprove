namespace CCAutoApprove.Infrastructure.Configuration;

public sealed class AppPaths
{
    public AppPaths(string? basePath = null)
    {
        BasePath = basePath
            ?? Environment.GetEnvironmentVariable("CCAA_DATA_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CCAutoApprove");
    }

    public string BasePath { get; }
    public string SettingsPath => Path.Combine(BasePath, "settings.json");
    public string RuntimeStatePath => Path.Combine(BasePath, "runtime.json");
}
