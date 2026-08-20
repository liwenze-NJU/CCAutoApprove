using System.Windows;

namespace CCAutoApprove.App.Services;

public static class StringResources
{
    private static readonly Lazy<IReadOnlyDictionary<string, string>> Fallback = new(LoadFallback);

    public static string Get(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return System.Windows.Application.Current?.TryFindResource(key) as string
            ?? Fallback.Value.GetValueOrDefault(key)
            ?? key;
    }

    private static IReadOnlyDictionary<string, string> LoadFallback()
    {
        var resources = new ResourceDictionary
        {
            Source = new Uri("/CCAutoApprove.App;component/Resources/Strings.zh-CN.xaml", UriKind.RelativeOrAbsolute)
        };
        return resources.Keys
            .OfType<string>()
            .ToDictionary(
                key => key,
                key => resources[key] as string ?? key,
                StringComparer.Ordinal);
    }
}
