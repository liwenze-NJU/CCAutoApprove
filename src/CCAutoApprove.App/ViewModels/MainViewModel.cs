namespace CCAutoApprove.App.ViewModels;

public sealed class MainViewModel
{
    public MainViewModel(StatusViewModel status, RecordsViewModel records, SettingsViewModel settings)
    {
        Status = status ?? throw new ArgumentNullException(nameof(status));
        Records = records ?? throw new ArgumentNullException(nameof(records));
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public StatusViewModel Status { get; }
    public RecordsViewModel Records { get; }
    public SettingsViewModel Settings { get; }
}
