using CCAutoApprove.App.ViewModels;
using CCAutoApprove.App;
using CCAutoApprove.App.Services;
using CCAutoApprove.Core.Abstractions;
using CCAutoApprove.Core.Models;
using CCAutoApprove.Infrastructure.Claude;

namespace CCAutoApprove.App.Tests.ViewModels;

public sealed class SettingsViewModelTests
{
    [Theory]
    [InlineData(new[] { "--minimized" }, false)]
    [InlineData(new[] { "--MINIMIZED" }, false)]
    [InlineData(new[] { "--minimized=true" }, true)]
    [InlineData(new string[0], true)]
    public void ShouldShowMainWindow_OnlySuppressesBareMinimizedArgument(string[] arguments, bool expected)
    {
        Assert.Equal(expected, App.ShouldShowMainWindow(arguments));
    }

    [Fact]
    public void ResolveCliPath_UsesPublishedSiblingDirectoryUnlessExplicitlyConfigured()
    {
        string appDirectory = Path.Combine("C:\\Program Files", "CCAutoApprove", "app");
        string configuredPath = Path.Combine("D:\\tools", "custom-cli.exe");

        string defaultPath = App.ResolveCliPath(appDirectory, configuredPath: null);
        string overriddenPath = App.ResolveCliPath(appDirectory, configuredPath);

        Assert.Equal(
            Path.Combine("C:\\Program Files", "CCAutoApprove", "cli", "CCAutoApprove.Cli.exe"),
            defaultPath);
        Assert.Equal(configuredPath, overriddenPath);
    }

    [Fact]
    public void ApplicationVersionText_UsesCentralizedFormatAndInjectedVersion()
    {
        var viewModel = new SettingsViewModel(
            new FakeSettingsStore(new PersistentSettings()),
            new FakeAuditLog(),
            () => Task.FromResult(false),
            applicationVersion: "1.2.3");

        Assert.Equal(
            string.Format(StringResources.Get("ApplicationVersionFormat"), "1.2.3"),
            viewModel.ApplicationVersionText);
    }

    [Fact]
    public async Task ChangeAuditDetailLevelAsync_FromDetailedToPrivacySafe_DeletesWhenUserChoosesDelete()
    {
        var settingsStore = new FakeSettingsStore(new PersistentSettings(AuditDetailLevel: AuditDetailLevel.Detailed));
        var auditLog = new FakeAuditLog();
        int promptCount = 0;
        var viewModel = new SettingsViewModel(settingsStore, auditLog, () =>
        {
            promptCount++;
            return Task.FromResult(true);
        });
        await viewModel.LoadAsync();

        await viewModel.ChangeAuditDetailLevelAsync(AuditDetailLevel.PrivacySafe);

        Assert.Equal(1, promptCount);
        Assert.True(auditLog.DeleteDetailedCalled);
        Assert.False(auditLog.ClearCalled);
        Assert.Equal(AuditDetailLevel.PrivacySafe, viewModel.AuditDetailLevel);
        Assert.Equal(AuditDetailLevel.PrivacySafe, settingsStore.Settings.AuditDetailLevel);
    }

    [Fact]
    public async Task ChangeAuditDetailLevelAsync_FromDetailedToPrivacySafe_KeepsLogsWhenUserDeclinesDelete()
    {
        var settingsStore = new FakeSettingsStore(new PersistentSettings(AuditDetailLevel: AuditDetailLevel.Detailed));
        var auditLog = new FakeAuditLog();
        var viewModel = new SettingsViewModel(settingsStore, auditLog, () => Task.FromResult(false));
        var changedLevels = new List<AuditDetailLevel>();
        viewModel.AuditDetailLevelChanged += changedLevels.Add;
        await viewModel.LoadAsync();

        await viewModel.ChangeAuditDetailLevelAsync(AuditDetailLevel.PrivacySafe);

        Assert.False(auditLog.ClearCalled);
        Assert.Equal(AuditDetailLevel.PrivacySafe, settingsStore.Settings.AuditDetailLevel);
        Assert.Equal([AuditDetailLevel.PrivacySafe], changedLevels);
    }

    [Fact]
    public async Task ChangeAuditDetailLevelAsync_EnteringDetailed_ConfirmsBeforePersistence()
    {
        var settingsStore = new FakeSettingsStore(new PersistentSettings(AuditDetailLevel: AuditDetailLevel.PrivacySafe));
        var auditLog = new FakeAuditLog();
        int promptCount = 0;
        var viewModel = new SettingsViewModel(
            settingsStore,
            auditLog,
            () => Task.FromResult(false),
            confirmEnableDetailedAsync: () =>
            {
                promptCount++;
                return Task.FromResult(true);
            });
        await viewModel.LoadAsync();

        await viewModel.ChangeAuditDetailLevelAsync(AuditDetailLevel.Detailed);

        Assert.Equal(1, promptCount);
        Assert.False(auditLog.ClearCalled);
        Assert.Equal(AuditDetailLevel.Detailed, settingsStore.Settings.AuditDetailLevel);
    }

    [Fact]
    public async Task ChangeAuditDetailLevelAsync_EnteringDetailedWhenConsentIsCanceled_KeepsOldLevel()
    {
        var settingsStore = new FakeSettingsStore(
            new PersistentSettings(AuditDetailLevel: AuditDetailLevel.PrivacySafe));
        var auditLog = new FakeAuditLog();
        var viewModel = new SettingsViewModel(
            settingsStore,
            auditLog,
            () => Task.FromResult(false),
            confirmEnableDetailedAsync: () => Task.FromResult(false));
        await viewModel.LoadAsync();

        await viewModel.ChangeAuditDetailLevelAsync(AuditDetailLevel.Detailed);

        Assert.Equal(AuditDetailLevel.PrivacySafe, viewModel.AuditDetailLevel);
        Assert.Equal(AuditDetailLevel.PrivacySafe, settingsStore.Settings.AuditDetailLevel);
        Assert.False(auditLog.DeleteDetailedCalled);
    }

    [Fact]
    public async Task AuditAndStartupUpdates_AfterProjectChange_PreserveEveryLatestField()
    {
        const string latestProject = @"D:\projects\latest";
        var settingsStore = new FakeSettingsStore(new PersistentSettings());
        var startupManager = new FakeStartupManager();
        var viewModel = new SettingsViewModel(
            settingsStore,
            new FakeAuditLog(),
            () => Task.FromResult(false),
            startupManager: startupManager);
        await viewModel.LoadAsync();

        settingsStore.Replace(settingsStore.Settings with { SelectedProject = latestProject });
        await viewModel.ChangeAuditDetailLevelAsync(AuditDetailLevel.Disabled);
        string? projectAfterAuditUpdate = settingsStore.Settings.SelectedProject;

        settingsStore.Replace(settingsStore.Settings with { SelectedProject = latestProject });
        await viewModel.ChangeStartupEnabledAsync(true);

        Assert.Equal(latestProject, projectAfterAuditUpdate);
        Assert.Equal(latestProject, settingsStore.Settings.SelectedProject);
        Assert.Equal(AuditDetailLevel.Disabled, settingsStore.Settings.AuditDetailLevel);
        Assert.True(settingsStore.Settings.StartWithWindows);
    }

    [Fact]
    public async Task ChangeStartupEnabledAsync_WhenEnabled_RegistersStartupBeforePersistingSetting()
    {
        var operations = new List<string>();
        var settingsStore = new FakeSettingsStore(new PersistentSettings(), operations);
        var startupManager = new FakeStartupManager(operations);
        var viewModel = new SettingsViewModel(
            settingsStore,
            new FakeAuditLog(),
            () => Task.FromResult(false),
            startupManager: startupManager);
        await viewModel.LoadAsync();

        await viewModel.ChangeStartupEnabledAsync(true);

        Assert.True(viewModel.StartupEnabled);
        Assert.True(settingsStore.Settings.StartWithWindows);
        Assert.Equal(["enable", "save"], operations);
    }

    [Fact]
    public async Task ChangeStartupEnabledAsync_WhenRegistryFails_PreservesCheckboxAndPersistedSetting()
    {
        var settingsStore = new FakeSettingsStore(new PersistentSettings(StartWithWindows: false));
        var startupManager = new FakeStartupManager { EnableException = new IOException("Registry is unavailable.") };
        var viewModel = new SettingsViewModel(
            settingsStore,
            new FakeAuditLog(),
            () => Task.FromResult(false),
            startupManager: startupManager);
        await viewModel.LoadAsync();
        var changedProperties = new List<string?>();
        viewModel.PropertyChanged += (_, eventArgs) => changedProperties.Add(eventArgs.PropertyName);

        await viewModel.ChangeStartupEnabledAsync(true);

        Assert.False(viewModel.StartupEnabled);
        Assert.False(settingsStore.Settings.StartWithWindows);
        Assert.Equal(StringResources.Get("ErrorOperationFailed"), viewModel.OperationMessage);
        Assert.DoesNotContain("Registry is unavailable.", viewModel.OperationMessage);
        Assert.Contains(nameof(SettingsViewModel.StartupEnabled), changedProperties);
        Assert.Equal(["enable"], startupManager.Operations);
    }

    [Fact]
    public async Task ChangeStartupEnabledAsync_WhenEnablePersistenceFails_CompensatesRegistryAndRetainsDisabledState()
    {
        var settingsStore = new FakeSettingsStore(new PersistentSettings())
        {
            SaveException = new IOException("settings write failed")
        };
        var startupManager = new FakeStartupManager();
        var viewModel = new SettingsViewModel(
            settingsStore,
            new FakeAuditLog(),
            () => Task.FromResult(false),
            startupManager: startupManager);
        await viewModel.LoadAsync();

        await viewModel.ChangeStartupEnabledAsync(true);

        Assert.False(startupManager.Enabled);
        Assert.False(viewModel.StartupEnabled);
        Assert.False(settingsStore.Settings.StartWithWindows);
        Assert.Equal(["enable", "disable"], startupManager.Operations);
        Assert.Equal(StringResources.Get("ErrorOperationFailed"), viewModel.OperationMessage);
    }

    [Fact]
    public async Task ChangeStartupEnabledAsync_WhenDisablePersistenceFails_CompensatesRegistryAndRetainsEnabledState()
    {
        var settingsStore = new FakeSettingsStore(new PersistentSettings(StartWithWindows: true))
        {
            SaveException = new IOException("settings write failed")
        };
        var startupManager = new FakeStartupManager { Enabled = true };
        var viewModel = new SettingsViewModel(
            settingsStore,
            new FakeAuditLog(),
            () => Task.FromResult(false),
            startupManager: startupManager);
        await viewModel.LoadAsync();

        await viewModel.ChangeStartupEnabledAsync(false);

        Assert.True(startupManager.Enabled);
        Assert.True(viewModel.StartupEnabled);
        Assert.True(settingsStore.Settings.StartWithWindows);
        Assert.Equal(["disable", "enable"], startupManager.Operations);
        Assert.Equal(StringResources.Get("ErrorOperationFailed"), viewModel.OperationMessage);
    }

    [Fact]
    public async Task ChangeStartupEnabledAsync_WhenEnablePersistenceRollbackAndReconciliationFail_ExposesUnknownState()
    {
        var settingsStore = new FakeSettingsStore(new PersistentSettings())
        {
            SaveException = new IOException("settings write failed")
        };
        var startupManager = new FakeStartupManager
        {
            DisableException = new IOException("rollback failed"),
            ThrowOnIsEnabledCall = 3
        };
        var viewModel = new SettingsViewModel(
            settingsStore,
            new FakeAuditLog(),
            () => Task.FromResult(false),
            startupManager: startupManager);
        await viewModel.LoadAsync();

        await viewModel.ChangeStartupEnabledAsync(true);

        Assert.Null(viewModel.StartupEnabled);
        Assert.False(viewModel.CanChangeStartup);
        Assert.True(viewModel.CanRecheckStartup);
        Assert.False(settingsStore.Settings.StartWithWindows);
        Assert.Equal(["enable", "disable"], startupManager.Operations);
        Assert.Equal(StringResources.Get("StartupStateUnknown"), viewModel.OperationMessage);

        await viewModel.RecheckStartupCommand.ExecuteAsync();

        Assert.True(viewModel.StartupEnabled);
        Assert.True(viewModel.CanChangeStartup);
        Assert.False(viewModel.CanRecheckStartup);
        Assert.Null(viewModel.OperationMessage);
    }

    [Fact]
    public async Task ChangeStartupEnabledAsync_WhenDisablePersistenceRollbackAndReconciliationFail_ExposesUnknownState()
    {
        var settingsStore = new FakeSettingsStore(new PersistentSettings(StartWithWindows: true))
        {
            SaveException = new IOException("settings write failed")
        };
        var startupManager = new FakeStartupManager
        {
            Enabled = true,
            EnableException = new IOException("rollback failed"),
            ThrowOnIsEnabledCall = 3
        };
        var viewModel = new SettingsViewModel(
            settingsStore,
            new FakeAuditLog(),
            () => Task.FromResult(false),
            startupManager: startupManager);
        await viewModel.LoadAsync();

        await viewModel.ChangeStartupEnabledAsync(false);

        Assert.Null(viewModel.StartupEnabled);
        Assert.False(viewModel.CanChangeStartup);
        Assert.True(viewModel.CanRecheckStartup);
        Assert.True(settingsStore.Settings.StartWithWindows);
        Assert.Equal(["disable", "enable"], startupManager.Operations);
        Assert.Equal(StringResources.Get("StartupStateUnknown"), viewModel.OperationMessage);

        await viewModel.RecheckStartupCommand.ExecuteAsync();

        Assert.False(viewModel.StartupEnabled);
        Assert.True(viewModel.CanChangeStartup);
        Assert.False(viewModel.CanRecheckStartup);
        Assert.Null(viewModel.OperationMessage);
    }

    [Fact]
    public async Task ChangeStartupEnabledAsync_WhenUnknownStateAccessRecovers_EnforcesRequestedState()
    {
        var settingsStore = new FakeSettingsStore(new PersistentSettings());
        var startupManager = new FakeStartupManager { ThrowOnIsEnabledCall = 1 };
        var viewModel = new SettingsViewModel(
            settingsStore,
            new FakeAuditLog(),
            () => Task.FromResult(false),
            startupManager: startupManager);

        await viewModel.LoadAsync();
        await viewModel.RecheckStartupCommand.ExecuteAsync();
        await viewModel.ChangeStartupEnabledAsync(true);

        Assert.False(viewModel.CanRecheckStartup);
        Assert.True(viewModel.StartupEnabled);
        Assert.True(settingsStore.Settings.StartWithWindows);
        Assert.Equal(["enable"], startupManager.Operations);
    }

    [Fact]
    public async Task ChangeStartupEnabledAsync_AfterUnknownRecovery_PreservesExternalSettingsUpdates()
    {
        const string latestProject = @"D:\projects\latest";
        var settingsStore = new FakeSettingsStore(new PersistentSettings())
        {
            SaveException = new IOException("settings write failed")
        };
        var startupManager = new FakeStartupManager
        {
            DisableException = new IOException("rollback failed"),
            ThrowOnIsEnabledCall = 3
        };
        var viewModel = new SettingsViewModel(
            settingsStore,
            new FakeAuditLog(),
            () => Task.FromResult(false),
            startupManager: startupManager);
        await viewModel.LoadAsync();
        settingsStore.Replace(settingsStore.Settings with
        {
            SelectedProject = latestProject,
            AuditDetailLevel = AuditDetailLevel.Detailed
        });

        await viewModel.ChangeStartupEnabledAsync(true);
        Assert.Null(viewModel.StartupEnabled);

        settingsStore.SaveException = null;
        startupManager.DisableException = null;
        await viewModel.RecheckStartupCommand.ExecuteAsync();
        await viewModel.ChangeStartupEnabledAsync(false);
        await viewModel.ChangeStartupEnabledAsync(true);

        Assert.Equal(latestProject, settingsStore.Settings.SelectedProject);
        Assert.Equal(AuditDetailLevel.Detailed, settingsStore.Settings.AuditDetailLevel);
        Assert.True(settingsStore.Settings.StartWithWindows);
    }

    [Fact]
    public async Task ChangeStartupEnabledCommand_WhenParameterIsNull_DoesNotMutateRegistry()
    {
        var settingsStore = new FakeSettingsStore(new PersistentSettings());
        var startupManager = new FakeStartupManager();
        var viewModel = new SettingsViewModel(
            settingsStore,
            new FakeAuditLog(),
            () => Task.FromResult(false),
            startupManager: startupManager);
        await viewModel.LoadAsync();

        await viewModel.ChangeStartupEnabledCommand.ExecuteAsync(null);

        Assert.Empty(startupManager.Operations);
        Assert.False(viewModel.StartupEnabled);
        Assert.False(settingsStore.Settings.StartWithWindows);
    }

    [Fact]
    public async Task RecheckStartupCommand_WhenRegistryReadFails_RemainsUnknownWithoutMutation()
    {
        var settingsStore = new FakeSettingsStore(new PersistentSettings());
        var startupManager = new FakeStartupManager { FailAllIsEnabledReads = true };
        var viewModel = new SettingsViewModel(
            settingsStore,
            new FakeAuditLog(),
            () => Task.FromResult(false),
            startupManager: startupManager);
        await viewModel.LoadAsync();

        await viewModel.RecheckStartupCommand.ExecuteAsync();

        Assert.Null(viewModel.StartupEnabled);
        Assert.False(viewModel.CanChangeStartup);
        Assert.True(viewModel.CanRecheckStartup);
        Assert.Equal(StringResources.Get("StartupStateUnknown"), viewModel.OperationMessage);
        Assert.Empty(startupManager.Operations);
    }

    [Fact]
    public async Task ChangeStartupEnabledCommand_WhilePersisting_DisablesSecondClickUntilFirstCompletes()
    {
        var settingsStore = new BlockingSettingsStore(new PersistentSettings());
        var startupManager = new FakeStartupManager();
        var viewModel = new SettingsViewModel(
            settingsStore,
            new FakeAuditLog(),
            () => Task.FromResult(false),
            startupManager: startupManager);
        await viewModel.LoadAsync();

        Task firstClick = viewModel.ChangeStartupEnabledCommand.ExecuteAsync(true);
        await settingsStore.SaveStarted.Task;
        Task secondClick = viewModel.ChangeStartupEnabledCommand.ExecuteAsync(true);

        Assert.False(viewModel.ChangeStartupEnabledCommand.CanExecute(true));
        Assert.Equal(["enable"], startupManager.Operations);
        settingsStore.ReleaseSave.SetResult();
        await Task.WhenAll(firstClick, secondClick);

        Assert.Equal(["enable"], startupManager.Operations);
        Assert.True(viewModel.StartupEnabled);
    }

    [Fact]
    public async Task InstallHookCommand_WhenStructuredDoctorIsUnhealthy_ShowsFirstFailedCheckNotCompleted()
    {
        var failedCheck = new DoctorCheck(
            "ClaudeStructuredDecisionSupported",
            DoctorSeverity.Error,
            "Claude Code compatibility could not be verified.");
        var maintenance = new FakeHookMaintenanceService(
            new HookOperationResult(HookOperationOutcome.Unhealthy, false, [failedCheck]));
        var viewModel = new SettingsViewModel(
            new FakeSettingsStore(new PersistentSettings()),
            new FakeAuditLog(),
            () => Task.FromResult(false),
            hookMaintenanceService: maintenance);

        await viewModel.InstallHookCommand.ExecuteAsync();

        Assert.Equal(
            "Hook 检查失败 [ClaudeStructuredDecisionSupported]：Claude Code compatibility could not be verified.",
            viewModel.OperationMessage);
        Assert.NotEqual(StringResources.Get("OperationCompleted"), viewModel.OperationMessage);
        Assert.Equal(1, maintenance.InstallCalls);
    }

    private sealed class FakeSettingsStore(PersistentSettings settings, List<string>? operations = null) : ISettingsStore
    {
        public PersistentSettings Settings { get; private set; } = settings;
        public Exception? SaveException { get; set; }

        public void Replace(PersistentSettings value) => Settings = value;

        public Task<PersistentSettings> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(Settings);

        public Task SaveAsync(PersistentSettings settings, CancellationToken cancellationToken)
        {
            if (SaveException is not null)
            {
                throw SaveException;
            }

            operations?.Add("save");
            Settings = settings;
            return Task.CompletedTask;
        }

        public async Task<PersistentSettings> UpdateAsync(
            Func<PersistentSettings, PersistentSettings> update,
            CancellationToken cancellationToken)
        {
            PersistentSettings updated = update(await LoadAsync(cancellationToken));
            await SaveAsync(updated, cancellationToken);
            return updated;
        }
    }

    private sealed class FakeAuditLog : IAuditLog
    {
        public bool ClearCalled { get; private set; }
        public bool DeleteDetailedCalled { get; private set; }

        public Task WriteAsync(ApprovalRequest request, ApprovalDecision decision, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<AuditRecord>> ReadRecentAsync(int maximumCount, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AuditRecord>>([]);

        public Task<int> CountAllowedAsync(
            DateTimeOffset startUtcInclusive,
            DateTimeOffset endUtcExclusive,
            CancellationToken cancellationToken) => Task.FromResult(0);

        public Task ClearAsync(CancellationToken cancellationToken)
        {
            ClearCalled = true;
            return Task.CompletedTask;
        }

        public Task DeleteDetailedAsync(CancellationToken cancellationToken)
        {
            DeleteDetailedCalled = true;
            return Task.CompletedTask;
        }

        public Task DeleteExpiredAsync(int retentionDays, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeStartupManager : IStartupManager
    {
        public FakeStartupManager(List<string>? operations = null) => Operations = operations ?? [];

        public List<string> Operations { get; }
        public Exception? EnableException { get; set; }
        public Exception? DisableException { get; set; }
        public int? ThrowOnIsEnabledCall { get; init; }
        public bool FailAllIsEnabledReads { get; init; }
        public bool Enabled { get; set; }
        private int isEnabledCalls;

        public bool IsEnabled()
        {
            if (FailAllIsEnabledReads || Interlocked.Increment(ref isEnabledCalls) == ThrowOnIsEnabledCall)
            {
                throw new IOException("registry read failed");
            }

            return Enabled;
        }

        public void Enable()
        {
            Operations.Add("enable");
            if (EnableException is not null)
            {
                throw EnableException;
            }

            Enabled = true;
        }

        public void Disable()
        {
            Operations.Add("disable");
            if (DisableException is not null)
            {
                throw DisableException;
            }

            Enabled = false;
        }
    }

    private sealed class BlockingSettingsStore(PersistentSettings settings) : ISettingsStore
    {
        public TaskCompletionSource SaveStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSave { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<PersistentSettings> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(settings);

        public async Task SaveAsync(PersistentSettings value, CancellationToken cancellationToken)
        {
            SaveStarted.SetResult();
            await ReleaseSave.Task;
            settings = value;
        }

        public async Task<PersistentSettings> UpdateAsync(
            Func<PersistentSettings, PersistentSettings> update,
            CancellationToken cancellationToken)
        {
            PersistentSettings updated = update(await LoadAsync(cancellationToken));
            await SaveAsync(updated, cancellationToken);
            return updated;
        }
    }

    private sealed class FakeHookMaintenanceService(HookOperationResult result)
        : IHookMaintenanceService
    {
        public event EventHandler<HookHealthSnapshot>? HealthChanged;
        public HookHealthSnapshot Current { get; private set; } = HookHealthSnapshot.Unknown;
        public int InstallCalls { get; private set; }

        public Task<HookOperationResult> InstallAsync(CancellationToken cancellationToken)
        {
            InstallCalls++;
            Current = new HookHealthSnapshot(result.IsOperational, result.Checks);
            HealthChanged?.Invoke(this, Current);
            return Task.FromResult(result);
        }

        public Task<HookOperationResult> DoctorAsync(CancellationToken cancellationToken) =>
            Task.FromResult(result);

        public Task<HookOperationResult> UninstallAsync(CancellationToken cancellationToken) =>
            Task.FromResult(result);

        public Task<HookHealthSnapshot> RefreshAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Current);
    }
}
