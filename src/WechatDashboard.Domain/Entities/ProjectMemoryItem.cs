using WechatDashboard.Domain.Enums;

namespace WechatDashboard.Domain.Entities;

/// <summary>
/// 可持续跨简报关联的项目事项。稳定 ID 由数据库生成，不能由模型指定。
/// </summary>
public sealed record ProjectMemoryItem(
    long Id,
    long ProjectId,
    ProjectMemoryCategory Category,
    string Title,
    string NormalizedTitle,
    ProjectMemoryStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset LastMentionedAt,
    string? LastStatusReason = null);

/// <summary>
/// 事项的来源证据。即使原始消息被删除，来源键和正文快照仍然保留。
/// </summary>
public sealed record ProjectMemorySource(
    long Id,
    long ItemId,
    long? MessageId,
    string Source,
    string SourceMessageKey,
    string ChatName,
    string SenderName,
    string ContentSnapshot,
    DateTimeOffset SentAt,
    DateTimeOffset CreatedAt);

/// <summary>项目事项的一次真实状态变化，状态不变时不会创建该记录。</summary>
public sealed record ProjectMemoryStatusChange(
    long Id,
    long ItemId,
    ProjectMemoryStatus? PreviousStatus,
    ProjectMemoryStatus NewStatus,
    string Reason,
    long? EvidenceMessageId,
    ProjectMemoryChangeSource ChangeSource,
    DateTimeOffset ChangedAt);

/// <summary>项目记忆标题归一化规则，供简报服务和 SQLite 仓储共同使用。</summary>
public static class ProjectMemoryTitle
{
    /// <summary>折叠空白并转为固定小写，避免同一活跃事项因排版差异重复创建。</summary>
    public static string Normalize(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("项目记忆标题不能为空。", nameof(title));
        }

        return string.Join(' ', title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToLowerInvariant();
    }
}
