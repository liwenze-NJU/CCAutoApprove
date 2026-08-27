namespace CCAutoApprove.App.Services;

public enum StatusVisualState
{
    Running,
    Paused,
    Error
}

public enum TrayIconKind
{
    Running,
    Paused,
    Error
}

public sealed record StatusPresentation(
    StatusVisualState State,
    string Text,
    string IconGlyph,
    string Color,
    TrayIconKind TrayIcon);

public static class StatusPresentationMapper
{
    public static StatusPresentation Map(
        bool enabled,
        string? errorCode,
        bool? hookOperational)
    {
        StatusVisualState state = errorCode is not null || hookOperational == false
            ? StatusVisualState.Error
            : enabled
                ? StatusVisualState.Running
                : StatusVisualState.Paused;
        return state switch
        {
            StatusVisualState.Running => new(
                state,
                StringResources.Get("StatusRunning"),
                StringResources.Get("StatusIconRunning"),
                "#FF237A3B",
                TrayIconKind.Running),
            StatusVisualState.Paused => new(
                state,
                StringResources.Get("StatusPaused"),
                StringResources.Get("StatusIconPaused"),
                "#FF646B75",
                TrayIconKind.Paused),
            _ => new(
                state,
                StringResources.Get("StatusError"),
                StringResources.Get("StatusIconError"),
                "#FFB42318",
                TrayIconKind.Error)
        };
    }
}
