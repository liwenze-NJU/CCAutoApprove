using System.Windows;

namespace CCAutoApprove.App.Services;

public static class StringResources
{
    private static readonly Lazy<ResourceDictionary> Fallback = new(() => new ResourceDictionary
    {
        Source = new Uri("/CCAutoApprove.App;component/Resources/Strings.zh-CN.xaml", UriKind.RelativeOrAbsolute)
    });

    public static string Get(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return System.Windows.Application.Current?.TryFindResource(key) as string
            ?? Fallback.Value[key] as string
            ?? key;
    }
}
