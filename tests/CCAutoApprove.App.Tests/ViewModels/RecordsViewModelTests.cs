using System.Text.Json;
using CCAutoApprove.App.ViewModels;
using CCAutoApprove.Core.Abstractions;
using CCAutoApprove.Core.Models;

namespace CCAutoApprove.App.Tests.ViewModels;

public sealed class RecordsViewModelTests
{
    [Fact]
    public async Task LoadAsync_RequestsAtMostTwoHundredNewestRecords()
    {
        var auditLog = new FakeAuditLog(CreateRecords(250));
        var viewModel = new RecordsViewModel(auditLog, () => Task.FromResult(true));

        await viewModel.LoadAsync();

        Assert.Equal(200, auditLog.LastMaximumCount);
        Assert.Equal(200, viewModel.Records.Count);
        Assert.Equal(
            new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.Zero).AddMinutes(249),
            viewModel.Records[0].TimeUtc);
    }

    [Fact]
    public async Task ClearCommand_WhenConfirmationReturnsTrue_DeletesAndRefreshesRecords()
    {
        var auditLog = new FakeAuditLog(CreateRecords(2));
        var viewModel = new RecordsViewModel(auditLog, () => Task.FromResult(true));
        await viewModel.LoadAsync();

        await viewModel.ClearCommand.ExecuteAsync();

        Assert.True(auditLog.ClearCalled);
        Assert.Empty(viewModel.Records);
    }

    [Fact]
    public async Task ClearCommand_WhenConfirmationReturnsFalse_PreservesRecords()
    {
        var auditLog = new FakeAuditLog(CreateRecords(2));
        var viewModel = new RecordsViewModel(auditLog, () => Task.FromResult(false));
        await viewModel.LoadAsync();

        await viewModel.ClearCommand.ExecuteAsync();

        Assert.False(auditLog.ClearCalled);
        Assert.Equal(2, viewModel.Records.Count);
    }

    [Fact]
    public async Task SelectedRecord_ExposesDetailFieldsOnlyInDetailedMode()
    {
        AuditRecord detailed = CreateRecord(1, detailed: true);
        var detailedViewModel = new RecordsViewModel(
            new FakeAuditLog([detailed]),
            () => Task.FromResult(true),
            AuditDetailLevel.Detailed);
        await detailedViewModel.LoadAsync();
        detailedViewModel.SelectedRecord = detailedViewModel.Records[0];

        var privacyViewModel = new RecordsViewModel(
            new FakeAuditLog([detailed]),
            () => Task.FromResult(true),
            AuditDetailLevel.PrivacySafe);
        await privacyViewModel.LoadAsync();
        privacyViewModel.SelectedRecord = privacyViewModel.Records[0];

        Assert.True(detailedViewModel.ShowDetails);
        Assert.Equal("session-1", detailedViewModel.SelectedSessionId);
        Assert.False(privacyViewModel.ShowDetails);
        Assert.Null(privacyViewModel.SelectedSessionId);
    }

    [Fact]
    public async Task SetAuditDetailLevel_EnteringAndLeavingDetailed_UpdatesVisibleDetails()
    {
        AuditRecord detailed = CreateRecord(1, detailed: true);
        var viewModel = new RecordsViewModel(
            new FakeAuditLog([detailed]),
            () => Task.FromResult(false),
            AuditDetailLevel.PrivacySafe);
        await viewModel.LoadAsync();

        Assert.False(viewModel.ShowDetails);

        viewModel.SetAuditDetailLevel(AuditDetailLevel.Detailed);

        Assert.True(viewModel.ShowDetails);
        Assert.Equal("session-1", viewModel.SelectedSessionId);

        viewModel.SetAuditDetailLevel(AuditDetailLevel.PrivacySafe);

        Assert.False(viewModel.ShowDetails);
        Assert.Null(viewModel.SelectedSessionId);
    }

    [Fact]
    public async Task CountTodayApprovalsAsync_MoreThanDisplayLimit_CountsAllTodayApprovals()
    {
        var today = new DateOnly(2026, 8, 20);
        AuditRecord[] records = Enumerable.Range(0, 250)
            .Select(index => CreateCountRecord(today, index, ApprovalDecisionKind.Allow))
            .Concat(Enumerable.Range(250, 10)
                .Select(index => CreateCountRecord(today, index, ApprovalDecisionKind.Deny)))
            .Concat(Enumerable.Range(260, 5)
                .Select(index => CreateCountRecord(today.AddDays(-1), index, ApprovalDecisionKind.Allow)))
            .ToArray();
        var auditLog = new FakeAuditLog(records);
        var viewModel = new RecordsViewModel(auditLog, () => Task.FromResult(false));

        int count = await viewModel.CountTodayApprovalsAsync(today);

        Assert.Equal(250, count);
        Assert.Equal(int.MaxValue, auditLog.LastMaximumCount);
    }

    [Theory]
    [InlineData(ApprovalDecisionKind.Allow, "允许")]
    [InlineData(ApprovalDecisionKind.Deny, "拒绝")]
    [InlineData(ApprovalDecisionKind.Ask, "询问")]
    public void AuditRecordItemViewModel_LocalizesDecision(
        ApprovalDecisionKind decision,
        string expected)
    {
        AuditRecord record = CreateCountRecord(new DateOnly(2026, 8, 20), 1, decision);

        var item = new AuditRecordItemViewModel(record);

        Assert.Equal(expected, item.DecisionText);
        Assert.NotEqual(decision.ToString(), item.DecisionText);
    }

    [Theory]
    [InlineData(DecisionSource.LocalAlwaysAllow, "本地始终允许")]
    [InlineData(DecisionSource.LocalRule, "本地规则")]
    [InlineData(DecisionSource.AI, "人工智能")]
    [InlineData(DecisionSource.RemotePhone, "远程手机")]
    [InlineData(DecisionSource.HumanDesktop, "桌面人工确认")]
    public void AuditRecordItemViewModel_LocalizesSource(
        DecisionSource source,
        string expected)
    {
        AuditRecord record = CreateCountRecord(new DateOnly(2026, 8, 20), 1, ApprovalDecisionKind.Allow)
            with { Source = source };

        var item = new AuditRecordItemViewModel(record);

        Assert.Equal(expected, item.SourceText);
        Assert.NotEqual(source.ToString(), item.SourceText);
    }

    private static IReadOnlyList<AuditRecord> CreateRecords(int count) =>
        Enumerable.Range(0, count).Select(index => CreateRecord(index, detailed: false)).ToArray();

    private static AuditRecord CreateRecord(int index, bool detailed)
    {
        JsonElement? toolInput = detailed ? JsonDocument.Parse("{\"command\":\"build\"}").RootElement.Clone() : null;
        return new AuditRecord(
            new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.Zero).AddMinutes(index),
            new Guid(index, 0, 0, new byte[8]),
            @"D:\projects\sample",
            "Bash",
            ApprovalDecisionKind.Allow,
            DecisionSource.LocalAlwaysAllow,
            detailed ? $"session-{index}" : null,
            detailed ? "default" : null,
            toolInput);
    }

    private static AuditRecord CreateCountRecord(
        DateOnly localDate,
        int index,
        ApprovalDecisionKind decision)
    {
        DateTime localTime = localDate.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Unspecified);
        DateTimeOffset timeUtc = new DateTimeOffset(localTime, TimeZoneInfo.Local.GetUtcOffset(localTime))
            .ToUniversalTime()
            .AddSeconds(index);
        return new AuditRecord(
            timeUtc,
            new Guid(index, 0, 0, new byte[8]),
            @"D:\projects\sample",
            "Bash",
            decision,
            DecisionSource.LocalAlwaysAllow);
    }

    private sealed class FakeAuditLog(IReadOnlyList<AuditRecord> records) : IAuditLog
    {
        private readonly List<AuditRecord> records = records.ToList();

        public int LastMaximumCount { get; private set; }
        public bool ClearCalled { get; private set; }

        public Task WriteAsync(ApprovalRequest request, ApprovalDecision decision, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<AuditRecord>> ReadRecentAsync(int maximumCount, CancellationToken cancellationToken)
        {
            LastMaximumCount = maximumCount;
            IReadOnlyList<AuditRecord> result = records
                .OrderByDescending(record => record.TimeUtc)
                .Take(maximumCount)
                .ToArray();
            return Task.FromResult(result);
        }

        public Task ClearAsync(CancellationToken cancellationToken)
        {
            ClearCalled = true;
            records.Clear();
            return Task.CompletedTask;
        }

        public Task DeleteExpiredAsync(int retentionDays, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
