using CCAutoApprove.App.ViewModels;
using CCAutoApprove.Core.Abstractions;
using CCAutoApprove.Core.Models;

namespace CCAutoApprove.App.Tests.ViewModels;

public sealed class SettingsViewModelTests
{
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
        await viewModel.LoadAsync();

        await viewModel.ChangeAuditDetailLevelAsync(AuditDetailLevel.PrivacySafe);

        Assert.False(auditLog.ClearCalled);
        Assert.Equal(AuditDetailLevel.PrivacySafe, settingsStore.Settings.AuditDetailLevel);
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
    public async Task LoadAsync_ExposesStartupAsReadOnlyDisabledControlState()
    {
        var settingsStore = new FakeSettingsStore(new PersistentSettings(StartWithWindows: true));
        var viewModel = new SettingsViewModel(settingsStore, new FakeAuditLog(), () => Task.FromResult(false));

        await viewModel.LoadAsync();

        Assert.True(viewModel.StartupEnabled);
        Assert.False(viewModel.CanChangeStartup);
    }

    private sealed class FakeSettingsStore(PersistentSettings settings) : ISettingsStore
    {
        public PersistentSettings Settings { get; private set; } = settings;

        public Task<PersistentSettings> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(Settings);

        public Task SaveAsync(PersistentSettings settings, CancellationToken cancellationToken)
        {
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

        public Task ClearAsync(CancellationToken cancellationToken)
        {
            ClearCalled = true;
            return Task.CompletedTask;
        }

        public Task DeleteExpiredAsync(int retentionDays, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
