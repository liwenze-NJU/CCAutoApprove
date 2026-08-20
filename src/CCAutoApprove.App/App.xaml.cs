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
    private SingleInstanceGuard? singleInstanceGuard;
    private TrayIconService? trayService;
    private HeartbeatService? heartbeatService;

    protected override async void OnStartup(StartupEventArgs e)
    {
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

        try
        {
            var paths = new AppPaths();
            var settingsStore = new JsonSettingsStore(paths.SettingsPath);
            PersistentSettings settings = await settingsStore.LoadAsync(CancellationToken.None);
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
            var controller = new AppController(settingsStore, directoryService, doctor, heartbeatService);
            await controller.InitializeAsync(CancellationToken.None);

            IAuditLog auditLog = AppAuditMaintenance.CreateLog(paths, settings, clock);
            await AppAuditMaintenance.DeleteExpiredFailSafeAsync(
                auditLog,
                settings.AuditRetentionDays,
                CancellationToken.None);
            var status = new StatusViewModel(controller);
            var records = new RecordsViewModel(auditLog, ConfirmClearRecordsAsync, settings.AuditDetailLevel);
            var settingsViewModel = new SettingsViewModel(
                settingsStore,
                auditLog,
                ConfirmDeleteDetailedLogsAsync,
                () => hookManager.InstallAsync(CancellationToken.None),
                async () => _ = await doctor.RunAsync(CancellationToken.None),
                () => hookManager.UninstallAsync(CancellationToken.None),
                new WindowsStartupManager(ResolveAppExecutablePath()));
            await records.LoadAsync();
            await settingsViewModel.LoadAsync();
            status.SetHookHealth(await doctor.IsOperationalAsync(CancellationToken.None));
            DateOnly today = DateOnly.FromDateTime(DateTime.Now);
            using var countCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            status.SetTodayApprovalCount(await records.CountTodayApprovalsAsync(
                today,
                TimeZoneInfo.Local,
                countCancellation.Token));

            var mainViewModel = new MainViewModel(status, records, settingsViewModel);
            var mainWindow = new MainWindow(mainViewModel);
            MainWindow = mainWindow;
            trayService = new TrayIconService(controller, mainViewModel, mainWindow);
            mainWindow.AttachTray(trayService);
            if (ShouldShowMainWindow(e.Args))
            {
                mainWindow.Show();
            }
        }
        catch
        {
            MessageBox.Show(
                StringResources.Get("ApplicationStartFailed"),
                StringResources.Get("AppName"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
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
