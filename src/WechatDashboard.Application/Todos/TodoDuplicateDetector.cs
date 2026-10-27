using WechatDashboard.Application.Agent;
using WechatDashboard.Application.Capture;
using WechatDashboard.Domain.Entities;
using WechatDashboard.Domain.Enums;

namespace WechatDashboard.Application.Todos;

/// <summary>
/// 跨群/跨消息待办去重：用 Agent 语义判断新消息是否在重复要求某条活动待办所描述的事项。
/// 已完成或已忽略的待办不参与匹配，避免把新提醒挂到历史记录上。
/// </summary>
public sealed class TodoDuplicateDetector
{
    // 只把最近的少量活动待办送进模型，控制提示词长度与单次调用成本。
    private const int MaxCandidates = 30;

    private readonly ITodoRepository _todos;
    private readonly IMessageActionModel _model;

    public TodoDuplicateDetector(ITodoRepository todos, IMessageActionModel model)
    {
        _todos = todos;
        _model = model;
    }

    /// <summary>
    /// 命中"同一件事"时返回既有待办；没有候选、未命中或模型失败一律返回 null（按新待办处理）。
    /// </summary>
    public async Task<TodoItem?> DetectAsync(Message message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        try
        {
            var active = await _todos.GetActiveAsync(cancellationToken);
            var candidates = active
                .Where(todo => todo.Status is TodoStatus.Pending or TodoStatus.InProgress or TodoStatus.Waiting)
                .OrderByDescending(todo => todo.UpdatedAt)
                .Take(MaxCandidates)
                .ToArray();
            if (candidates.Length == 0)
            {
                return null;
            }

            var matchedId = await _model.MatchDuplicateTodoAsync(message, candidates, cancellationToken);
            return matchedId is { } id
                ? candidates.FirstOrDefault(todo => todo.Id == id)
                : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 主动取消必须继续传播，不能伪装成"未命中"。
            throw;
        }
        catch (Exception)
        {
            // 去重是增强能力，模型或数据访问失败时按新待办处理，不阻断采集。
            return null;
        }
    }
}
