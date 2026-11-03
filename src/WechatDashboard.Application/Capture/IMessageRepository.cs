using WechatDashboard.Domain.Entities;
using WechatDashboard.Domain.Enums;

namespace WechatDashboard.Application.Capture;

/// <summary>
/// 消息仓储接口：负责消息的持久化与查询。
/// 实现见 SqliteMessageRepository。
/// </summary>
public interface IMessageRepository
{
    /// <summary>判断指定来源+消息键是否已存在（用于去重）。</summary>
    Task<bool> ExistsAsync(string source, string sourceMessageKey, CancellationToken cancellationToken);

    /// <summary>
    /// 重复采集时补充路由元数据；只有旧记录的会话类型和方向都为 Unknown 才允许更新，
    /// 已知路由绝不覆盖，也不触碰正文、分类、评分或待办。
    /// </summary>
    /// <returns>实际补充了元数据时返回 true。</returns>
    Task<bool> UpdateRoutingMetadataIfUnknownAsync(
        string source,
        string sourceMessageKey,
        string? senderExternalId,
        ChatType chatType,
        Direction direction,
        CancellationToken cancellationToken);

    /// <summary>保存一条消息并返回带 Id 的实体。</summary>
    Task<Message> SaveAsync(Message message, CancellationToken cancellationToken);

    /// <summary>获取最近 N 条消息（按采集时间倒序）。</summary>
    Task<IReadOnlyList<Message>> GetRecentAsync(int limit, CancellationToken cancellationToken);

    /// <summary>获取发送时间落在 [from, to] 区间内的消息（按发送时间倒序、限量），用于跨源项目简报取数。</summary>
    Task<IReadOnlyList<Message>> GetSentBetweenAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>按 sent_at、id 稳定分页读取完整闭区间 [from, to]，不限制窗口内消息总数。</summary>
    Task<IReadOnlyList<Message>> GetAllSentBetweenAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken);

    /// <summary>按消息 ID 集合精确查询消息。</summary>
    Task<IReadOnlyList<Message>> GetByIdsAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken);

    /// <summary>读取每条消息最新的紧急度优先级；没有评分记录的消息返回 P3。</summary>
    Task<IReadOnlyDictionary<long, PriorityLevel>> GetEffectivePrioritiesAsync(
        IReadOnlyCollection<long> messageIds,
        CancellationToken cancellationToken);

    /// <summary>按来源与来源消息键批量解析规范化数据库消息。</summary>
    Task<IReadOnlyList<Message>> GetBySourceKeysAsync(
        IReadOnlyCollection<MessageIdentity> identities,
        CancellationToken cancellationToken);

    /// <summary>按数据库主键获取一条原始消息。</summary>
    Task<Message?> GetByIdAsync(long id, CancellationToken cancellationToken);

    /// <summary>按锚点消息 ID 获取前后上下文，结果保持消息流的 newest-first 顺序。</summary>
    Task<MessageContext?> GetContextAsync(long messageId, int before, int after, CancellationToken cancellationToken);

    /// <summary>分页查询消息（按发送时间倒序）。</summary>
    Task<MessagePage> GetPageAsync(int pageNumber, int pageSize, CancellationToken cancellationToken);

    /// <summary>分页查询消息，并按指定群名过滤（仅返回关注群的消息）。</summary>
    Task<MessagePage> GetPageAsync(int pageNumber, int pageSize, IReadOnlyCollection<string> chatNames, CancellationToken cancellationToken);

    /// <summary>分页查询消息，并按指定群名过滤（include=true 时仅返回列表内群，false 时排除列表内群）。</summary>
    Task<MessagePage> GetPageAsync(int pageNumber, int pageSize, IReadOnlyCollection<string> chatNames, bool include, CancellationToken cancellationToken);

    /// <summary>分页查询消息，但跳过 COUNT 查询（总数由调用方传入）。</summary>
    Task<MessagePage> GetPageWithKnownCountAsync(int pageNumber, int pageSize, int totalCount, CancellationToken cancellationToken);

    /// <summary>分页查询消息并按指定群名过滤，跳过 COUNT 查询（总数由调用方传入）。include=true 时仅返回列表内群，false 时排除列表内群。</summary>
    Task<MessagePage> GetPageWithKnownCountAsync(int pageNumber, int pageSize, int totalCount, IReadOnlyCollection<string> chatNames, bool include, CancellationToken cancellationToken);

    /// <summary>获取消息总数。</summary>
    Task<int> GetMessageCountAsync(CancellationToken cancellationToken);

    /// <summary>获取指定群名集合内的消息总数。include=true 时统计列表内群，false 时统计列表外群。</summary>
    Task<int> GetMessageCountAsync(IReadOnlyCollection<string> chatNames, bool include, CancellationToken cancellationToken);
}

/// <summary>
/// 消息分页结果。
/// </summary>
/// <param name="Messages">当前页消息列表。</param>
/// <param name="TotalCount">符合条件的消息总数。</param>
/// <param name="PageNumber">当前页码（从 1 开始）。</param>
/// <param name="PageSize">每页大小。</param>
public sealed record MessagePage(
    IReadOnlyList<Message> Messages,
    int TotalCount,
    int PageNumber,
    int PageSize);

public sealed record MessageContext(Message Anchor, IReadOnlyList<Message> Messages);

public sealed record MessageIdentity(string Source, string SourceMessageKey);
