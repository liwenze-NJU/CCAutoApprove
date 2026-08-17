namespace CCAutoApprove.Core.Models;

public sealed record PersistentSettings(
    int SchemaVersion = 1,
    string? SelectedProject = null,
    bool StartWithWindows = false,
    string Language = "zh-CN",
    AuditDetailLevel AuditDetailLevel = AuditDetailLevel.PrivacySafe,
    int AuditRetentionDays = 7);
