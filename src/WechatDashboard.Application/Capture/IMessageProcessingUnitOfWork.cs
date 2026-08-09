using WechatDashboard.Domain.Entities;

namespace WechatDashboard.Application.Capture;

/// <summary>
/// 将单条消息、分类结果、紧急度结果及自动待办作为一个原子工作单元持久化。
/// 实现必须使用来源消息唯一键完成最终去重，并在任一结果写入失败时回滚整条消息。
/// </summary>
public interface IMessageProcessingUnitOfWork
{
    /// <summary>
    /// 尝试处理一条消息。仅在消息成功插入后调用 <paramref name="createArtifacts"/>，
    /// 由应用层生成分类、紧急度和可选自动待办。
    /// </summary>
    Task<MessageProcessingWriteResult> TryProcessAsync(
        Message message,
        Func<Message, MessageProcessingArtifacts> createArtifacts,
        CancellationToken cancellationToken);
}

/// <summary>消息写入后由应用层计算的关联结果。</summary>
/// <param name="Classification">项目与类别分类结果。</param>
/// <param name="Urgency">紧急度评分结果。</param>
/// <param name="Todo">需要自动创建的待办；普通消息为空。</param>
public sealed record MessageProcessingArtifacts(
    ClassificationResult Classification,
    UrgencyScore Urgency,
    TodoItem? Todo);

/// <summary>单条消息原子写入的结果。</summary>
/// <param name="Persisted">消息是否为本次新写入；false 表示命中唯一键去重。</param>
/// <param name="CreatedTodo">是否在同一事务中创建了待办。</param>
/// <param name="SavedMessage">本次新写入并带数据库 ID 的消息；重复消息为空。</param>
public sealed record MessageProcessingWriteResult(
    bool Persisted,
    bool CreatedTodo,
    Message? SavedMessage);
