using WechatDashboard.Domain.Entities;
using WechatDashboard.Domain.Enums;

namespace WechatDashboard.Application.Briefing;

/// <summary>
/// 一次简报对项目记忆提出的新增或状态变化建议。
/// ItemId 为空表示新事项；既有事项必须携带应用时用于并发校验的基准状态。
/// </summary>
public sealed record ProjectMemoryProposal(
    long? ItemId,
    ProjectMemoryCategory Category,
    string Title,
    ProjectMemoryStatus? BaseStatus,
    ProjectMemoryStatus ProposedStatus,
    string Reason,
    IReadOnlyList<long> SourceMessageIds);

/// <summary>项目事项及其完整来源快照、状态时间线。</summary>
public sealed record ProjectMemoryDetails(
    ProjectMemoryItem Item,
    IReadOnlyList<ProjectMemorySource> Sources,
    IReadOnlyList<ProjectMemoryStatusChange> History);

/// <summary>批量应用后的实际写入统计，跳过项包含状态未变化且没有新增来源的建议。</summary>
public sealed record ProjectMemoryApplyResult(
    int Created,
    int Updated,
    int Skipped,
    IReadOnlyList<long> ItemIds);

/// <summary>项目记忆持久化边界；批量应用必须在一个 SQLite 事务中完成。</summary>
public interface IProjectMemoryRepository
{
    /// <summary>按项目读取全部事项，供简报关联和界面筛选使用。</summary>
    Task<IReadOnlyList<ProjectMemoryItem>> GetByProjectAsync(
        long projectId,
        CancellationToken cancellationToken);

    /// <summary>读取单个事项的来源快照和状态历史。</summary>
    Task<ProjectMemoryDetails?> GetDetailsAsync(
        long projectId,
        long itemId,
        CancellationToken cancellationToken);

    /// <summary>
    /// 在一个事务中应用已确认建议，并重新校验项目归属、基准状态和活跃标题去重。
    /// 原始消息由应用服务按来源 ID 重新读取后传入，确保证据快照真实可靠。
    /// </summary>
    Task<ProjectMemoryApplyResult> ApplyAsync(
        long projectId,
        IReadOnlyList<ProjectMemoryProposal> proposals,
        IReadOnlyDictionary<long, Message> sourceMessages,
        ProjectMemoryChangeSource changeSource,
        DateTimeOffset appliedAt,
        CancellationToken cancellationToken);
}

/// <summary>确认窗口已经过期或事项被并发修改时抛出，调用方应刷新后重新确认。</summary>
public sealed class ProjectMemoryConcurrencyException : InvalidOperationException
{
    public ProjectMemoryConcurrencyException(string message) : base(message)
    {
    }
}
