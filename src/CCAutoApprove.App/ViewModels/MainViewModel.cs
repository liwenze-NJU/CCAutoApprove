using System.Collections.ObjectModel;
using System.ComponentModel;
using CCAutoApprove.App.Services;

namespace CCAutoApprove.App.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private NavigationPage? selectedPage;

    public MainViewModel(StatusViewModel status, RecordsViewModel records, SettingsViewModel settings)
    {
        Status = status ?? throw new ArgumentNullException(nameof(status));
        Records = records ?? throw new ArgumentNullException(nameof(records));
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        Settings.AuditDetailLevelChanged += Records.SetAuditDetailLevel;
        StatusPage = new NavigationPage(StringResources.Get("NavStatus"), Status);
        RecordsPage = new NavigationPage(StringResources.Get("NavRecords"), Records);
        SettingsPage = new NavigationPage(StringResources.Get("NavSettings"), Settings);
        Pages = [StatusPage, RecordsPage, SettingsPage];
        selectedPage = StatusPage;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public StatusViewModel Status { get; }
    public RecordsViewModel Records { get; }
    public SettingsViewModel Settings { get; }
    public ObservableCollection<NavigationPage> Pages { get; }
    public NavigationPage StatusPage { get; }
    public NavigationPage RecordsPage { get; }
    public NavigationPage SettingsPage { get; }

    public NavigationPage? SelectedPage
    {
        get => selectedPage;
        set
        {
            if (ReferenceEquals(selectedPage, value))
            {
                return;
            }

            selectedPage = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedPage)));
        }
    }

    public void SelectStatus() => SelectedPage = StatusPage;
    public void SelectRecords() => SelectedPage = RecordsPage;
    public void SelectSettings() => SelectedPage = SettingsPage;
}

public sealed record NavigationPage(string Title, object Content);
