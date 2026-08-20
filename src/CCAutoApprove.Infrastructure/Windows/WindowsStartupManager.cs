using Microsoft.Win32;
using CCAutoApprove.Core.Abstractions;

namespace CCAutoApprove.Infrastructure.Windows;

public sealed class WindowsStartupManager : IStartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CCAutoApprove";
    private readonly IUserRunRegistry registry;
    private readonly string valueData;

    public WindowsStartupManager(string applicationPath)
        : this(applicationPath, new CurrentUserRunRegistry())
    {
    }

    internal WindowsStartupManager(string applicationPath, IUserRunRegistry registry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationPath);
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        valueData = $"\"{applicationPath}\" --minimized";
    }

    public bool IsEnabled() => string.Equals(
        registry.GetValue(ValueName) as string,
        valueData,
        StringComparison.Ordinal);

    public void Enable()
    {
        if (!IsEnabled())
        {
            registry.SetValue(ValueName, valueData);
        }
    }

    public void Disable()
    {
        if (registry.GetValue(ValueName) is not null)
        {
            registry.DeleteValue(ValueName);
        }
    }

    private sealed class CurrentUserRunRegistry : IUserRunRegistry
    {
        public object? GetValue(string name)
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(name);
        }

        public void SetValue(string name, string value)
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            (key ?? throw new InvalidOperationException("The current-user Run key is unavailable."))
                .SetValue(name, value, RegistryValueKind.String);
        }

        public void DeleteValue(string name)
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            (key ?? throw new InvalidOperationException("The current-user Run key is unavailable."))
                .DeleteValue(name, throwOnMissingValue: false);
        }
    }
}

internal interface IUserRunRegistry
{
    object? GetValue(string name);
    void SetValue(string name, string value);
    void DeleteValue(string name);
}
