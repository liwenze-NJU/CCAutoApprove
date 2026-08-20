using CCAutoApprove.App.ViewModels;
using CCAutoApprove.App;
using CCAutoApprove.Core.Abstractions;
using CCAutoApprove.Core.Models;

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
        Assert.True(auditLog.ClearCalled);
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
    public async Task ChangeAuditDetailLevelAsync_WhenNotLeavingDetailed_DoesNotPrompt()
    {
        var settingsStore = new FakeSettingsStore(new PersistentSettings(AuditDetailLevel: AuditDetailLevel.PrivacySafe));
        var auditLog = new FakeAuditLog();
        int promptCount = 0;
        var viewModel = new SettingsViewModel(settingsStore, auditLog, () =>
        {
            promptCount++;
            return Task.FromResult(true);
        });
        await viewModel.LoadAsync();

        await viewModel.ChangeAuditDetailLevelAsync(AuditDetailLevel.Detailed);

        Assert.Equal(0, promptCount);
        Assert.False(auditLog.ClearCalled);
        Assert.Equal(AuditDetailLevel.Detailed, settingsStore.Settings.AuditDetailLevel);
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

        await viewModel.ChangeStartupEnabledAsync(true);

        Assert.False(viewModel.StartupEnabled);
        Assert.False(settingsStore.Settings.StartWithWindows);
        Assert.Equal("Registry is unavailable.", viewModel.OperationMessage);
        Assert.Equal(["enable"], startupManager.Operations);
    }

    private sealed class FakeSettingsStore(PersistentSettings settings, List<string>? operations = null) : ISettingsStore
    {
        public PersistentSettings Settings { get; private set; } = settings;

        public Task<PersistentSettings> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(Settings);

        public Task SaveAsync(PersistentSettings settings, CancellationToken cancellationToken)
        {
            operations?.Add("save");
            Settings = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAuditLog : IAuditLog
    {
        public bool ClearCalled { get; private set; }

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

        public Task DeleteExpiredAsync(int retentionDays, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeStartupManager : IStartupManager
    {
        public FakeStartupManager(List<string>? operations = null) => Operations = operations ?? [];

        public List<string> Operations { get; }
        public Exception? EnableException { get; init; }
        public bool Enabled { get; private set; }

        public bool IsEnabled() => Enabled;

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
            Enabled = false;
        }
    }
}
