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

    /// <summary>分页查询消息（按采集时间倒序）。</summary>
    Task<MessagePage> GetPageAsync(int pageNumber, int pageSize, CancellationToken cancellationToken);

    /// <summary>获取消息总数。</summary>
    Task<int> GetMessageCountAsync(CancellationToken cancellationToken);
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
