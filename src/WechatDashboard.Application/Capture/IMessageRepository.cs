using WechatDashboard.Domain.Entities;

namespace WechatDashboard.Application.Capture;

/// <summary>
/// 消息仓储接口：负责消息的持久化与查询。
/// 实现见 SqliteMessageRepository。
/// </summary>
public interface IMessageRepository
{
    /// <summary>判断指定来源+消息键是否已存在（用于去重）。</summary>
    Task<bool> ExistsAsync(string source, string sourceMessageKey, CancellationToken cancellationToken);

    /// <summary>保存一条消息并返回带 Id 的实体。</summary>
    Task<Message> SaveAsync(Message message, CancellationToken cancellationToken);

    /// <summary>获取最近 N 条消息（按采集时间倒序）。</summary>
    Task<IReadOnlyList<Message>> GetRecentAsync(int limit, CancellationToken cancellationToken);

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
