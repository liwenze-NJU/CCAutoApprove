using CCAutoApprove.App.Services;

namespace CCAutoApprove.App.Tests.Services;

public sealed class StatusPresentationMapperTests
{
    [Theory]
    [InlineData(true, null, true, StatusVisualState.Running, TrayIconKind.Running)]
    [InlineData(false, null, true, StatusVisualState.Paused, TrayIconKind.Paused)]
    [InlineData(false, null, false, StatusVisualState.Error, TrayIconKind.Error)]
    [InlineData(true, AppController.HeartbeatWriteFailed, true, StatusVisualState.Error, TrayIconKind.Error)]
    public void Map_UsesDistinctAccessibleTextIconAndColorSemantics(
        bool enabled,
        string? errorCode,
        bool? hookOperational,
        StatusVisualState expectedState,
        TrayIconKind expectedTrayIcon)
    {
        StatusPresentation presentation = StatusPresentationMapper.Map(
            enabled,
            errorCode,
            hookOperational);

        Assert.Equal(expectedState, presentation.State);
        Assert.Equal(expectedTrayIcon, presentation.TrayIcon);
        Assert.False(string.IsNullOrWhiteSpace(presentation.Text));
        Assert.False(string.IsNullOrWhiteSpace(presentation.IconGlyph));
        Assert.StartsWith("#", presentation.Color, StringComparison.Ordinal);
    }

    [Fact]
    public void Map_AllStatesHaveDistinctTextGlyphColorAndTrayIcon()
    {
        StatusPresentation[] presentations =
        [
            StatusPresentationMapper.Map(true, null, true),
            StatusPresentationMapper.Map(false, null, true),
            StatusPresentationMapper.Map(false, AppController.HookNotOperational, false)
        ];

        Assert.Equal(3, presentations.Select(item => item.Text).Distinct().Count());
        Assert.Equal(3, presentations.Select(item => item.IconGlyph).Distinct().Count());
        Assert.Equal(3, presentations.Select(item => item.Color).Distinct().Count());
        Assert.Equal(3, presentations.Select(item => item.TrayIcon).Distinct().Count());
    }
}
