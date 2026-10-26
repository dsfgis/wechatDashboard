using WechatDashboard.Application.Capture;
using WechatDashboard.Domain.Entities;

namespace WechatDashboard.Application.Agent;

/// <summary>
/// 消息行动分析模型：自动待办与手动转待办共用同一摘要和优先级分析入口。
/// </summary>
public interface IMessageActionModel
{
    /// <summary>分析单条消息，返回创建必要性、优先级和可独立使用的中文说明。</summary>
    Task<MentionMessageAnalysis> AnalyzeMentionAsync(
        Message message,
        CancellationToken cancellationToken);
}
