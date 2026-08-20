using CCAutoApprove.Infrastructure.Windows;

namespace CCAutoApprove.Infrastructure.Tests.Windows;

public sealed class WindowsStartupManagerTests
{
    private const string AppPath = @"C:\Program Files\CCAutoApprove\CCAutoApprove.App.exe";
    private const string ValueName = "CCAutoApprove";
    private const string ValueData = "\"C:\\Program Files\\CCAutoApprove\\CCAutoApprove.App.exe\" --minimized";

    [Fact]
    public void Enable_WhenDisabled_WritesExactCurrentUserRunValue()
    {
        var registry = new FakeUserRunRegistry();
        var manager = new WindowsStartupManager(AppPath, registry);

        manager.Enable();

        Assert.Equal(ValueData, registry.Values[ValueName]);
        Assert.Equal([(ValueName, ValueData)], registry.SetCalls);
    }

    [Fact]
    public void Enable_WhenAlreadyEnabled_DoesNotWriteAgain()
    {
        var registry = new FakeUserRunRegistry { Values = { [ValueName] = ValueData } };
        var manager = new WindowsStartupManager(AppPath, registry);

        manager.Enable();
        manager.Enable();

        Assert.Empty(registry.SetCalls);
    }

    [Fact]
    public void Disable_RemovesOnlyApplicationValue()
    {
        var registry = new FakeUserRunRegistry
        {
            Values =
            {
                [ValueName] = ValueData,
                ["OtherApplication"] = "other.exe"
            }
        };
        var manager = new WindowsStartupManager(AppPath, registry);

        manager.Disable();

        Assert.DoesNotContain(ValueName, registry.Values.Keys);
        Assert.Equal("other.exe", registry.Values["OtherApplication"]);
        Assert.Equal([ValueName], registry.DeleteCalls);
    }

    [Fact]
    public void IsEnabled_WhenValuePointsAtDifferentExecutable_ReturnsFalse()
    {
        var registry = new FakeUserRunRegistry { Values = { [ValueName] = "\"C:\\Old\\CCAutoApprove.App.exe\" --minimized" } };
        var manager = new WindowsStartupManager(AppPath, registry);

        bool enabled = manager.IsEnabled();

        Assert.False(enabled);
    }

    private sealed class FakeUserRunRegistry : IUserRunRegistry
    {
        public Dictionary<string, object?> Values { get; } = new(StringComparer.Ordinal);
        public List<(string Name, string Value)> SetCalls { get; } = [];
        public List<string> DeleteCalls { get; } = [];

        public object? GetValue(string name) => Values.GetValueOrDefault(name);

        public void SetValue(string name, string value)
        {
            SetCalls.Add((name, value));
            Values[name] = value;
        }

        public void DeleteValue(string name)
        {
            DeleteCalls.Add(name);
            Values.Remove(name);
        }
    }
}
