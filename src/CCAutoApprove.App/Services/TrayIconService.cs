using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using CCAutoApprove.App.ViewModels;
using Application = System.Windows.Application;

namespace CCAutoApprove.App.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly AppController controller;
    private readonly MainViewModel mainViewModel;
    private readonly Window mainWindow;
    private readonly NotifyIcon notifyIcon;
    private readonly ToolStripMenuItem statusItem;
    private readonly ToolStripMenuItem projectItem;
    private readonly ToolStripMenuItem toggleItem;
    private bool disposed;

    public TrayIconService(AppController controller, MainViewModel mainViewModel, Window mainWindow)
    {
        this.controller = controller ?? throw new ArgumentNullException(nameof(controller));
        this.mainViewModel = mainViewModel ?? throw new ArgumentNullException(nameof(mainViewModel));
        this.mainWindow = mainWindow ?? throw new ArgumentNullException(nameof(mainWindow));

        statusItem = new ToolStripMenuItem { Enabled = false };
        projectItem = new ToolStripMenuItem { Enabled = false };
        toggleItem = new ToolStripMenuItem();
        toggleItem.Click += (_, _) => Dispatch(ToggleApprovalAsync);

        var openItem = new ToolStripMenuItem(StringResources.Get("OpenMainWindow"));
        openItem.Click += (_, _) => Dispatch(() => OpenPage(mainViewModel.SelectStatus));
        var recordsItem = new ToolStripMenuItem(StringResources.Get("NavRecords"));
        recordsItem.Click += (_, _) => Dispatch(async () =>
        {
            await mainViewModel.Records.RefreshCommand.ExecuteAsync();
            OpenPage(mainViewModel.SelectRecords);
        });
        var settingsItem = new ToolStripMenuItem(StringResources.Get("NavSettings"));
        settingsItem.Click += (_, _) => Dispatch(() => OpenPage(mainViewModel.SelectSettings));
        var exitItem = new ToolStripMenuItem(StringResources.Get("ExitApplication"));
        exitItem.Click += (_, _) => Dispatch(ExitAsync);

        var menu = new ContextMenuStrip();
        menu.Items.AddRange([
            statusItem,
            projectItem,
            new ToolStripSeparator(),
            toggleItem,
            openItem,
            recordsItem,
            settingsItem,
            new ToolStripSeparator(),
            exitItem
        ]);

        notifyIcon = new NotifyIcon
        {
            Text = StringResources.Get("AppName"),
            Icon = SystemIcons.Shield,
            ContextMenuStrip = menu,
            Visible = true
        };
        notifyIcon.DoubleClick += (_, _) => Dispatch(() => OpenPage(mainViewModel.SelectStatus));
        controller.StateChanged += OnControllerStateChanged;
        RefreshMenu();
    }

    public bool IsExitRequested { get; private set; }

    private void OnControllerStateChanged(object? sender, EventArgs eventArgs) => Dispatch(RefreshMenu);

    private async Task ToggleApprovalAsync()
    {
        await mainViewModel.Status.ToggleApprovalCommand.ExecuteAsync();
    }

    private void RefreshMenu()
    {
        string status = controller.IsEnabled
            ? StringResources.Get("StatusRunning")
            : StringResources.Get("StatusPaused");
        statusItem.Text = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            StringResources.Get("TrayStatusFormat"),
            status);
        string project = string.IsNullOrWhiteSpace(controller.SelectedProject)
            ? StringResources.Get("NoProjectSelected")
            : controller.SelectedProject;
        projectItem.Text = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            StringResources.Get("TrayProjectFormat"),
            project);
        toggleItem.Text = controller.IsEnabled
            ? StringResources.Get("PauseApproval")
            : StringResources.Get("EnableApproval");
    }

    private void OpenPage(Action selectPage)
    {
        selectPage();
        if (!mainWindow.IsVisible)
        {
            mainWindow.Show();
        }

        if (mainWindow.WindowState == WindowState.Minimized)
        {
            mainWindow.WindowState = WindowState.Normal;
        }

        mainWindow.Activate();
    }

    private async Task ExitAsync()
    {
        if (IsExitRequested)
        {
            return;
        }

        IsExitRequested = true;
        try
        {
            await controller.ShutdownAsync(CancellationToken.None);
        }
        catch
        {
            // Shutdown still proceeds after the controller has attempted its fail-safe disable.
        }
        finally
        {
            Dispose();
            Application.Current.Shutdown();
        }
    }

    private static void Dispatch(Action action)
    {
        if (Application.Current.Dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _ = Application.Current.Dispatcher.BeginInvoke(action);
        }
    }

    private static void Dispatch(Func<Task> action)
    {
        if (Application.Current.Dispatcher.CheckAccess())
        {
            _ = RunSafelyAsync(action);
        }
        else
        {
            _ = Application.Current.Dispatcher.InvokeAsync(() => RunSafelyAsync(action)).Task.Unwrap();
        }
    }

    private static async Task RunSafelyAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch
        {
            // Tray callbacks must not escape the UI event boundary.
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        controller.StateChanged -= OnControllerStateChanged;
        notifyIcon.Visible = false;
        notifyIcon.Dispose();
    }
}
