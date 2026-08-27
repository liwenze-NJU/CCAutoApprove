using System.ComponentModel;
using System.Windows;
using CCAutoApprove.App.Services;
using CCAutoApprove.App.ViewModels;
using Microsoft.Win32;

namespace CCAutoApprove.App;

public partial class MainWindow : Window
{
    private TrayIconService? trayService;

    public MainWindow(MainViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        DataContext = viewModel;
        viewModel.Status.ChooseProjectPath = ChooseProject;
    }

    public void AttachTray(TrayIconService service) =>
        trayService = service ?? throw new ArgumentNullException(nameof(service));

    private string? ChooseProject()
    {
        var dialog = new OpenFolderDialog
        {
            Title = StringResources.Get("ChooseProject"),
            Multiselect = false
        };
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (trayService is not null && !trayService.IsExitRequested)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }
}
