using CCAutoApprove.Infrastructure.Windows;

namespace CCAutoApprove.Infrastructure.Tests.Windows;

public sealed class WindowsProjectMatcherTests
{
    [Theory]
    [InlineData(@"D:\projects\my-app", @"D:\projects\my-app", true)]
    [InlineData(@"D:\projects\my-app\", @"d:\PROJECTS\MY-APP", true)]
    [InlineData(@"D:\projects\my-app", @"D:\projects\my-app\src", true)]
    [InlineData(@"D:\projects\my-app", @"D:\projects\my-app-old", false)]
    [InlineData(@"D:\projects\my-app", @"D:\projects", false)]
    [InlineData(@"D:\projects\my-app", @"E:\projects\my-app", false)]
    public void IsMatch_UsesDirectoryBoundaries(string selected, string request, bool expected)
    {
        var matcher = new WindowsProjectMatcher();

        Assert.Equal(expected, matcher.IsMatch(selected, request));
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative\\path")]
    [InlineData("bad\0path")]
    public void IsMatch_InvalidRequestPath_ReturnsFalse(string request)
    {
        var matcher = new WindowsProjectMatcher();

        Assert.False(matcher.IsMatch(@"D:\projects\my-app", request));
    }
}
