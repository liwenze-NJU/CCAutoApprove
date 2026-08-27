using System.IO;
using System.Windows;
using CCAutoApprove.App.Services;
using CCAutoApprove.App.ViewModels;
using CCAutoApprove.Core.Abstractions;
using CCAutoApprove.Core.Models;
using CCAutoApprove.Infrastructure.Claude;
using CCAutoApprove.Infrastructure.Configuration;
using CCAutoApprove.Infrastructure.Windows;
using MessageBox = System.Windows.MessageBox;

namespace CCAutoApprove.App;

public partial class App : System.Windows.Application
{
    private readonly CancellationTokenSource appLifetimeCancellation = new();
    private SingleInstanceGuard? singleInstanceGuard;
    private TrayIconService? trayService;
    private HeartbeatService? heartbeatService;
    private Task? retentionMaintenanceTask;

    protected override async void OnStartup(StartupEventArgs e)
    {
        CancellationToken appLifetimeToken = appLifetimeCancellation.Token;
        base.OnStartup(e);
        singleInstanceGuard = new SingleInstanceGuard();
        if (!singleInstanceGuard.TryAcquire())
        {
            MessageBox.Show(
                StringResources.Get("SecondInstanceMessage"),
                StringResources.Get("AppName"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        await AppStartupLifecycle.RunAsync(
            async lifetimeToken =>
            {
                var paths = new AppPaths();
                var settingsStore = new JsonSettingsStore(paths.SettingsPath);
                PersistentSettings settings = await settingsStore.LoadAsync(lifetimeToken);
                lifetimeToken.ThrowIfCancellationRequested();
                var clock = new SystemClock();
                var directoryService = new WindowsDirectoryService();
                var runtimeStore = new JsonRuntimeStateStore(paths.RuntimeStatePath);
                heartbeatService = new HeartbeatService(
                    runtimeStore,
                    clock,
                    new WindowsCurrentProcessInfo(),
                    directoryService,
                    new PeriodicHeartbeatTimer());

                string cliPath = ResolveCliPath();
                string claudeSettingsPath = ResolveClaudeSettingsPath();
                var hookManager = new ClaudeHookManager(claudeSettingsPath, cliPath);
                var doctor = new ClaudeDoctor(
                    claudeSettingsPath,
                    cliPath,
                    paths.BasePath,
                    paths.RuntimeStatePath,
                    settings.SelectedProject);
                var hookMaintenance = new HookMaintenanceService(hookManager, doctor);
                var controller = new AppController(
                    settingsStore,
                    directoryService,
                    doctor,
                    heartbeatService);
                await controller.InitializeAsync(lifetimeToken);
                lifetimeToken.ThrowIfCancellationRequested();

                IAuditLog auditLog = AppAuditMaintenance.CreateLog(paths, settings, clock);
                retentionMaintenanceTask = await AppAuditMaintenance.InitializeThenScheduleAsync(
                    async startupToken =>
                    {
                        startupToken.ThrowIfCancellationRequested();
                        StatusViewModel? status = null;
                        var records = new RecordsViewModel(
                            auditLog,
                            ConfirmClearRecordsAsync,
                            settings.AuditDetailLevel,
                            token => status?.RefreshTodayCountAsync(token) ?? Task.CompletedTask);
                        status = new StatusViewModel(
                            controller,
                            hookMaintenance,
                            async token =>
                            {
                                DateOnly today = DateOnly.FromDateTime(DateTime.Now);
                                using var countCancellation =
                                    CancellationTokenSource.CreateLinkedTokenSource(token);
                                countCancellation.CancelAfter(TimeSpan.FromSeconds(10));
                                return await records.CountTodayApprovalsAsync(
                                    today,
                                    TimeZoneInfo.Local,
                                    countCancellation.Token);
                            });
                        var settingsViewModel = new SettingsViewModel(
                            settingsStore,
                            auditLog,
                            ConfirmDeleteDetailedLogsAsync,
                            startupManager: new WindowsStartupManager(ResolveAppExecutablePath()),
                            confirmEnableDetailedAsync: ConfirmEnableDetailedLogsAsync,
                            hookMaintenanceService: hookMaintenance);
                        await records.LoadAsync(startupToken);
                        startupToken.ThrowIfCancellationRequested();
                        await settingsViewModel.LoadAsync();
                        startupToken.ThrowIfCancellationRequested();
                        await hookMaintenance.RefreshAsync(startupToken);
                        startupToken.ThrowIfCancellationRequested();

                        var mainViewModel = new MainViewModel(status, records, settingsViewModel);
                        var mainWindow = new MainWindow(mainViewModel);
                        MainWindow = mainWindow;
                        trayService = new TrayIconService(controller, mainViewModel, mainWindow);
                        mainWindow.AttachTray(trayService);
                        if (ShouldShowMainWindow(e.Args))
                        {
                            mainWindow.Show();
                        }
                    },
                    auditLog,
                    settings.AuditRetentionDays,
                    lifetimeToken);
            },
            () =>
            {
                MessageBox.Show(
                    StringResources.Get("ApplicationStartFailed"),
                    StringResources.Get("AppName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Shutdown(1);
            },
            appLifetimeToken);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppAuditMaintenance.CancelLifetimeWithoutWaiting(
            appLifetimeCancellation,
            retentionMaintenanceTask);
        trayService?.Dispose();
        if (heartbeatService is not null)
        {
            heartbeatService.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        singleInstanceGuard?.Dispose();
        base.OnExit(e);
    }

    private static Task<bool> ConfirmClearRecordsAsync() => Task.FromResult(
        MessageBox.Show(
            Current.MainWindow,
            StringResources.Get("ClearRecordsConfirm"),
            StringResources.Get("ConfirmTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes);

    private static Task<bool> ConfirmDeleteDetailedLogsAsync() => Task.FromResult(
        MessageBox.Show(
            Current.MainWindow,
            StringResources.Get("DeleteDetailedLogsPrompt"),
            StringResources.Get("ConfirmTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes);

    private static Task<bool> ConfirmEnableDetailedLogsAsync() => Task.FromResult(
        MessageBox.Show(
            Current.MainWindow,
            StringResources.Get("EnableDetailedLogsPrompt"),
            StringResources.Get("ConfirmTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes);

    private static string ResolveClaudeSettingsPath()
    {
        string? configured = Environment.GetEnvironmentVariable("CCAA_CLAUDE_SETTINGS_PATH");
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json")
            : configured;
    }

    private static string ResolveCliPath()
    {
        return ResolveCliPath(
            AppContext.BaseDirectory,
            Environment.GetEnvironmentVariable("CCAA_CLI_PATH"));
    }

    internal static string ResolveCliPath(string appBaseDirectory, string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return configuredPath;
        }

        string fullAppDirectory = Path.GetFullPath(appBaseDirectory);
        string installRoot = Directory.GetParent(
            Path.TrimEndingDirectorySeparator(fullAppDirectory))?.FullName
            ?? throw new InvalidOperationException("The application directory has no parent.");
        return Path.Combine(installRoot, "cli", "CCAutoApprove.Cli.exe");
    }

    private static string ResolveAppExecutablePath() =>
        Environment.ProcessPath
        ?? Path.Combine(AppContext.BaseDirectory, "CCAutoApprove.App.exe");

    internal static bool ShouldShowMainWindow(IEnumerable<string> arguments) =>
        !arguments.Any(argument => string.Equals(argument, "--minimized", StringComparison.OrdinalIgnoreCase));
}
